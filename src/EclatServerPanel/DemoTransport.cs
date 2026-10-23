using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EclatServerPanel;

/// <summary>In-memory demonstration. It never reads or writes connection credentials or server data.</summary>
public sealed partial class DemoTransport : IPanelTransport
{
    internal const int MaxPlayers = 10;
    private readonly object gate = new();
    private readonly Random random = new(7);
    private readonly List<DemoBackup> backups = [];
    private readonly List<string> events = [];
    private bool running = true;
    private readonly HashSet<string> closedRooms = [];
    private int tick;
    private long totalConnections = 34, totalDisconnections = 14, totalRoomLeaves = 11;
    private DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddHours(-26);
    private int gamePid = 1429;
    private readonly byte[] database = CreateSyntheticDatabase();

    public DemoTransport()
    {
        InitializeAdministration();
        for (int index = 0; index < 3; index++) CreateSnapshot(DateTimeOffset.UtcNow.AddHours(-index * 6 - 1));
        backups.Sort((left, right) => right.Created.CompareTo(left.Created));
    }

    public Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null)
    {
        SshTransport.ValidateRequest(action, name);
        lock (gate)
        {
            object data;
            switch (action)
            {
                case "status": data = Status(); break;
                case "close-room":
                    string roomToClose = LegacyRoomId(name!);
                    int removed = CloseDemoRoom(roomToClose);
                    AppendAudit(Guid.NewGuid().ToString(), "room.close", roomToClose, null, new { ok = true, closed = true, removedPlayers = removed });
                    data = new { closed = true, roomId = name }; break;
                case "diagnostics": data = new { status = Status(), journal = Journal(), backupCount = backups.Count }; break;
                case "logs": data = new { text = Journal() }; break;
                case "backups": data = new { items = backups.Select(BackupInfo).ToArray() }; break;
                case "backup":
                    var created = CreateSnapshot(DateTimeOffset.UtcNow);
                    data = new { name = created.Name, size = created.Bytes.LongLength, modifiedUtc = created.Created.ToString("O"), integrity = "ok", sha256 = Hash(created.Bytes) };
                    events.Add("Sauvegarde créée en démonstration");
                    break;
                case "start": Start(); data = new { service = Service() }; events.Add("Service démarré en démonstration"); break;
                case "stop": Stop(); data = new { service = Service() }; events.Add("Service arrêté en démonstration"); break;
                case "restart": Stop(); Start(); data = new { service = Service() }; events.Add("Service redémarré en démonstration"); break;
                case "download":
                    var selected = Find(name!);
                    data = new { name = selected.Name[..^7] + ".sqlite", base64 = Convert.ToBase64String(selected.Bytes), sha256 = Hash(selected.Bytes) };
                    break;
                case "restore":
                    _ = Find(name!);
                    var rollback = CreateSnapshot(DateTimeOffset.UtcNow);
                    data = new { name, restored = true, integrity = "ok", rollbackBackup = rollback.Name };
                    events.Add("Sauvegarde restaurée en démonstration");
                    break;
                default: throw new InvalidOperationException("Action inconnue.");
            }
            // Keep fake responses on the same contract as the actual SSH helper.
            return Task.FromResult(PanelResponse.Parse(JsonSerializer.Serialize(new { ok = true, data }), action, name));
        }
    }

    private object Service() => new { active = running ? "active" : "inactive", sub = running ? "running" : "dead", pid = running ? gamePid : 0 };
    private object Status()
    {
        tick++;
        var players = CurrentPlayers().Select(PlayerMonitor).ToArray();
        var rooms = running ? DemoRooms().ToArray() : [];
        var now = DateTimeOffset.UtcNow.ToString("O");
        return new
        {
            service = Service(),
            protocol = new { ok = running, latencyMs = running ? (double?)Math.Round(3.4 + random.NextDouble() * 2.2, 1) : null, error = running ? null : "Service arrêté en démonstration" },
            monitor = new
            {
                version = 1, pid = running ? gamePid : 0, processCurrent = running, startedAt = startedAt.ToString("O"), updatedAt = now, status = running ? "running" : "stopped", uptimeSeconds = running ? Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds) : 0,
                memory = new { rssBytes = running ? 96_000_000L + random.Next(0, 9_000_000) : 0, heapUsedBytes = running ? 41_000_000L : 0 },
                players, rooms, connections = players.Length, mapAtlas = DemoAtlas, eclatsConfig = ConfigStatus(),
                counters = new { connections = totalConnections, disconnections = totalDisconnections, roomsCreated = 9, roomJoins = 15, roomLeaves = totalRoomLeaves, errors = 1 },
                lastError = new { at = DateTimeOffset.UtcNow.AddHours(-3).ToString("O"), code = "ROOM_FULL" }
            },
            network = new { publication = running ? "available" : "unavailable", tunnel = running ? "connected" : "disconnected", updatedAt = now },
            metrics = new { memoryTotalBytes = 8589934592L, memoryAvailableBytes = 6388752384L - random.Next(0, 300_000_000), diskTotalBytes = 107374182400L, diskFreeBytes = 94596653056L, load = new[] { Math.Round(0.05 + random.NextDouble() * 0.2, 2), 0.06, 0.03 } },
            host = new { name = "eclat-vps", uptimeSeconds = 1_209_600 + tick * 20, nodeVersion = "v24.14.0" }
        };
    }
    private DemoBackup CreateSnapshot(DateTimeOffset at)
    {
        var snapshot = new DemoBackup($"eclat-{at.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}.tar.gz", at, database.ToArray());
        backups.Insert(0, snapshot);
        return snapshot;
    }
    private DemoBackup Find(string name) => backups.FirstOrDefault(backup => backup.Name == name) ?? throw new InvalidOperationException("Sauvegarde de démonstration introuvable.");
    private void Stop() { if (running) totalDisconnections += CurrentPlayers().Count(); running = false; }
    private void Start()
    {
        if (!running)
        {
            gamePid++; startedAt = DateTimeOffset.UtcNow;
            foreach (var player in demoPlayers)
            {
                player.Connected = !maintenance && !IsBanned(player.AccountHash);
                player.ConnectedAt = startedAt;
            }
            running = true;
            totalConnections += CurrentPlayers().Count();
        }
    }
    private static byte[] CreateSyntheticDatabase()
    {
        // Sized dummy content preserves the preview's realistic file sizes; it is not user data.
        byte[] bytes = new byte[2_483_302];
        "SQLite format 3\0DÉMONSTRATION — aucune donnée réelle"u8.CopyTo(bytes);
        return bytes;
    }
    private static object BackupInfo(DemoBackup backup) => new { name = backup.Name, size = backup.Bytes.LongLength, modifiedUtc = backup.Created.ToString("O") };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private string Journal()
    {
        string At(int minutes) => DateTimeOffset.Now.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:sszzz") + " eclat-vps node[1428]: ";
        return "DÉMONSTRATION — données fictives\n" + string.Join("\n", new[] {
            At(64) + "Serveur Éclat démarré sur 127.0.0.1:8787", At(64) + "Base SQLite ouverte, intégrité vérifiée", At(63) + "Annuaire Coop publié",
            At(41) + "Salon créé : Les promeneurs du matin (public)", At(40) + "Aurore a rejoint le salon", At(39) + "Lucas a rejoint le salon",
            At(22) + "Avertissement : tunnel lent, nouvelle tentative", At(21) + "Tunnel reconnecté", At(18) + "Salon créé : Les explorateurs de l’aurore (privé)",
            At(12) + "Refus ROOM_FULL : salon complet", At(6) + "Sauvegarde terminée", At(1) + "Contrôle du protocole : réponse en 4,1 ms" }.Concat(events.Select(message => At(0) + message)));
    }
    private sealed record DemoBackup(string Name, DateTimeOffset Created, byte[] Bytes);
}
