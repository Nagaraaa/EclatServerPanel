using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EclatServerPanel;

internal static partial class SelfTests
{
    public static void Run()
    {
        ConnectionValidation();
        EndpointScopedTrust();
        ResponseValidation();
        BoundedReads();
        DemoTransitions();
        RunAdditional();
    }
    static partial void RunAdditional();
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Reject(Action operation, string reason)
    {
        try { operation(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(reason);
    }
    private static void ConnectionValidation()
    {
        foreach (var host in new[] { "-oProxyCommand=evil", "a b", "host\ncommand", "example.invalid\n", "a..b", "bad-.example", "", null! })
            Reject(() => new ConnectionSettings { Host = host }.Validate(false), "Unsafe host accepted");
        new ConnectionSettings { Host = "example.invalid", User = "eclat-panel" }.Validate(false);
        new ConnectionSettings { Host = "::1", User = "eclat-panel" }.Validate(false);
        Reject(() => new ConnectionSettings { Host = "example.invalid", User = "name\n" }.Validate(false), "User newline accepted");
        Reject(() => new ConnectionSettings { Host = "example.invalid", Port = 0 }.Validate(false), "Invalid port accepted");
        var settings = new ConnectionSettings { Host = "EXAMPLE.invalid", KeyPath = Path.Combine(Path.GetTempPath(), "key with spaces") };
        Assert(settings.Snapshot() == settings && settings.EndpointIdentity == "example.invalid", "Settings snapshot changed endpoint");
        var arguments = SshTransport.BuildArguments(settings, Path.Combine(Path.GetTempPath(), "directory with spaces", "known_hosts"));
        Assert(arguments.Contains("none") && arguments.Contains("StrictHostKeyChecking=yes") && arguments.Contains("GlobalKnownHostsFile=NUL"), "OpenSSH trust defaults are not isolated");
        Assert(arguments.Any(arg => arg.StartsWith("UserKnownHostsFile=\"") && arg.EndsWith('"')), "Known-hosts path with spaces is not quoted for OpenSSH");
        Reject(() => SshTransport.ValidateRequest("download", "../coop.sqlite"), "Path traversal in snapshot name accepted");
        Reject(() => SshTransport.ValidateRequest("unknown", null), "Unknown action accepted");
        Reject(() => SshTransport.ValidateRequest("status", "unexpected"), "Unexpected request parameter accepted");
    }
    private static void EndpointScopedTrust()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eclat-trust-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "known_hosts");
            string key = FakePublicKey(1), nextKey = FakePublicKey(2);
            var endpoint = new ConnectionSettings { Host = "server.example", Port = 2222 };
            string line = $"[server.example]:2222 ssh-ed25519 {key}\n";
            Assert(!KnownHostsTrust.HasTrustedHost(endpoint, path), "Absent known-hosts file trusted");
            Reject(() => KnownHostsTrust.NormalizeScannedKey(endpoint, $"server.example ssh-ed25519 {key}"), "Default SSH port trusted for nondefault endpoint");
            Reject(() => KnownHostsTrust.NormalizeScannedKey(endpoint, $"[other.example]:2222 ssh-ed25519 {key}"), "Wrong hostname in keyscan accepted");
            Reject(() => KnownHostsTrust.NormalizeScannedKey(endpoint, line + $"[server.example]:2222 ssh-ed25519 {nextKey}\n"), "Ambiguous network keys accepted");
            Reject(() => KnownHostsTrust.NormalizeScannedKey(endpoint, "[server.example]:2222 ssh-ed25519 AAAA"), "Malformed SSH wire key accepted");
            var normalized = KnownHostsTrust.NormalizeScannedKey(endpoint, line);
            Assert(normalized.Fingerprint.StartsWith("SHA256:"), "SHA-256 fingerprint absent");
            File.WriteAllText(path, $"unrelated.example ssh-ed25519 {key}\n");
            KnownHostsTrust.SaveTrustedKey(endpoint, line, path);
            Assert(KnownHostsTrust.HasTrustedHost(endpoint, path), "Explicit endpoint trust lost");
            Assert(!KnownHostsTrust.HasTrustedHost(endpoint with { Port = 22 }, path), "Trust crossed SSH ports");
            Assert(!KnownHostsTrust.HasTrustedHost(endpoint with { Host = "other.example" }, path), "Trust crossed hostnames");
            KnownHostsTrust.SaveTrustedKey(endpoint, line.Replace(key, nextKey), path);
            string contents = File.ReadAllText(path);
            Assert(contents.Contains("unrelated.example") && contents.Contains(nextKey) && contents.Split('\n').Count(item => item.StartsWith("[server.example]:2222")) == 1, "Explicit key replacement affected another host or retained old endpoint keys");
            byte[] salt = Enumerable.Repeat((byte)9, 20).ToArray();
            string hashed = "|1|" + Convert.ToBase64String(salt) + "|" + Convert.ToBase64String(HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(endpoint.EndpointIdentity)));
            File.WriteAllText(path, $"{hashed} ssh-ed25519 {key}\n");
            Assert(KnownHostsTrust.HasTrustedHost(endpoint, path), "OpenSSH hashed endpoint trust not recognized");
            Assert(!KnownHostsTrust.HasTrustedHost(endpoint with { Port = 22 }, path), "Hashed trust crossed SSH ports");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static string FakePublicKey(byte value)
    {
        byte[] bytes = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(15, 4), 32);
        bytes.AsSpan(19).Fill(value);
        return Convert.ToBase64String(bytes);
    }
    private static void ResponseValidation()
    {
        foreach (var text in new[] { "[]", "null", "{}", "{\"ok\":true}", "{\"ok\":true,\"data\":[]}", "{\"ok\":true,\"ok\":false,\"data\":{}}", "{\"ok\":true,\"data\":{\"text\":12}}" })
            Reject(() => PanelResponse.Parse(text, "logs", null), "Malformed or ambiguous response accepted");
        Assert(PanelResponse.Parse("{\"ok\":true,\"data\":{\"text\":\"Journal\"}}", "logs", null).GetProperty("text").GetString() == "Journal", "Valid log response rejected");
        Assert(SshTransport.ParseRemoteError("{\"ok\":false,\"error\":\"Sauvegarde introuvable\"}") == "Sauvegarde introuvable", "Remote helper error lost");
        Assert(SshTransport.ParseRemoteError("{\"ok\":true,\"error\":\"misleading\"}") is null && SshTransport.ParseRemoteError("not json") is null, "Failed process could be treated as successful JSON");
        var error = PanelResponse.SafeError("token=NEVER_SHOW https://user:NEVER_SHOW@host/?key=NEVER_SHOW Bearer NEVER_SHOW {\"api_key\":\"NEVER_SHOW\"}\u001b[31m");
        Assert(!error.Contains("NEVER_SHOW") && !error.Contains('\u001b'), "Sensitive error content was not removed");
        var payload = "SQLite format 3\0Synthetic test only"u8.ToArray();
        var download = JsonSerializer.SerializeToElement(new { base64 = Convert.ToBase64String(payload), sha256 = Convert.ToHexString(SHA256.HashData(payload)) });
        Assert(SshTransport.VerifyDownload(download).SequenceEqual(payload), "Download hash validation failed");
        Reject(() => SshTransport.VerifyDownload(JsonSerializer.SerializeToElement(new { base64 = Convert.ToBase64String(payload), sha256 = new string('0', 64) })), "Mismatched download checksum accepted");
        Reject(() => SshTransport.VerifyDownload(JsonSerializer.SerializeToElement(new { base64 = 12, sha256 = "bad" })), "Wrong download types accepted");
        Reject(() => SshTransport.VerifyDownload(JsonSerializer.SerializeToElement(new { base64 = "AAAA", sha256 = new string('0', 64) })), "Non-SQLite download accepted");
    }
    private static void BoundedReads()
    {
        bool stopped = false;
        Reject(() => ProcessRunner.ReadBoundedAsync(new StringReader(new string('x', 33)), 32, CancellationToken.None, () => stopped = true).GetAwaiter().GetResult(), "Response cap was not enforced");
        Assert(stopped, "Overflow did not terminate the process");
        Assert(ProcessRunner.ReadBoundedAsync(new StringReader("small response"), 32, CancellationToken.None, () => { }).GetAwaiter().GetResult() == "small response", "Bounded response reader lost data");
    }
    private static void DemoTransitions()
    {
        var transport = new DemoTransport();
        var settings = new ConnectionSettings();
        JsonElement Send(string action, string? name = null) => transport.SendAsync(settings, action, name).GetAwaiter().GetResult();
        var initial = Send("status");
        Assert(initial.GetProperty("protocol").GetProperty("ok").GetBoolean(), "Demo did not begin online");
        for (int index = 0; index < 40; index++)
        {
            var monitor = Send("status").GetProperty("monitor");
            Assert(monitor.GetProperty("players").GetArrayLength() <= DemoTransport.MaxPlayers, "Demo exceeded the player limit");
            Assert(monitor.GetProperty("rooms").EnumerateArray().All(room => room.GetProperty("capacity").GetInt32() == DemoTransport.MaxPlayers), "Demo rooms use an outdated capacity");
        }
        Send("stop");
        var stopped = Send("status");
        Assert(stopped.GetProperty("service").GetProperty("active").GetString() == "inactive" && !stopped.GetProperty("protocol").GetProperty("ok").GetBoolean() && stopped.GetProperty("monitor").GetProperty("players").GetArrayLength() == 0, "Demo stop left active players or a healthy service");
        Send("start"); Assert(Send("status").GetProperty("protocol").GetProperty("ok").GetBoolean(), "Demo start failed");
        int before = Send("backups").GetProperty("items").GetArrayLength();
        string created = Send("backup").GetProperty("name").GetString()!;
        Assert(Send("backups").GetProperty("items").GetArrayLength() == before + 1, "Demo backup not added to its list");
        Assert(SshTransport.VerifyDownload(Send("download", created)).Length > 0, "Demo download did not match the backup");
        Assert(Send("restore", created).GetProperty("restored").GetBoolean() && Send("backups").GetProperty("items").GetArrayLength() == before + 2, "Demo restore failed to produce a rollback snapshot");
        Reject(() => Send("restore", "eclat-20000101T000000Z-00000000.tar.gz"), "Demo restored an absent backup");
        Reject(() => Send("invented-action"), "Demo invented success for an unknown action");
    }
}
