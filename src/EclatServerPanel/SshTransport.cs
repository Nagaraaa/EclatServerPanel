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
    private static readonly HashSet<string> Actions = ["status", "logs", "start", "stop", "restart", "backup", "backups", "download", "restore", "diagnostics", "close-room"];
    public async Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null)
    {
        settings = settings.Snapshot();
        settings.Validate();
        ValidateRequest(action, name);
        if (!KnownHostsTrust.HasTrustedHost(settings)) throw new InvalidOperationException("Vérifiez et enregistrez d’abord l’empreinte de cette adresse et de ce port dans Connexion.");
        var args = BuildArguments(settings, ConnectionSettings.KnownHostsPath);
        var request = JsonSerializer.Serialize(new { action, name });
        int timeout = action switch { "restore" => 360, "start" or "stop" or "restart" => 90, "backup" or "download" => 120, _ => 35 };
        var output = await ProcessRunner.RunAsync("ssh.exe", args, request + "\n", TimeSpan.FromSeconds(timeout), action == "download" ? 96 * 1024 * 1024 : 4 * 1024 * 1024).ConfigureAwait(false);
        if (output.Code != 0)
        {
            var remoteError = ParseRemoteError(output.Text);
            if (remoteError is not null) throw new InvalidOperationException(remoteError);
            var reason = output.Error.Trim();
            if (reason.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || reason.Contains("REMOTE HOST IDENTIFICATION", StringComparison.OrdinalIgnoreCase)) reason = "L’empreinte SSH ne correspond pas. Vérifiez-la auprès de l’hébergeur avant toute nouvelle confiance.";
            else if (reason.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) reason = "Authentification refusée. Vérifiez le compte, la clé dédiée et son chargement dans l’agent SSH si elle est chiffrée.";
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? "La connexion SSH a échoué." : PanelResponse.SafeError(reason));
        }
        return PanelResponse.Parse(output.Text, action, name);
    }

    internal static void ValidateRequest(string action, string? name)
    {
        if (!Actions.Contains(action)) throw new InvalidOperationException("Action inconnue.");
        if (action is "download" or "restore") PanelResponse.ValidateSnapshotName(name);
        else if (action == "close-room")
        {
            if (name is null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-zA-Z0-9_-]{1,64}\z")) throw new InvalidOperationException("Identifiant du salon invalide.");
        }
        else if (name is not null) throw new InvalidOperationException("Cette action n’accepte pas de nom de sauvegarde.");
    }
    internal static List<string> BuildArguments(ConnectionSettings settings, string knownHostsPath)
    {
        var path = Path.GetFullPath(knownHostsPath).Replace('\\', '/');
        return ["-F", "none", "-T", "-p", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", Path.GetFullPath(settings.KeyPath), "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=\"" + path + "\"", "-o", "GlobalKnownHostsFile=NUL", "-o", "HostKeyAlgorithms=ssh-ed25519", "-o", "UpdateHostKeys=no", "-o", "VerifyHostKeyDNS=no", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2", "-o", "LogLevel=ERROR", "-l", settings.User, settings.CanonicalHost, "panel"];
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
                return string.IsNullOrEmpty(message) ? null : PanelResponse.SafeError(message);
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static async Task<(string KeyLine, string Fingerprint)> ScanFingerprintAsync(ConnectionSettings settings)
    {
        settings = settings.Snapshot();
        settings.Validate(false);
        var scan = await ProcessRunner.RunAsync("ssh-keyscan.exe", ["-T", "8", "-p", settings.Port.ToString(), "-t", "ed25519", settings.CanonicalHost], null, TimeSpan.FromSeconds(15), 65536).ConfigureAwait(false);
        if (scan.Code != 0) throw new InvalidOperationException("Impossible de lire la clé du serveur. Vérifiez l’adresse, le port et la connexion réseau.");
        return KnownHostsTrust.NormalizeScannedKey(settings, scan.Text);
    }
    public static byte[] VerifyDownload(JsonElement data)
    {
        PanelResponse.ValidateDownload(data);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data.GetProperty("base64").GetString()!); }
        catch (FormatException) { throw new InvalidOperationException("L’encodage de la sauvegarde reçue est invalide. Aucun fichier n’a été enregistré."); }
        if (bytes.Length is 0 or > PanelResponse.MaxDownloadBytes || !bytes.AsSpan().StartsWith("SQLite format 3\0"u8)) throw new InvalidOperationException("Le téléchargement n’est pas une base SQLite valide dans la limite autorisée de 64 Mio.");
        var expected = data.GetProperty("sha256").GetString()!;
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("La vérification SHA-256 a échoué. Le téléchargement n’a pas été enregistré.");
        return bytes;
    }
}

internal sealed record ProcessResult(int Code, string Text, string Error);
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, string? input, TimeSpan timeout, int maxOutputChars = 4 * 1024 * 1024)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException("Impossible de lancer le client SSH."); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Le client OpenSSH Windows est introuvable. Activez « Client OpenSSH » dans les fonctionnalités facultatives de Windows."); }
        using var cancel = new CancellationTokenSource(timeout);
        void KillProcess() { try { process.Kill(true); } catch (InvalidOperationException) { } }
        // 64 MiB SQLite becomes ~86 MiB of base64; cap the whole JSON response at 96 MiB.
        var text = ReadBoundedAsync(process.StandardOutput, maxOutputChars, cancel.Token, KillProcess);
        var error = ReadBoundedAsync(process.StandardError, 64 * 1024, cancel.Token, KillProcess);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancel.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            return new(process.ExitCode, await text.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            throw new TimeoutException("Le serveur n’a pas répondu dans le délai prévu. Vérifiez son état avant de répéter une action.");
        }
        catch
        {
            KillProcess();
            throw;
        }
        finally
        {
            // Observe both reader failures even when the peer dies before stdin is closed.
            try { await Task.WhenAll(text, error).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int maxChars, CancellationToken token, Action onLimit)
    {
        var result = new StringBuilder();
        var buffer = new char[16384];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
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
