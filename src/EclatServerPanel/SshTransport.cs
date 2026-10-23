using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EclatServerPanel;

public interface IPanelTransport
{
    Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null);
}

public sealed class SshTransport : IPanelTransport
{
    private static readonly HashSet<string> Actions = ["status", "logs", "start", "stop", "restart", "backup", "backups", "download", "restore", "diagnostics"];
    public async Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null)
    {
        settings.Validate();
        if (!Actions.Contains(action)) throw new InvalidOperationException("Action inconnue.");
        if (!File.Exists(ConnectionSettings.KnownHostsPath)) throw new InvalidOperationException("Vérifiez et enregistrez d’abord l’empreinte du serveur dans Connexion.");
        var args = new List<string> { "-T", "-p", settings.Port.ToString(), "-i", Path.GetFullPath(settings.KeyPath), "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + ConnectionSettings.KnownHostsPath.Replace('\\', '/'), "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2", "-o", "LogLevel=ERROR", "-l", settings.User, settings.Host, "panel" };
        var request = JsonSerializer.Serialize(new { action, name });
        int timeout = action is "backup" or "restore" or "download" ? 120 : 35;
        var output = await ProcessRunner.RunAsync("ssh.exe", args, request + "\n", TimeSpan.FromSeconds(timeout));
        if (output.Code != 0)
        {
            var remoteError = ParseRemoteError(output.Text);
            if (remoteError is not null) throw new InvalidOperationException(remoteError);
            var reason = output.Error.Trim();
            if (reason.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || reason.Contains("REMOTE HOST IDENTIFICATION", StringComparison.OrdinalIgnoreCase)) reason = "L’empreinte SSH ne correspond pas. Vérifiez-la auprès de l’hébergeur avant toute nouvelle confiance.";
            else if (reason.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) reason = "Authentification refusée. Vérifiez le compte, la clé dédiée et son chargement dans l’agent SSH si elle est chiffrée.";
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? "La connexion SSH a échoué." : reason[..Math.Min(reason.Length, 1400)]);
        }
        try
        {
            using var doc = JsonDocument.Parse(output.Text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.ToString() : "Le serveur a refusé l’action.");
            if (!root.TryGetProperty("data", out var data)) throw new InvalidOperationException("La réponse du serveur est incomplète.");
            return data.Clone();
        }
        catch (JsonException) { throw new InvalidOperationException("La réponse SSH n’est pas celle du panel. Vérifiez l’installation du compte d’administration sur le VPS."); }
    }

    internal static string? ParseRemoteError(string text)
    {
        // A failed SSH command can still return the helper's structured rejection.
        // Never interpret a success envelope as success when the exit code is nonzero.
        if (text.Length > 65536) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var message = error.GetString()?.Trim();
                return string.IsNullOrEmpty(message) ? null : message[..Math.Min(message.Length, 1400)];
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static async Task<(string KeyLine, string Fingerprint)> ScanFingerprintAsync(ConnectionSettings settings)
    {
        settings.Validate(false);
        var scan = await ProcessRunner.RunAsync("ssh-keyscan.exe", ["-T", "8", "-p", settings.Port.ToString(), "-t", "ed25519", settings.Host], null, TimeSpan.FromSeconds(15));
        var lines = scan.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).Select(line => line.Trim()).ToArray();
        if (scan.Code != 0 || lines.Length == 0) throw new InvalidOperationException("Impossible de lire la clé du serveur. Vérifiez l’adresse, le port et la connexion réseau.");
        // Keyscan output is untrusted. Accept only the requested modern host-key format.
        foreach (var line in lines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[1] != "ssh-ed25519") throw new InvalidOperationException("Format de clé SSH inattendu.");
            _ = Convert.FromBase64String(fields[2]);
        }
        var keyLine = string.Join("\n", lines) + "\n";
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, keyLine);
            var fingerprint = await ProcessRunner.RunAsync("ssh-keygen.exe", ["-lf", temp, "-E", "sha256"], null, TimeSpan.FromSeconds(10));
            if (fingerprint.Code != 0) throw new InvalidOperationException("Impossible de calculer l’empreinte SSH.");
            return (keyLine, fingerprint.Text.Trim());
        }
        finally { File.Delete(temp); }
    }
    public static byte[] VerifyDownload(JsonElement data)
    {
        var bytes = Convert.FromBase64String(data.GetProperty("base64").GetString() ?? "");
        var expected = data.GetProperty("sha256").GetString() ?? "";
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("La vérification SHA-256 a échoué. Le téléchargement n’a pas été enregistré.");
        return bytes;
    }
}

