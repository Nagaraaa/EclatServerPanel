using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EclatServerPanel;

public sealed partial class DemoTransport
{
    // Synthetic geography, independent of the game's atlas and names.
    private static readonly object DemoAtlas = new
    {
        version = 1, width = 22, height = 15,
        sections = new object[]
        {
            new { id = "demo-a", name = "Cité des longs noms accentués", x = 4, y = 10, width = 1, height = 1, kind = "city" },
            new { id = "demo-b", name = "Les Jardins de l’aurore boréale", x = 15, y = 5, width = 1, height = 1, kind = "city" },
            new { id = "demo-c", name = "Le Belvédère des brumes légères", x = 9, y = 12, width = 2, height = 1, kind = "area" },
            new { id = "demo-d", name = "Chemin de démonstration", x = 5, y = 10, width = 10, height = 1, kind = "route" },
            new { id = "demo-town-1", name = "Bourg-Lumière", x = 4, y = 13, width = 1, height = 1, kind = "city" },
            new { id = "demo-town-2", name = "Port-Corail", x = 15, y = 10, width = 1, height = 1, kind = "city" },
            new { id = "demo-town-3", name = "Hautevigne", x = 9, y = 3, width = 1, height = 1, kind = "city" },
            new { id = "demo-town-4", name = "Val-Givre", x = 19, y = 2, width = 1, height = 1, kind = "city" },
            new { id = "demo-town-5", name = "Roche-Plume", x = 2, y = 5, width = 1, height = 1, kind = "city" },
            new { id = "demo-town-6", name = "Brisemer", x = 11, y = 8, width = 1, height = 1, kind = "city" },
            new { id = "demo-town-7", name = "Mont-Ardent", x = 19, y = 10, width = 1, height = 1, kind = "city" },
            new { id = "demo-route-1", name = "Sentier des Lucioles", x = 4, y = 11, width = 1, height = 2, kind = "route" },
            new { id = "demo-route-2", name = "Route des Falaises", x = 2, y = 6, width = 1, height = 4, kind = "route" },
            new { id = "demo-route-3", name = "Passage du Gué", x = 3, y = 10, width = 1, height = 1, kind = "route" },
            new { id = "demo-route-4", name = "Route des Pins", x = 3, y = 5, width = 6, height = 1, kind = "route" },
            new { id = "demo-route-5", name = "Montée des Vignes", x = 9, y = 4, width = 1, height = 1, kind = "route" },
            new { id = "demo-route-6", name = "Route des Crêtes", x = 10, y = 3, width = 5, height = 1, kind = "route" },
            new { id = "demo-route-7", name = "Descente du Col", x = 15, y = 3, width = 1, height = 2, kind = "route" },
            new { id = "demo-route-8", name = "Allée des Saules", x = 16, y = 5, width = 3, height = 1, kind = "route" },
            new { id = "demo-route-9", name = "Route du Nord", x = 19, y = 3, width = 1, height = 7, kind = "route" },
            new { id = "demo-route-10", name = "Route des Moulins", x = 15, y = 6, width = 1, height = 4, kind = "route" },
            new { id = "demo-route-11", name = "Quai des Brumes", x = 16, y = 10, width = 3, height = 1, kind = "route" },
            new { id = "demo-route-12", name = "Route du Phare", x = 12, y = 8, width = 3, height = 1, kind = "route" },
            new { id = "demo-route-13", name = "Escalier marin", x = 11, y = 9, width = 1, height = 1, kind = "route" },
            new { id = "demo-route-14", name = "Chenal des Mouettes", x = 5, y = 13, width = 7, height = 1, kind = "route" },
            new { id = "demo-route-15", name = "Chenal du Récif", x = 12, y = 11, width = 1, height = 3, kind = "route" },
            new { id = "demo-area-1", name = "Forêt des Murmures", x = 2, y = 7, width = 1, height = 1, kind = "area" },
            new { id = "demo-area-2", name = "Grotte Opaline", x = 6, y = 5, width = 1, height = 1, kind = "area" },
            new { id = "demo-area-3", name = "Lac Miroir", x = 17, y = 7, width = 1, height = 1, kind = "area" },
            new { id = "demo-area-4", name = "Île aux Échos", x = 17, y = 13, width = 1, height = 1, kind = "area" },
            new { id = "demo-area-5", name = "Tour des Vents", x = 20, y = 2, width = 1, height = 1, kind = "area" }
        }
    };
    // Developer aid: ECLAT_PANEL_DEMO_ATLAS may name a local copy of a server's
    // atlas to check the demo players against it. Nothing of it is published.
    private static readonly JsonElement? AtlasOverride = LoadAtlasOverride();
    internal static bool HasAtlasOverride => AtlasOverride is not null;
    private static JsonElement? LoadAtlasOverride()
    {
        string? path = Environment.GetEnvironmentVariable("ECLAT_PANEL_DEMO_ATLAS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    private static (string Id, string Name) DemoPlace(string section)
    {
        string name = section == "demo-a" ? "Cité des longs noms accentués" : section == "demo-b" ? "Les Jardins de l’aurore boréale" : "Le Belvédère des brumes légères";
        if (AtlasOverride is not { } atlas) return (section, name);
        var sections = atlas.GetProperty("sections").EnumerateArray().ToList();
        var cities = sections.Where(item => item.GetProperty("kind").GetString() == "city").ToList();
        var areas = sections.Where(item => item.GetProperty("kind").GetString() == "area").ToList();
        var pick = section == "demo-a" ? cities[0] : section == "demo-b" ? cities[Math.Min(6, cities.Count - 1)] : areas.Count > 1 ? areas[1] : cities[^1];
        return (pick.GetProperty("id").GetString()!, pick.GetProperty("name").GetString()!);
    }
    private readonly List<DemoPlayer> demoPlayers = [];
    private readonly List<DemoRoom> demoRooms = [];
    private readonly Dictionary<string, DemoAccount> demoAccounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DemoBan> demoBans = new(StringComparer.Ordinal);
    private readonly List<object> adminAudit = [];
    private readonly Dictionary<string, DemoReceipt> adminReceipts = new(StringComparer.Ordinal);
    private readonly List<DemoConfigVersion> configVersions = [];
    private readonly HashSet<string> configRequests = new(StringComparer.Ordinal);
    private JsonElement demoConfig;
    private string configHash = "";
    private DateTimeOffset configUpdatedAt = DateTimeOffset.UtcNow;
    private bool maintenance;
    private int refundCount, refundedCoins;
    private long casinoHand = 400;

    public Task<JsonElement> SendAdminAsync(ConnectionSettings settings, string action, JsonElement fields)
    {
        lock (gate)
        {
            RequireObject(fields);
            object result;
            switch (action)
            {
                case "admin.overview": Fields(fields); RequireRunning(); result = AdministrationOverview(); break;
                case "admin.audit":
                    Fields(fields);
                    result = running ? new { items = adminAudit.AsEnumerable().Reverse().Take(200).ToArray() }
                        : (object)new { items = adminAudit.AsEnumerable().Reverse().Take(200).ToArray(), serverAuditUnavailable = "Service arrêté en démonstration." };
                    break;
                case "admin.command": RequireRunning(); return Task.FromResult(ExecuteDemoCommand(fields));
                case "config.get":
                    Fields(fields);
                    result = new { config = demoConfig, sha256 = configHash, exists = true, status = ConfigStatus(), capabilities = new { configuration = true, history = true, statistics = true } };
                    break;
                case "config.history":
                    Fields(fields);
                    result = new { items = configVersions.Select(item => new { id = item.Id, modifiedUtc = item.Created.ToString("O"), sha256 = item.Sha256, reason = item.Reason }).ToArray() };
                    break;
                case "config.save": case "config.restore": return Task.FromResult(ChangeDemoConfiguration(action, fields));
                case "statistics": Fields(fields); result = DemoStatistics(); break;
                default: throw Error("UNKNOWN_ADMIN_COMMAND", "Action d’administration inconnue.");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
    }

    private void InitializeAdministration()
    {
        string[] roomNames = ["Les promeneurs du matin", "Les explorateurs de l’aurore boréale", "Les amis du belvédère"];
        for (int index = 0; index < roomNames.Length; index++) demoRooms.Add(new DemoRoom(RoomUuid(index), roomNames[index], index == 1 ? "private" : "public", MaxPlayers));
        string[] names = ["Éléonore de la Grande Prairie", "Aurore", "Lucas", "Lou", "Maëlle", "Noé", "Zoé", "Léon", "Chloé", "Anaïs", "Gaël", "Émile", "Théo", "Océane", "Célestine", "Nadège", "Aurélien", "Isaïe", "Éloïse", "Sans position reçue"];
        for (int index = 0; index < 21; index++)
        {
            var account = new DemoAccount(Hash(Encoding.UTF8.GetBytes("synthetic-demo-account-" + index)), index < 20 ? names[index] : "Compte banni de démonstration", 600 + index * 37, index % 9 == 8 ? 255 : (1 << (index % 8)) - 1, 31 + index * 7);
            demoAccounts.Add(account.Hash, account);
            if (index == 20)
            {
                demoBans.Add(account.Hash, new DemoBan(account.Hash, "Exemple de bannissement — données fictives", DateTimeOffset.UtcNow.AddDays(-2), null));
                continue;
            }
            int roomIndex = index < 5 || index >= 17 ? 0 : index < 15 ? 1 : 2;
            string? section = index < 5 ? "demo-a" : index < 17 ? "demo-b" : index < 19 ? "demo-c" : null;
            demoPlayers.Add(new DemoPlayer(PlayerUuid(index), account.Name, account.Hash, RoomUuid(roomIndex), section, index));
        }
        demoConfig = InitialDemoConfiguration();
        configHash = ConfigurationHash(demoConfig);
        RecordConfigVersion(demoConfig, configHash, "Configuration initiale de démonstration", DateTimeOffset.UtcNow.AddDays(-1));
        AppendAudit(Guid.NewGuid().ToString(), "account.ban", demoBans.Keys.First(), "Exemple de bannissement — données fictives", new { ok = true, banned = true });
    }

    private static string PlayerUuid(int index) => $"10000000-0000-4000-8000-{index + 1:000000000000}";
    private static string RoomUuid(int index) => $"20000000-0000-4000-8000-{index + 1:000000000000}";
    private string LegacyRoomId(string value) => value is "r1" or "r2" or "r3" ? RoomUuid(value[1] - '1') : value;
    private IEnumerable<DemoPlayer> CurrentPlayers() => running ? demoPlayers.Where(player => player.Connected) : [];
    private IEnumerable<object> DemoRooms() => demoRooms.Where(room => !closedRooms.Contains(room.Id)).Select(room => (object)new
    {
        id = room.Id, name = room.Name, visibility = room.Visibility, count = CurrentPlayers().Count(player => player.RoomId == room.Id), capacity = room.Capacity
    });
    private object PlayerMonitor(DemoPlayer player)
    {
        var account = demoAccounts[player.AccountHash];
        object? place = player.Section is null ? null : new
        {
            mapGroup = player.Section == "demo-a" ? 4 : player.Section == "demo-b" ? 6 : 8,
            mapNum = 1, x = 5 + player.Index % 7, y = 3 + player.Index % 5, direction = player.Index % 4 + 1,
            name = DemoPlace(player.Section).Name,
            section = DemoPlace(player.Section).Id
        };
        return new { id = player.Id, name = player.Name, roomId = player.RoomId, place,
            stateAgeMs = player.Section is null ? (int?)null : player.Index is 16 or 18 ? 18000 + player.Index * 100 : 250 + player.Index * 70,
            connectedAt = player.ConnectedAt.ToString("O"), progress = new { badges = account.Badges, dex = account.Dex }, accountFingerprint = account.Hash[..8] };
    }

    private object AdministrationOverview() => new
    {
        connectedPlayers = CurrentPlayers().Select(player => new { id = player.Id, name = player.Name, roomId = player.RoomId, accountHash = player.AccountHash }).ToArray(),
        rooms = DemoRooms().ToArray(), maintenance,
        accounts = demoAccounts.Values.Select(account => new { accountHash = account.Hash, name = account.Name, balance = account.Balance,
            createdAt = account.CreatedAt.ToString("O"), badges = account.Badges, dex = account.Dex }).ToArray(),
        bans = demoBans.Values.Where(ban => ban.ExpiresAt is null || ban.ExpiresAt > DateTimeOffset.UtcNow).Select(ban => new
        { accountHash = ban.AccountHash, reason = ban.Reason, createdAt = ban.CreatedAt.ToString("O"), expiresAt = ban.ExpiresAt?.ToString("O") }).ToArray(),
        eclatsConfig = ConfigStatus(), accountsLimit = 500, capabilities = new { commands = true, configuration = true, history = true, statistics = true }
    };

    private JsonElement ExecuteDemoCommand(JsonElement fields)
    {
        Fields(fields, "requestId", "command", "arguments");
        string requestId = Uuid(fields, "requestId"), command = Text(fields, "command");
        var args = Property(fields, "arguments"); RequireObject(args);
        ValidateDemoCommand(command, args);
        string signature = CanonicalJson(JsonSerializer.SerializeToElement(new { command, arguments = args }));
        string? target = args.TryGetProperty("accountHash", out var hashValue) ? hashValue.GetString() : args.TryGetProperty("playerId", out var playerValue) ? playerValue.GetString() : args.TryGetProperty("roomId", out var roomValue) ? roomValue.GetString() : null;
        string? reason = args.TryGetProperty("reason", out _) ? Reason(args) : null;
        if (adminReceipts.TryGetValue(requestId, out var receipt))
        {
            if (receipt.Signature != signature) throw Error("ADMIN_REQUEST_CONFLICT", "Cet identifiant a déjà servi pour une autre commande.");
            if (receipt.Error is not null) throw new InvalidOperationException(receipt.Error);
            AppendCommandAudit(requestId, command, target, reason, receipt.Data);
            return receipt.Data;
        }
        try
        {
            object result;
            switch (command)
            {
                case "player.kick":
                    var player = CurrentPlayers().FirstOrDefault(item => item.Id == Text(args, "playerId")) ?? throw Error("PLAYER_NOT_FOUND", "Joueur introuvable.");
                    DisconnectDemoPlayer(player); result = new { playerId = player.Id, kicked = true }; break;
                case "account.ban":
                    var account = Account(args);
                    DateTimeOffset? expiresAt = Property(args, "expiresAt").ValueKind == JsonValueKind.Null ? null : DateTimeOffset.Parse(Text(args, "expiresAt"), CultureInfo.InvariantCulture);
                    if (expiresAt is not null && (expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddDays(10 * 366))) throw Error("INVALID_ADMIN_ARGUMENT", "Date de fin de bannissement invalide.");
                    demoBans[account.Hash] = new DemoBan(account.Hash, reason!, DateTimeOffset.UtcNow, expiresAt);
                    foreach (var banned in CurrentPlayers().Where(item => item.AccountHash == account.Hash).ToArray()) DisconnectDemoPlayer(banned);
                    result = new { accountHash = account.Hash, banned = true, expiresAt = expiresAt?.ToString("O") }; break;
                case "account.unban":
                    var unbanned = Account(args);
                    if (!demoBans.Remove(unbanned.Hash)) throw Error("BAN_NOT_FOUND", "Bannissement introuvable.");
                    result = new { accountHash = unbanned.Hash, banned = false }; break;
                case "room.close":
                    string roomId = Text(args, "roomId");
                    int removedPlayers = CloseDemoRoom(roomId);
                    result = new { roomId, closed = true, removedPlayers }; break;
                case "room.update":
                    var room = demoRooms.FirstOrDefault(item => item.Id == Text(args, "roomId") && !closedRooms.Contains(item.Id)) ?? throw Error("ROOM_NOT_FOUND", "Salon introuvable.");
                    int capacity = args.TryGetProperty("capacity", out var capacityValue) ? capacityValue.GetInt32() : room.Capacity;
                    int count = CurrentPlayers().Count(item => item.RoomId == room.Id);
                    if (capacity < count) throw Error("ROOM_OCCUPIED", "La capacité ne peut pas être inférieure au nombre de membres.");
                    room.Capacity = capacity;
                    if (args.TryGetProperty("visibility", out var visibilityValue)) room.Visibility = visibilityValue.GetString()!;
                    result = new { roomId = room.Id, capacity = room.Capacity, visibility = room.Visibility, count }; break;
                case "maintenance.set": maintenance = Property(args, "enabled").GetBoolean(); result = new { maintenance, connectedPlayers = CurrentPlayers().Count() }; break;
                case "eclats.adjust":
                    var adjusted = Account(args); int amount = Property(args, "amount").GetInt32();
                    long balance = adjusted.Balance + (long)amount;
                    if (balance < 0 || balance > int.MaxValue) throw Error(balance < 0 ? "INSUFFICIENT_BALANCE" : "BALANCE_LIMIT", "Le solde doit rester entre zéro et 2 147 483 647.");
                    adjusted.Balance = (int)balance;
                    result = new { accountHash = adjusted.Hash, amount, balance, ledgerReason = "admin" }; break;
                case "casino.refund":
                    var refunded = Account(args); int coins = Property(args, "amount").GetInt32();
                    refundCount++; refundedCoins += coins;
                    result = new { accountHash = refunded.Hash, amount = coins, hand = ++casinoHand, deliveryPending = true }; break;
                default: throw Error("UNKNOWN_ADMIN_COMMAND", "Commande inconnue.");
            }
            var data = JsonSerializer.SerializeToElement(result);
            adminReceipts.Add(requestId, new DemoReceipt(signature, data, null));
            AppendCommandAudit(requestId, command, target, reason, data);
            return data;
        }
        catch (InvalidOperationException error)
        {
            adminReceipts.Add(requestId, new DemoReceipt(signature, default, error.Message));
            AppendAudit(requestId, command, target, reason, new { ok = false, code = error.Message.Split(':')[0] });
            throw;
        }
    }

    private static void ValidateDemoCommand(string command, JsonElement args)
    {
        switch (command)
        {
            case "player.kick": Fields(args, "playerId"); Uuid(args, "playerId"); break;
            case "account.ban":
                Fields(args, "accountHash", "reason", "expiresAt"); AccountHash(args); Reason(args);
                var expiry = Property(args, "expiresAt");
                if (expiry.ValueKind != JsonValueKind.Null && (expiry.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw Error("INVALID_ADMIN_ARGUMENT", "Date invalide.");
                break;
            case "account.unban": Fields(args, "accountHash", "reason"); AccountHash(args); if (args.TryGetProperty("reason", out _)) Reason(args); break;
            case "room.close": Fields(args, "roomId"); Uuid(args, "roomId"); break;
            case "room.update":
                Fields(args, "roomId", "capacity", "visibility"); Uuid(args, "roomId");
                bool hasCapacity = args.TryGetProperty("capacity", out _), hasVisibility = args.TryGetProperty("visibility", out var visibility);
                if (!hasCapacity && !hasVisibility) throw Error("INVALID_ADMIN_ARGUMENT", "Indiquez une capacité ou une visibilité.");
                if (hasCapacity) Integer(args, "capacity", 2, MaxPlayers);
                if (hasVisibility && (visibility.ValueKind != JsonValueKind.String || visibility.GetString() is not ("public" or "private"))) throw Error("INVALID_ADMIN_ARGUMENT", "Visibilité invalide.");
                break;
            case "maintenance.set": Fields(args, "enabled"); if (Property(args, "enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error("INVALID_ADMIN_ARGUMENT", "État de maintenance invalide."); break;
            case "eclats.adjust":
                Fields(args, "accountHash", "amount", "reason"); AccountHash(args); Reason(args);
                if (Integer(args, "amount", -int.MaxValue, int.MaxValue) == 0) throw Error("INVALID_ADMIN_ARGUMENT", "Montant nul."); break;
            case "casino.refund": Fields(args, "accountHash", "amount", "reason"); AccountHash(args); Reason(args); Integer(args, "amount", 1, 9999); break;
            default: throw Error("UNKNOWN_ADMIN_COMMAND", "Commande d’administration inconnue.");
        }
    }

    private DemoAccount Account(JsonElement args) => demoAccounts.GetValueOrDefault(AccountHash(args)) ?? throw Error("ACCOUNT_NOT_FOUND", "Compte introuvable.");
    private bool IsBanned(string hash) => demoBans.TryGetValue(hash, out var ban) && (ban.ExpiresAt is null || ban.ExpiresAt > DateTimeOffset.UtcNow);
    private void DisconnectDemoPlayer(DemoPlayer player)
    {
        if (player.Connected)
        {
            player.Connected = false; totalDisconnections++;
            if (player.RoomId is not null) { player.RoomId = null; totalRoomLeaves++; }
        }
    }
    private int CloseDemoRoom(string roomId)
    {
        if (!demoRooms.Any(room => room.Id == roomId) || !closedRooms.Add(roomId)) throw Error("ROOM_NOT_FOUND", "Salon introuvable.");
        int removed = 0;
        foreach (var player in demoPlayers.Where(player => player.RoomId == roomId)) { if (player.Connected && running) removed++; player.RoomId = null; }
        totalRoomLeaves += removed;
        return removed;
    }
    private void AppendAudit(string requestId, string command, string? target, string? reason, object result)
    {
        adminAudit.Add(new { at = DateTimeOffset.UtcNow.ToString("O"), requestId, command, target, reason, result });
        if (adminAudit.Count > 200) adminAudit.RemoveAt(0);
        events.Add("Administration de démonstration : " + command);
    }
    private void AppendCommandAudit(string requestId, string command, string? target, string? reason, JsonElement data)
    {
        var result = new Dictionary<string, object?> { ["ok"] = true };
        string[] allowed = ["amount", "balance", "ledgerReason", "hand", "deliveryPending", "kicked", "banned", "closed", "removedPlayers", "capacity", "visibility", "count", "maintenance", "connectedPlayers", "expiresAt"];
        foreach (var property in data.EnumerateObject()) if (allowed.Contains(property.Name)) result[property.Name] = property.Value.Clone();
        AppendAudit(requestId, command, target, reason, result);
    }
    private void RequireRunning() { if (!running) throw Error("ADMIN_UNAVAILABLE", "Le service de démonstration est arrêté."); }

    private static JsonElement InitialDemoConfiguration() => JsonSerializer.SerializeToElement(new
    {
        version = 1, prices = new { single = 30, ten = 270 }, welcomePack = new { price = 135 },
        gains = new { badge = 50, species = 5, zone = 3, battle = 2, level = 1, duo = 15, daily = 10, welcome = 30, dailyCap = 40, duoCap = 45, groupBonusPercent = 25 },
        sharedPools = new Dictionary<string, int[]> { ["0"] = [1], ["1"] = [4] },
        banners = new[] { new { id = 1, name = "Collection de démonstration", permanent = true, rates = new[] { 7000, 2400, 550, 50 }, shinyOneIn = 256, pity = 50, featured = new[] { 7 }, pools = new Dictionary<string, int[]> { ["2"] = [7] } } },
        events = new[] { new { id = 1, name = "Festival des jardins — évènement actif", kind = "gainMultiplier", value = 2, start = Iso(DateTimeOffset.UtcNow.AddDays(-1)), end = Iso(DateTimeOffset.UtcNow.AddDays(2)) } }
    });
    private object ConfigStatus() => running
        ? new { state = "accepted", sha256 = configHash, loadedSha256 = configHash, updatedAt = configUpdatedAt.ToString("O") }
        : (object)new { state = "pending", sha256 = configHash, message = "Acceptation en attente du démarrage du service de démonstration." };
    private JsonElement ChangeDemoConfiguration(string action, JsonElement fields)
    {
        Fields(fields, action == "config.save" ? ["requestId", "config", "expectedSha256", "reason"] : ["requestId", "version", "expectedSha256", "reason"]);
        string requestId = Uuid(fields, "requestId"), reason = Reason(fields), expected = Text(fields, "expectedSha256");
        if (!Regex.IsMatch(expected, @"\A[a-f0-9]{64}\z")) throw Error("INVALID_ADMIN_ARGUMENT", "Empreinte de configuration invalide.");
        if (!configRequests.Add(requestId)) throw Error("CONFIG_REQUEST_USED", "Identifiant déjà utilisé ; consultez l’historique.");
        try
        {
            if (expected != configHash) throw Error("CONFIG_CONFLICT", "La configuration a changé ; rechargez-la avant de l’enregistrer.");
            var candidate = action == "config.save" ? Property(fields, "config").Clone()
                : configVersions.FirstOrDefault(item => item.Id == Text(fields, "version"))?.Config.Clone() ?? throw Error("CONFIG_NOT_FOUND", "Version de configuration introuvable.");
            ValidateDemoConfiguration(candidate);
            demoConfig = candidate; configHash = ConfigurationHash(candidate); configUpdatedAt = DateTimeOffset.UtcNow;
            string version = RecordConfigVersion(candidate, configHash, reason, configUpdatedAt);
            AppendAudit(requestId, action, "eclats.json", reason, new { ok = running, code = running ? "ACCEPTED" : "PENDING_RELOAD", written = true });
            return JsonSerializer.SerializeToElement(new { sha256 = configHash, accepted = running, status = ConfigStatus(), version });
        }
        catch (InvalidOperationException error)
        {
            AppendAudit(requestId, action, "eclats.json", reason, new { ok = false, code = error.Message.Split(':')[0], written = false });
            throw;
        }
    }
    private string RecordConfigVersion(JsonElement config, string sha256, string reason, DateTimeOffset at)
    {
        string id = "eclats-" + at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".json";
        configVersions.Insert(0, new DemoConfigVersion(id, at, sha256, reason, config.Clone()));
        if (configVersions.Count > 20) configVersions.RemoveRange(20, configVersions.Count - 20);
        return id;
    }
    private static string ConfigurationHash(JsonElement config) => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n"));
    private static void ValidateDemoConfiguration(JsonElement config)
    {
        Fields(config, "version", "prices", "welcomePack", "gains", "sharedPools", "banners", "events", "casino");
        if (Encoding.UTF8.GetByteCount(config.GetRawText()) > 64 * 1024 || Integer(config, "version", 1, 1) != 1) throw Error("CONFIG_INVALID", "Configuration invalide.");
        var prices = Property(config, "prices"); RequireObject(prices); Integer(prices, "single", 1, 65535); Integer(prices, "ten", 1, 65535);
        var gains = Property(config, "gains"); RequireObject(gains);
        foreach (string key in new[] { "badge", "species", "zone", "battle", "level", "duo", "daily", "welcome", "dailyCap", "duoCap", "groupBonusPercent" }) Integer(gains, key, 0, 65535);
        var banners = Property(config, "banners");
        if (banners.ValueKind != JsonValueKind.Array || banners.GetArrayLength() is < 1 or > 9) throw Error("CONFIG_INVALID", "Bannières invalides.");
        foreach (var banner in banners.EnumerateArray())
        {
            RequireObject(banner); Integer(banner, "id", 1, 17); Integer(banner, "shinyOneIn", 1, 65535); Integer(banner, "pity", 1, 255);
            var rates = Property(banner, "rates");
            if (rates.ValueKind != JsonValueKind.Array || rates.GetArrayLength() != 4 || rates.EnumerateArray().Any(rate => !rate.TryGetInt32(out int number) || number < 0 || number > 10000)
                || rates.EnumerateArray().Sum(rate => rate.GetInt32()) != 10000) throw Error("CONFIG_INVALID", "Les taux doivent totaliser 10 000.");
        }
        if (config.TryGetProperty("events", out var eventList))
        {
            if (eventList.ValueKind != JsonValueKind.Array) throw Error("CONFIG_INVALID", "Évènements invalides.");
            foreach (var item in eventList.EnumerateArray())
            {
                RequireObject(item); Integer(item, "value", 1, 65535);
                if (Text(item, "kind") is not ("gainMultiplier" or "shinyOneIn") || !DateTimeOffset.TryParse(Text(item, "start"), out var start) || !DateTimeOffset.TryParse(Text(item, "end"), out var end) || end <= start) throw Error("CONFIG_INVALID", "Dates d’évènement invalides.");
            }
        }
    }

    private object DemoStatistics() => new
    {
        totals = new { accounts = demoAccounts.Count, eclatsInCirculation = demoAccounts.Values.Sum(account => (long)account.Balance), eggs = 127, pulls = 62, shiny = 4 },
        drawsByBanner = new[] { new { banner = 1, eggs = 127, pulls = 62, shiny = 4 } },
        casinoByGame = new[] { new { game = "slots", currency = "eclats", rounds = 42, wagers = 310, payouts = 264 }, new { game = "plinko", currency = "coins", rounds = 18, wagers = 900, payouts = 1070 }, new { game = "blackjack", currency = "coins", rounds = 12, wagers = 600, payouts = 550 } },
        activePlayersByDay = Enumerable.Range(0, 7).Select(index => new { day = DateTimeOffset.UtcNow.AddDays(index - 6).ToString("yyyy-MM-dd"), players = 8 + index * 2 }).ToArray(),
        refundTotals = new { refunds = refundCount, coins = refundedCoins }, periodDays = 30, activityScope = "authenticatedConnections", activityAvailableSince = DateTimeOffset.UtcNow.AddDays(-6).ToString("yyyy-MM-dd"), dayTimezone = "UTC"
    };

    private static void RequireObject(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw Error("INVALID_ADMIN_ARGUMENT", "Objet JSON attendu."); }
    private static void Fields(JsonElement value, params string[] allowed)
    {
        RequireObject(value); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw Error("INVALID_ADMIN_ARGUMENT", "Champ inconnu ou dupliqué.");
    }
    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : throw Error("INVALID_ADMIN_ARGUMENT", "Champ requis : " + key);
    private static string Text(JsonElement value, string key) { var property = Property(value, key); return property.ValueKind == JsonValueKind.String ? property.GetString()! : throw Error("INVALID_ADMIN_ARGUMENT", "Texte attendu : " + key); }
    private static string Uuid(JsonElement value, string key)
    {
        string text = Text(value, key);
        if (!Regex.IsMatch(text, @"\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[1-8][a-fA-F0-9]{3}-[89abAB][a-fA-F0-9]{3}-[a-fA-F0-9]{12}\z")) throw Error("INVALID_ADMIN_ARGUMENT", "Identifiant invalide.");
        return text.ToLowerInvariant();
    }
    private static string AccountHash(JsonElement value) { string hash = Text(value, "accountHash"); return Regex.IsMatch(hash, @"\A[a-f0-9]{64}\z") ? hash : throw Error("INVALID_ADMIN_ARGUMENT", "Empreinte de compte invalide."); }
    private static string Reason(JsonElement value)
    {
        string reason = Text(value, "reason");
        if (reason.Length is < 3 or > 240 || reason != reason.Trim() || reason.Any(character => character < 32 || character == 127)) throw Error("INVALID_ADMIN_ARGUMENT", "Motif obligatoire, de 3 à 240 caractères.");
        return Regex.Replace(reason, @"\b[A-Za-z0-9_-]{43}\b", "[masqué]");
    }
    private static int Integer(JsonElement value, string key, int min, int max) { var field = Property(value, key); return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int number) && number >= min && number <= max ? number : throw Error("INVALID_ADMIN_ARGUMENT", "Nombre invalide : " + key); }
    private static InvalidOperationException Error(string code, string message) => new(code + ": " + message);
    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string CanonicalJson(JsonElement element)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject(); foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); } writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }

    private sealed class DemoPlayer(string id, string name, string accountHash, string? roomId, string? section, int index)
    {
        internal string Id { get; } = id; internal string Name { get; } = name; internal string AccountHash { get; } = accountHash;
        internal string? RoomId { get; set; } = roomId; internal string? Section { get; } = section; internal int Index { get; } = index;
        internal bool Connected { get; set; } = true; internal DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(-12 - index * 4);
    }
    private sealed class DemoRoom(string id, string name, string visibility, int capacity)
    {
        internal string Id { get; } = id; internal string Name { get; } = name; internal string Visibility { get; set; } = visibility; internal int Capacity { get; set; } = capacity;
    }
    private sealed class DemoAccount(string hash, string name, int balance, int badges, int dex)
    {
        internal string Hash { get; } = hash; internal string Name { get; } = name; internal int Balance { get; set; } = balance; internal int Badges { get; } = badges;
        internal int Dex { get; } = dex; internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow.AddDays(-9);
    }
    private sealed record DemoBan(string AccountHash, string Reason, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
    private sealed record DemoReceipt(string Signature, JsonElement Data, string? Error);
    private sealed record DemoConfigVersion(string Id, DateTimeOffset Created, string Sha256, string Reason, JsonElement Config);
}
