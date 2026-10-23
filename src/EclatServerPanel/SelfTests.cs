using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        DemoAdministration();
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
            Assert(monitor.GetProperty("players").GetArrayLength() == 20, "The map demonstration must include twenty players across several rooms");
            Assert(monitor.GetProperty("rooms").EnumerateArray().All(room => room.GetProperty("capacity").GetInt32() == DemoTransport.MaxPlayers), "Demo rooms use an outdated capacity");
            Assert(monitor.GetProperty("rooms").EnumerateArray().All(room => room.GetProperty("count").GetInt32() <= room.GetProperty("capacity").GetInt32()), "Demo room occupancy exceeded its own capacity");
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

    private static void DemoAdministration()
    {
        var transport = new DemoTransport(); var settings = new ConnectionSettings();
        JsonElement Send(string action, object? fields = null) => transport.SendAdminAsync(settings, action, JsonSerializer.SerializeToElement(fields ?? new { })).GetAwaiter().GetResult();
        JsonElement Status() => transport.SendAsync(settings, "status").GetAwaiter().GetResult();
        JsonElement Command(string command, object arguments, string? id = null) => Send("admin.command", new { requestId = id ?? Guid.NewGuid().ToString(), command, arguments });
        var monitor = Status().GetProperty("monitor"); var players = monitor.GetProperty("players").EnumerateArray().ToArray();
        Assert(players.Length == 20 && players.All(player => Guid.TryParse(player.GetProperty("id").GetString(), out _)), "Demo players must have stable UUIDs");
        Assert(players.Count(player => player.GetProperty("place").ValueKind == JsonValueKind.Null) == 1, "Demo map must retain one player without position");
        Assert(players.Count(player => player.GetProperty("stateAgeMs").ValueKind == JsonValueKind.Number && player.GetProperty("stateAgeMs").GetInt32() > 10000) == 2, "Demo map must contain two stale positions");
        var groups = players.Where(player => player.GetProperty("place").ValueKind == JsonValueKind.Object).GroupBy(player => player.GetProperty("place").GetProperty("section").GetString()).ToArray();
        Assert(groups.Any(group => group.Count() == 5) && groups.Any(group => group.Count() == 12), "Demo must exercise the five-player fan and the twelve-player list");
        Assert(groups.Single(group => group.Count() == 12).Select(player => player.GetProperty("roomId").GetString()).Distinct().Count() == 2, "A map group must aggregate players from different rooms");
        Assert(players.All(player => player.GetProperty("accountFingerprint").GetString()!.Length == 8), "The monitor must expose only eight fingerprint characters");
        Assert(monitor.GetProperty("mapAtlas").GetProperty("sections").GetArrayLength() == 4, "Demo atlas must contain synthetic geometry");
        var overview = Send("admin.overview");
        Assert(overview.GetProperty("connectedPlayers").GetArrayLength() == 20 && overview.GetProperty("bans").GetArrayLength() == 1, "Demo administration must include connected players and one existing ban");
        string hash = overview.GetProperty("accounts")[0].GetProperty("accountHash").GetString()!;
        int originalBalance = overview.GetProperty("accounts")[0].GetProperty("balance").GetInt32();
        Assert(hash.Length == 64, "Mutation target must keep the complete synthetic account hash on the wire");
        string requestId = Guid.NewGuid().ToString();
        int balance = Command("eclats.adjust", new { accountHash = hash, amount = 75, reason = "Test de crédit fictif" }, requestId).GetProperty("balance").GetInt32();
        Assert(balance == originalBalance + 75, "Demo credit did not update the balance");
        Assert(Command("eclats.adjust", new { reason = "Test de crédit fictif", amount = 75, accountHash = hash }, requestId).GetProperty("balance").GetInt32() == balance, "Retry must not credit twice or depend on property order");
        Reject(() => Command("eclats.adjust", new { accountHash = hash, amount = 76, reason = "Test de crédit fictif" }, requestId), "One request UUID accepted changed arguments");
        Reject(() => Command("eclats.adjust", new { accountHash = hash, amount = -balance - 1, reason = "Test de débit refusé" }), "Demo accepted a negative account balance");
        Reject(() => Command("eclats.adjust", new { accountHash = new string('f', 64), amount = 1, reason = "Compte absent" }), "Demo credited an unknown account");
        Reject(() => Command("eclats.adjust", new { accountHash = hash, amount = 1, reason = "" }), "Demo accepted a mutation without reason");
        var after = Send("admin.overview").GetProperty("accounts").EnumerateArray().Single(account => account.GetProperty("accountHash").GetString() == hash);
        Assert(after.GetProperty("balance").GetInt32() == balance, "Refused mutations changed the balance");

        string refundId = Guid.NewGuid().ToString();
        Assert(Command("casino.refund", new { accountHash = hash, amount = 500, reason = "Jetons de test" }, refundId).GetProperty("deliveryPending").GetBoolean(), "Demo refund must remain a pending game delivery");
        Command("casino.refund", new { accountHash = hash, amount = 500, reason = "Jetons de test" }, refundId);
        var statistics = Send("statistics");
        Assert(statistics.GetProperty("refundTotals").GetProperty("refunds").GetInt32() == 1 && statistics.GetProperty("refundTotals").GetProperty("coins").GetInt32() == 500, "Refund retry changed statistics twice");
        Assert(statistics.GetProperty("totals").GetProperty("eclatsInCirculation").GetInt64() == Send("admin.overview").GetProperty("accounts").EnumerateArray().Sum(account => account.GetProperty("balance").GetInt64()), "Statistics diverged from demo account balances");

        var room = overview.GetProperty("rooms")[0]; string roomId = room.GetProperty("id").GetString()!; int occupancy = room.GetProperty("count").GetInt32();
        Reject(() => Command("room.update", new { roomId, capacity = occupancy - 1 }), "Demo accepted a room capacity below occupancy");
        Reject(() => Command("room.update", new { roomId, capacity = 11 }), "Demo accepted eleven room members");
        Assert(Command("room.update", new { roomId, capacity = occupancy, visibility = "private" }).GetProperty("visibility").GetString() == "private", "Demo room update did not persist");
        Assert(Command("maintenance.set", new { enabled = true }).GetProperty("connectedPlayers").GetInt32() == 20 && Send("admin.overview").GetProperty("maintenance").GetBoolean(), "Maintenance must preserve players already connected");
        Command("maintenance.set", new { enabled = false });
        Command("account.ban", new { accountHash = hash, reason = "Bannissement de test", expiresAt = (string?)null });
        Assert(Send("admin.overview").GetProperty("connectedPlayers").GetArrayLength() == 19, "Banning a connected account must disconnect it");
        Command("account.unban", new { accountHash = hash, reason = "Levée de test" });
        Assert(Send("admin.overview").GetProperty("bans").GetArrayLength() == 1, "Unban changed unrelated bans");
        var connected = Send("admin.overview").GetProperty("connectedPlayers")[0];
        string kickId = Guid.NewGuid().ToString(); string playerId = connected.GetProperty("id").GetString()!;
        Command("player.kick", new { playerId }, kickId); Command("player.kick", new { playerId }, kickId);
        Assert(Send("admin.overview").GetProperty("connectedPlayers").GetArrayLength() == 18, "Kick retry disconnected more than one player");
        Command("room.close", new { roomId });
        var closed = Send("admin.overview");
        Assert(closed.GetProperty("rooms").GetArrayLength() == 2 && closed.GetProperty("connectedPlayers").GetArrayLength() == 18, "Room closure must preserve connections while removing the room");
        Assert(closed.GetProperty("connectedPlayers").EnumerateArray().All(player => player.GetProperty("roomId").GetString() != roomId), "Closed room still owns players");
        Assert(Send("admin.audit").GetProperty("items").GetArrayLength() > 10, "Demo mutations and refusals must leave an administration journal");

        var initialConfig = Send("config.get"); string initialVersion = Send("config.history").GetProperty("items")[0].GetProperty("id").GetString()!;
        var changedConfig = JsonNode.Parse(initialConfig.GetProperty("config").GetRawText())!; changedConfig["prices"]!["single"] = 40;
        var saved = Send("config.save", new { requestId = Guid.NewGuid().ToString(), config = changedConfig, expectedSha256 = initialConfig.GetProperty("sha256").GetString(), reason = "Prix de démonstration" });
        Assert(saved.GetProperty("accepted").GetBoolean() && saved.GetProperty("sha256").GetString() != initialConfig.GetProperty("sha256").GetString(), "Demo configuration save lacks an acceptance acknowledgement");
        Assert(Send("config.get").GetProperty("config").GetProperty("prices").GetProperty("single").GetInt32() == 40, "Demo configuration save did not persist");
        var restored = Send("config.restore", new { requestId = Guid.NewGuid().ToString(), version = initialVersion, expectedSha256 = saved.GetProperty("sha256").GetString(), reason = "Restauration de démonstration" });
        Assert(restored.GetProperty("accepted").GetBoolean() && Send("config.get").GetProperty("config").GetProperty("prices").GetProperty("single").GetInt32() == 30, "Demo configuration restore failed");
        Reject(() => Send("config.save", new { requestId = Guid.NewGuid().ToString(), config = changedConfig, expectedSha256 = saved.GetProperty("sha256").GetString(), reason = "Conflit de test" }), "Demo overwrote a concurrent configuration version");
        for (int index = 0; index < 22; index++)
        {
            var current = Send("config.get");
            Send("config.save", new { requestId = Guid.NewGuid().ToString(), config = current.GetProperty("config"), expectedSha256 = current.GetProperty("sha256").GetString(), reason = "Historique de test " + index });
        }
        Assert(Send("config.history").GetProperty("items").GetArrayLength() == 20, "Demo configuration history exceeded its twenty-version limit");
        int previousPid = Status().GetProperty("monitor").GetProperty("pid").GetInt32();
        transport.SendAsync(settings, "restart").GetAwaiter().GetResult();
        var restarted = Status();
        Assert(restarted.GetProperty("monitor").GetProperty("pid").GetInt32() > previousPid && restarted.GetProperty("service").GetProperty("pid").GetInt32() == restarted.GetProperty("monitor").GetProperty("pid").GetInt32(), "Demo restart must publish a coherent new process ID");
        transport.SendAsync(settings, "stop").GetAwaiter().GetResult();
        Reject(() => Send("admin.overview"), "Demo local command channel remained available after stop");
        Assert(Send("config.get").GetProperty("status").GetProperty("state").GetString() == "pending" && Send("statistics").ValueKind == JsonValueKind.Object, "Offline configuration/statistics must remain readable without claiming acceptance");
        Reject(() => Send("invented-admin-action"), "Demo accepted an unknown administration action");
    }
}