internal sealed record ProcessResult(int Code, string Text, string Error);
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, string? input, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException("Impossible de lancer le client SSH."); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Le client OpenSSH Windows est introuvable. Activez « Client OpenSSH » dans les fonctionnalités facultatives de Windows."); }
        using var cancel = new CancellationTokenSource(timeout);
        void KillProcess() { try { process.Kill(true); } catch (InvalidOperationException) { } }
        // 64 MiB SQLite becomes ~86 MiB of base64; cap the whole JSON response at 96 MiB.
        var text = ReadBoundedAsync(process.StandardOutput, 96 * 1024 * 1024, cancel.Token, KillProcess);
        var error = ReadBoundedAsync(process.StandardError, 64 * 1024, cancel.Token, KillProcess);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancel.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancel.Token);
            return new(process.ExitCode, await text, await error);
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            throw new TimeoutException("Le serveur n’a pas répondu dans le délai prévu. Vérifiez son état avant de répéter une action.");
        }
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int maxChars, CancellationToken token, Action onLimit)
    {
        var result = new StringBuilder();
        var buffer = new char[16384];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return result.ToString();
            if (count > maxChars - result.Length)
            {
                onLimit();
                throw new InvalidOperationException("La réponse du serveur dépasse la taille maximale autorisée. Le transfert a été interrompu.");
            }
            result.Append(buffer, 0, count);
        }
    }
}

public sealed class DemoTransport : IPanelTransport
{
    // Same shapes as server/eclat-admin.py and the game's monitor.json, with
    // a little movement between refreshes so the charts are not flat.
    private readonly Random random = new(7);
    private int tick;

    public Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null)
    {
        tick++;
        object data = action switch
        {
            "status" => Status(),
            "diagnostics" => new { status = Status(), journal = Journal(), backupCount = 3 },
            "logs" => new { text = Journal() },
            "backups" => new { items = new[] {
                new { name = "2026-10-23_171000.tar.gz", size = 2483302, modifiedUtc = DateTime.UtcNow.AddMinutes(-42).ToString("O") },
                new { name = "2026-10-23_120000.tar.gz", size = 2458011, modifiedUtc = DateTime.UtcNow.AddHours(-6).ToString("O") },
                new { name = "2026-10-22_120000.tar.gz", size = 2391554, modifiedUtc = DateTime.UtcNow.AddHours(-30).ToString("O") } } },
            "backup" => new { name = "2026-10-23_" + DateTime.Now.ToString("HHmmss") + ".tar.gz", size = 2483302, integrity = "ok" },
            "start" or "stop" or "restart" => new { service = new { active = "active", sub = "running", pid = 1428 } },
            _ => new { restored = true }
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(data));
    }

    private object Status()
    {
        var players = new List<object> { new { id = "p1", name = "Aurore", roomId = "r1" }, new { id = "p2", name = "Lucas", roomId = "r1" }, new { id = "p3", name = "Lou", roomId = "r2" } };
        if ((tick / 6) % 2 == 1 || tick > 14) players.Add(new { id = "p4", name = "Nagara", roomId = "r2" });
        var now = DateTime.UtcNow.ToString("O");
        return new
        {
            service = new { active = "active", sub = "running", pid = 1428 },
            protocol = new { ok = true, latencyMs = Math.Round(3.4 + random.NextDouble() * 2.2, 1), error = (string?)null },
            monitor = new
            {
                version = 1, updatedAt = now, status = "running", uptimeSeconds = 93840 + tick * 20,
                memory = new { rssBytes = 96_000_000L + random.Next(0, 9_000_000), heapUsedBytes = 41_000_000L },
                players,
                rooms = new[] { new { id = "r1", name = "Prairie", visibility = "public", count = 2, capacity = 2 }, new { id = "r2", name = "Forêt de Jade", visibility = "private", count = players.Count - 2, capacity = 2 } },
                counters = new { connections = 17 + tick, disconnections = 13 + tick, roomsCreated = 9, roomJoins = 15, roomLeaves = 11, errors = 1 },
                lastError = new { at = DateTime.UtcNow.AddHours(-3).ToString("O"), code = "ROOM_FULL" }
            },
            network = new { publication = "available", tunnel = "connected", updatedAt = now },
            metrics = new { memoryTotalBytes = 8589934592L, memoryAvailableBytes = 6388752384L - random.Next(0, 300_000_000), diskTotalBytes = 107374182400L, diskFreeBytes = 94596653056L, load = new[] { Math.Round(0.05 + random.NextDouble() * 0.2, 2), 0.06, 0.03 } },
            host = new { name = "eclat-vps", uptimeSeconds = 1_209_600 + tick * 20, nodeVersion = "v24.14.0" }
        };
    }

    private static string Journal()
    {
        string At(int minutes) => DateTime.Now.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ss") + "+0200 eclat-vps node[1428]: ";
        return "DÉMONSTRATION — données fictives\n" + string.Join("\n", new[] {
            At(64) + "Serveur Éclat démarré sur 127.0.0.1:8787", At(64) + "Base SQLite ouverte, intégrité vérifiée", At(63) + "Annuaire Coop publié",
            At(41) + "Salon créé : Prairie (public)", At(40) + "Aurore a rejoint Prairie", At(39) + "Lucas a rejoint Prairie",
            At(22) + "Avertissement : tunnel lent, nouvelle tentative", At(21) + "Tunnel reconnecté", At(18) + "Salon créé : Forêt de Jade (privé)",
            At(12) + "Refus ROOM_FULL : salon complet", At(6) + "Sauvegarde terminée (2,4 Mo)", At(1) + "Contrôle du protocole : réponse en 4,1 ms" });
    }
}

