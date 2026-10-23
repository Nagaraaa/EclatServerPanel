using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace EclatServerPanel;

/// <summary>Explicit, endpoint-scoped OpenSSH trust. No network discovery implies trust.</summary>
public static class KnownHostsTrust
{
    private const int MaxKnownHostsBytes = 1024 * 1024;

    public static bool HasTrustedHost(ConnectionSettings settings, string? knownHostsPath = null)
    {
        try
        {
            settings.Validate(false);
            var lines = ReadLines(knownHostsPath ?? ConnectionSettings.KnownHostsPath);
            bool trusted = false;
            foreach (var line in lines)
            {
                var fields = Fields(line);
                if (fields.Length < 3) continue;
                bool marker = fields[0].StartsWith('@');
                string hosts = fields[marker ? 1 : 0];
                if (!hosts.Split(',').Any(host => Matches(host, settings))) continue;
                if (marker) return false; // Revocations and certificates require explicit operator inspection.
                if (fields[1] == "ssh-ed25519" && ValidEd25519(fields[2])) trusted = true;
            }
            return trusted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException) { return false; }
    }

    public static (string KeyLine, string Fingerprint) NormalizeScannedKey(ConnectionSettings settings, string output)
    {
        settings.Validate(false);
        if (output.Length > 65536) throw new InvalidOperationException("La réponse de lecture de clé SSH est trop longue.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var fields = Fields(line);
            if (fields.Length == 0) continue;
            if (fields.Length != 3 || fields[0].Split(',').Any(host => !MatchesPlain(host, settings)) || fields[1] != "ssh-ed25519" || !ValidEd25519(fields[2])) throw new InvalidOperationException("La clé reçue ne correspond pas à l’adresse et au port demandés.");
            keys.Add(fields[2]);
        }
        if (keys.Count != 1) throw new InvalidOperationException("Aucune clé unique n’a été reçue. Vérifiez l’adresse et l’identité du serveur auprès de l’hébergeur.");
        string key = keys.Single();
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(key))).TrimEnd('=');
        return ($"{settings.EndpointIdentity} ssh-ed25519 {key}\n", fingerprint + " (ED25519)");
    }

    /// <summary>Call only after the human has compared and accepted this exact scanned endpoint/key.</summary>
    public static void SaveTrustedKey(ConnectionSettings settings, string keyLine, string? knownHostsPath = null)
    {
        settings = settings.Snapshot();
        var normalized = NormalizeScannedKey(settings, keyLine);
        string path = Path.GetFullPath(knownHostsPath ?? ConnectionSettings.KnownHostsPath);
        var result = new List<string>();
        foreach (var line in ReadLines(path))
        {
            var fields = Fields(line);
            if (fields.Length < 3) { result.Add(line); continue; }
            int hostIndex = fields[0].StartsWith('@') ? 1 : 0;
            var hosts = fields[hostIndex].Split(',');
            if (!hosts.Any(host => Matches(host, settings))) { result.Add(line); continue; }
            var remaining = hosts.Where(host => !Matches(host, settings)).ToArray();
            if (remaining.Length > 0)
            {
                fields[hostIndex] = string.Join(',', remaining);
                result.Add(string.Join(' ', fields));
            }
        }
        result.Add(normalized.KeyLine.TrimEnd('\n'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllLines(temporary, result, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string[] ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaxKnownHostsBytes) throw new InvalidOperationException("Le fichier d’empreintes SSH est trop volumineux.");
        return File.ReadAllLines(path);
    }
    private static string[] Fields(string line)
    {
        line = line.Trim();
        return line.Length == 0 || line.StartsWith('#') ? [] : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
    private static bool Matches(string host, ConnectionSettings settings)
    {
        if (!host.StartsWith('|')) return MatchesPlain(host, settings);
        var fields = host.Split('|');
        if (fields.Length != 4 || fields[1] != "1") return false;
        try
        {
            var salt = Convert.FromBase64String(fields[2]);
            var expected = Convert.FromBase64String(fields[3]);
            if (salt.Length != 20 || expected.Length != 20) return false;
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(settings.EndpointIdentity));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
    private static bool MatchesPlain(string value, ConnectionSettings settings)
    {
        string host = value;
        int port = 22;
        if (value.StartsWith('['))
        {
            int separator = value.LastIndexOf("]:", StringComparison.Ordinal);
            if (separator <= 1 || !int.TryParse(value[(separator + 2)..], out port)) return false;
            host = value[1..separator];
        }
        if (port != settings.Port) return false;
        if (IPAddress.TryParse(host, out var address) && IPAddress.TryParse(settings.Host, out var requested)) return address.Equals(requested);
        return string.Equals(host, settings.CanonicalHost, StringComparison.OrdinalIgnoreCase);
    }
    private static bool ValidEd25519(string encoded)
    {
        try
        {
            var key = Convert.FromBase64String(encoded);
            return key.Length == 51 && BinaryPrimitives.ReadUInt32BigEndian(key) == 11 && Encoding.ASCII.GetString(key, 4, 11) == "ssh-ed25519" && BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(15, 4)) == 32;
        }
        catch (FormatException) { return false; }
    }
}
