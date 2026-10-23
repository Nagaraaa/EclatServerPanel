using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace EclatServerPanel;

/// <summary>
/// One long-lived <c>ssh.exe</c> running the Linux tool in session mode: one
/// JSON line per request and per reply. Windows OpenSSH has no connection
/// multiplexing, so this avoids a full handshake on every refresh.
/// </summary>
internal sealed class SshSession : IDisposable
{
    internal static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    // The Linux tool closes an idle session after 600 s; reopen a little before.
    internal static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(480);
    private readonly Process process;
    private readonly StreamWriter input;
    private readonly StreamReader output;
    private readonly StringBuilder pending = new();
    private readonly char[] buffer = new char[65536];
    internal string Key { get; }
    internal DateTime LastUsed { get; private set; } = DateTime.UtcNow;
    internal bool Usable => !process.HasExited && DateTime.UtcNow - LastUsed < MaxIdle;

    private SshSession(string key, Process process)
    {
        Key = key; this.process = process;
        input = process.StandardInput; input.NewLine = "\n"; input.AutoFlush = true;
        output = process.StandardOutput;
    }

    internal enum OpenResult { Opened, Unsupported, Failed }

    internal static async Task<(SshSession? Session, OpenResult Result)> OpenAsync(string key, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("ssh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start };
        try { if (!process.Start()) return (null, OpenResult.Failed); }
        catch (System.ComponentModel.Win32Exception) { process.Dispose(); return (null, OpenResult.Failed); }
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var session = new SshSession(key, process);
        try
        {
            using var cancel = new CancellationTokenSource(OpenTimeout);
            await session.input.WriteLineAsync("{\"action\":\"session\"}".AsMemory(), cancel.Token);
            string? line = await session.ReadLineAsync(64 * 1024, cancel.Token);
            if (line is null) { session.Dispose(); return (null, OpenResult.Failed); }
            using var reply = JsonDocument.Parse(line);
            var root = reply.RootElement;
            bool opened = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("data", out var data) && data.TryGetProperty("session", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1;
            if (opened) return (session, OpenResult.Opened);
            // An older tool answers with a structured refusal: use one call per action.
            session.Dispose();
            return (null, OpenResult.Unsupported);
        }
        catch (OperationCanceledException)
        {
            // Tools older than 1.2 wait for the end of input and never answer.
            session.Dispose();
            return (null, OpenResult.Unsupported);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            session.Dispose();
            return (null, OpenResult.Failed);
        }
    }

    internal async Task<ProcessResult> ExchangeAsync(string request, TimeSpan timeout, int maxChars)
    {
        if (request.Contains('\n')) throw new InvalidOperationException("Requête invalide.");
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await input.WriteLineAsync(request.AsMemory(), cancel.Token);
            string line = await ReadLineAsync(maxChars, cancel.Token) ?? throw new IOException("Session SSH fermée.");
            LastUsed = DateTime.UtcNow;
            using var reply = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            bool ok = reply.RootElement.ValueKind == JsonValueKind.Object && reply.RootElement.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new(ok ? 0 : 1, line, "");
        }
        catch (OperationCanceledException) { throw new TimeoutException("Le serveur n’a pas répondu dans le délai prévu. Vérifiez son état avant de répéter une action."); }
    }

    private async Task<string?> ReadLineAsync(int maxChars, CancellationToken token)
    {
        while (true)
        {
            for (int i = 0; i < pending.Length; i++)
            {
                if (pending[i] != '\n') continue;
                string line = pending.ToString(0, i);
                pending.Remove(0, i + 1);
                return line;
            }
            if (pending.Length > maxChars) throw new InvalidOperationException("La réponse du serveur dépasse la taille maximale autorisée. Le transfert a été interrompu.");
            int count = await output.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return null;
            pending.Append(buffer, 0, count);
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}

public sealed partial class SshTransport : IDisposable
{
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private SshSession? session;
    private string? unsupportedKey;
    private DateTime unsupportedUntil;

    /// <summary>
    /// Sends one request through the shared session when the Linux tool offers
    /// it, otherwise through one SSH call. A read is retried on a fresh call
    /// after a broken session; a change is never repeated automatically.
    /// </summary>
    private async Task<ProcessResult> ExchangeAsync(IReadOnlyList<string> args, string request, TimeSpan timeout, int maxChars, bool mutation)
    {
        string key = string.Join("\u0001", args);
        await sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool skip = unsupportedKey == key && DateTime.UtcNow < unsupportedUntil;
            if (!skip)
            {
                if (session is { } current && (current.Key != key || !current.Usable)) { current.Dispose(); session = null; }
                if (session is null)
                {
                    var (opened, result) = await SshSession.OpenAsync(key, args).ConfigureAwait(false);
                    session = opened;
                    if (result == SshSession.OpenResult.Unsupported) { unsupportedKey = key; unsupportedUntil = DateTime.UtcNow.AddHours(6); }
                }
                if (session is { } live)
                {
                    try { return await live.ExchangeAsync(request, timeout, maxChars).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or TimeoutException or JsonException)
                    {
                        live.Dispose(); session = null;
                        if (mutation || ex is TimeoutException) throw new TimeoutException("La connexion au serveur s’est interrompue pendant l’action. Vérifiez son état avant de la répéter.");
                    }
                }
            }
        }
        finally { sessionGate.Release(); }
        return await ProcessRunner.RunAsync("ssh.exe", args, request + "\n", timeout, maxChars).ConfigureAwait(false);
    }

    public void Dispose()
    {
        session?.Dispose();
        session = null;
    }
}