internal static class SelfTests
{
    public static void Run()
    {
        foreach (var host in new[] { "-oProxyCommand=evil", "a b", "host\ncommand", "" })
        {
            bool rejected = false;
            try { new ConnectionSettings { Host = host }.Validate(false); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid host accepted");
        }
        new ConnectionSettings { Host = "example.invalid", User = "eclat-panel" }.Validate(false);
        new ConnectionSettings { Host = "::1", User = "eclat-panel" }.Validate(false);
        var payload = Encoding.UTF8.GetBytes("SQLite test payload");
        var data = JsonSerializer.SerializeToElement(new { base64 = Convert.ToBase64String(payload), sha256 = Convert.ToHexString(SHA256.HashData(payload)) });
        if (!SshTransport.VerifyDownload(data).SequenceEqual(payload)) throw new Exception("Download validation failed");
        bool mismatchRejected = false;
        try { SshTransport.VerifyDownload(JsonSerializer.SerializeToElement(new { base64 = Convert.ToBase64String(payload), sha256 = "00" })); }
        catch (InvalidOperationException) { mismatchRejected = true; }
        if (!mismatchRejected) throw new Exception("Invalid hash accepted");
        if (SshTransport.ParseRemoteError("{\"ok\":false,\"error\":\"Sauvegarde introuvable\"}") != "Sauvegarde introuvable") throw new Exception("Remote helper error lost");
        if (SshTransport.ParseRemoteError("{\"ok\":true,\"error\":\"misleading\"}") is not null || SshTransport.ParseRemoteError("not json") is not null) throw new Exception("Invalid error envelope accepted");
        bool overflowStopped = false, overflowRejected = false;
        try { ProcessRunner.ReadBoundedAsync(new StringReader(new string('x', 33)), 32, CancellationToken.None, () => overflowStopped = true).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { overflowRejected = true; }
        if (!overflowStopped || !overflowRejected) throw new Exception("Response size limit not enforced");
        if (ProcessRunner.ReadBoundedAsync(new StringReader("small response"), 32, CancellationToken.None, () => { }).GetAwaiter().GetResult() != "small response") throw new Exception("Bounded response read failed");
    }
}
