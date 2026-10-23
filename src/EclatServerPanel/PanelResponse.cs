using System.Text.Json;
using System.Text.RegularExpressions;

namespace EclatServerPanel;

internal static class PanelResponse
{
    internal const int MaxDownloadBytes = 64 * 1024 * 1024;
    private const int MaxEncodedDownload = ((MaxDownloadBytes + 2) / 3) * 4;
    private static readonly Regex SnapshotName = new(@"\Aeclat-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}\.tar\.gz\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);

    internal static JsonElement Parse(string text, string action, string? requestedName)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object);
            CheckUniqueProperties(root);
            var ok = Property(root, "ok");
            Require(ok.ValueKind is JsonValueKind.True or JsonValueKind.False);
            if (ok.ValueKind == JsonValueKind.False)
            {
                var error = Property(root, "error");
                Require(error.ValueKind == JsonValueKind.String);
                throw new InvalidOperationException(SafeError(error.GetString()!));
            }
            var data = Property(root, "data");
            Require(data.ValueKind == JsonValueKind.Object);
            switch (action)
            {
                case "status": ValidateStatus(data); break;
                case "monitor": ValidateService(Property(data, "service")); Require(Property(data, "monitor").ValueKind == JsonValueKind.Object); break;
                case "diagnostics":
                    ValidateStatus(Property(data, "status"));
                    Require(String(data, "journal").Length <= 131072);
                    NonNegative(data, "backupCount");
                    break;
                case "logs": Require(String(data, "text").Length <= 131072); break;
                case "close-room": Require(Property(data, "closed").ValueKind == JsonValueKind.True && String(data, "roomId") == requestedName); break;
                case "start": case "stop": case "restart": ValidateService(Property(data, "service")); break;
                case "backups":
                    var items = Property(data, "items");
                    Require(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 10000);
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in items.EnumerateArray())
                    {
                        string name = String(item, "name"); ValidateSnapshotName(name); Require(names.Add(name));
                        NonNegative(item, "size");
                        Require(DateTimeOffset.TryParse(String(item, "modifiedUtc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _));
                    }
                    break;
                case "backup":
                    ValidateSnapshotName(String(data, "name")); NonNegative(data, "size");
                    Require(String(data, "integrity") == "ok" && Sha256.IsMatch(String(data, "sha256")));
                    break;
                case "download":
                    ValidateDownload(data);
                    Require(requestedName is not null && String(data, "name") == requestedName[..^7] + ".sqlite");
                    break;
                case "restore":
                    Require(Property(data, "restored").ValueKind == JsonValueKind.True && String(data, "name") == requestedName && String(data, "integrity") == "ok");
                    ValidateSnapshotName(String(data, "rollbackBackup"));
                    break;
                default: throw new InvalidOperationException("Action inconnue.");
            }
            return data.Clone();
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (KeyNotFoundException) { throw InvalidResponse(); }
    }

    internal static void ValidateDownload(JsonElement data)
    {
        string encoded = String(data, "base64");
        Require(encoded.Length is > 0 and <= MaxEncodedDownload && encoded.Length % 4 == 0);
        int padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
        Require((long)encoded.Length / 4 * 3 - padding <= MaxDownloadBytes);
        Require(Sha256.IsMatch(String(data, "sha256")));
        for (int i = 0; i < encoded.Length; i++)
        {
            char c = encoded[i];
            bool alphabet = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/';
            Require(alphabet || c == '=' && i >= encoded.Length - 2);
        }
    }

    internal static void ValidateSnapshotName(string? name)
    {
        if (name is null || name.Length > 80 || !SnapshotName.IsMatch(name)) throw new InvalidOperationException("Le nom de sauvegarde est invalide. Actualisez la liste avant de réessayer.");
    }
    private static void ValidateStatus(JsonElement data)
    {
        Require(data.ValueKind == JsonValueKind.Object);
        ValidateService(Property(data, "service"));
        var protocol = Property(data, "protocol");
        bool healthy = Property(protocol, "ok").ValueKind == JsonValueKind.True;
        Require(Property(protocol, "ok").ValueKind is JsonValueKind.True or JsonValueKind.False);
        if (healthy)
        {
            var latency = Property(protocol, "latencyMs");
            Require(latency.ValueKind == JsonValueKind.Number && latency.TryGetDouble(out var milliseconds) && double.IsFinite(milliseconds) && milliseconds >= 0);
        }
        foreach (string key in new[] { "monitor", "metrics", "network", "host" })
            if (data.TryGetProperty(key, out var value)) Require(value.ValueKind == JsonValueKind.Object);
    }
    private static void ValidateService(JsonElement service)
    {
        Require(service.ValueKind == JsonValueKind.Object);
        Require(String(service, "active").Length is > 0 and <= 32 && String(service, "sub").Length is > 0 and <= 64);
        NonNegative(service, "pid");
    }
    private static void NonNegative(JsonElement data, string key)
    {
        var value = Property(data, key);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0);
    }
    private static JsonElement Property(JsonElement data, string key)
    {
        Require(data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out _));
        return data.GetProperty(key);
    }
    private static string String(JsonElement data, string key)
    {
        var value = Property(data, key);
        Require(value.ValueKind == JsonValueKind.String);
        return value.GetString()!;
    }
    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { Require(names.Add(property.Name)); CheckUniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckUniqueProperties(item);
    }
    private static void Require(bool condition) { if (!condition) throw InvalidResponse(); }
    private static InvalidOperationException InvalidResponse() => new("La réponse du serveur est incomplète ou invalide. Aucune réussite de l’action n’a été confirmée.");

    internal static string SafeError(string message)
    {
        // Errors can cross an SSH boundary too. Avoid presenting raw credentials or terminal controls.
        if (message.Length > 65536) message = message[..65536];
        message = Regex.Replace(message, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|\z)", "[clé privée masquée]", RegexOptions.IgnoreCase);
        message = Regex.Replace(message, @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[masqué]@");
        message = Regex.Replace(message, @"(?i)(\b(?:password|passwd|secret|token|authorization|credential|publish[_-]?key|api[_-]?key|private[_-]?key)[\w-]*[\s""']*[:=][\s""']*)[^\s,;""'}]+", "$1[masqué]");
        message = Regex.Replace(message, @"(?i)([?&](?:token|key|secret|password|auth)=)[^&\s]+", "$1[masqué]");
        message = Regex.Replace(message, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [masqué]");
        message = Regex.Replace(message, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\z))", "");
        message = new string(message.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
        return string.IsNullOrEmpty(message) ? "Le serveur a refusé l’action." : message[..Math.Min(message.Length, 1400)];
    }
}
