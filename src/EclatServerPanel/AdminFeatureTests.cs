using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EclatServerPanel;

/// <summary>Offline acceptance tests for map, command and economy models.</summary>
internal static class AdminFeatureTests
{
    private const string PlayerId = "30000000-0000-4000-8000-000000000001";
    private const string RoomId = "40000000-0000-4000-8000-000000000001";
    private static readonly string Account = new('d', 64);
    private static readonly object Atlas = new
    {
        version = 1, width = 22, height = 15,
        sections = new[] { new { id = "place-a", name = "Place des démonstrateurs", x = 3, y = 4, width = 1, height = 1, kind = "city" },
            new { id = "place-b", name = "Le chemin des essais", x = 4, y = 4, width = 5, height = 1, kind = "route" } }
    };

    internal static void Run()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            DemoGroupsAndMissingPlaces();
            PositionFreshnessUsesTheSnapshotClock();
            InteriorSectionsAndMultipleCoordinates();
            FanNeverOverlapsOrLeavesTheCanvas();
            SortsUseValuesAndBadgeCounts();
            CombinedFiltersAndStableUpdates();
            AtlasRejectsUnsafeGeometry();
            CommandsUseClosedArgumentsAndIdentifiers();
            CommandBoundariesAndDates();
            ConfirmationsKeepExactNamesAndAmounts();
            EconomyEditingPreservesUnchangedData();
            EconomyDatesAndInvalidEdits();
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement DemoMonitor()
        => new DemoTransport().SendAsync(new ConnectionSettings(), "status").GetAwaiter().GetResult().GetProperty("monitor");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Admin features: " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Admin features: refusal missing — " + message);
    }
    private static object Player(string id, string name, string room = "room-a", string? section = "place-a", int x = 7,
        int badges = 1, int dex = 100, double age = 1000, DateTimeOffset? connected = null, string? location = null)
        => new { id, name, roomId = room, place = section is null ? null : (object)new { mapGroup = 4, mapNum = 1, x, y = 3, direction = 2, section, name = location ?? "Place des démonstrateurs" },
            stateAgeMs = section is null ? (double?)null : age, connectedAt = (connected ?? DateTimeOffset.UtcNow.AddHours(-1)).ToString("O"), progress = new { badges, dex } };
    private static JsonElement Monitor(object[] players, DateTimeOffset? at = null) => Json(new
    {
        version = 1, updatedAt = (at ?? DateTimeOffset.UtcNow).ToString("O"), mapAtlas = Atlas, players,
        rooms = new[] { new { id = "room-a", name = "Amis des essais", count = 2, capacity = 10 }, new { id = "room-b", name = "Les autres explorateurs", count = 1, capacity = 10 } }
    });

    private static void DemoGroupsAndMissingPlaces()
    {
        var model = new PlayerMapModel(); model.Update(DemoMonitor(), true);
        Check(model.Players.Count == 20, "twenty players must remain available to the list");
        var groups = model.Groups();
        Check(groups.Any(group => group.Players.Count == 5) && groups.Any(group => group.Players.Count == 12), "grouping must support both fan and large-list cases");
        var twelve = groups.Single(group => group.Players.Count == 12);
        Check(twelve.Players.Select(player => player.RoomId).Distinct().Count() == 2, "geographic grouping must aggregate two different rooms");
        Check(groups.Sum(group => group.Players.Count) == 19, "one missing position must not receive an invented map point");
        Check(model.Players.Count(player => !player.HasPlace) == 1 && model.Players.Count(player => player.HasPlace && player.IsStale) == 2, "unknown and stale positions must remain distinguishable");
        var missing = model.Players.Single(player => !player.HasPlace);
        Check(missing.Location == "Position inconnue" && missing.Map == "—" && missing.Coordinates == "—", "an unlocated player must remain readable in the list");
        model.PlaceFilter = "?";
        Check(model.Players.Where(model.Matches).Single().Id == missing.Id && model.Groups().Count == 0, "filtering unknown places must retain the list entry without a map dot");
    }

    private static void PositionFreshnessUsesTheSnapshotClock()
    {
        var model = new PlayerMapModel();
        model.Update(Monitor([Player("old", "Donnée ancienne", age: 200)], DateTimeOffset.UtcNow.AddSeconds(-12)), true);
        var old = model.Players.Single();
        Check(old.BaseAgeMs == 200 && old.AgeMs >= 12200 && old.IsStale, "a fresh-looking position inside an old snapshot must already be stale");
        model.Update(Monitor([Player("old", "Donnée récente", age: 200)], DateTimeOffset.UtcNow.AddMinutes(2)), true);
        Check(ReferenceEquals(old, model.Players.Single()) && old.AgeMs is >= 200 and < 1000 && !old.IsStale, "clock skew must not create a negative age");
        model.Update(Monitor([Player("old", "Donnée récente")]), false);
        Check(model.Players.Count == 0 && model.Groups().Count == 0, "unavailable monitoring must not keep showing old players as online");
    }

    private static void InteriorSectionsAndMultipleCoordinates()
    {
        var model = new PlayerMapModel();
        model.Update(Monitor([
            Player("indoor", "Dans un bâtiment", x: 2, location: "Maison de démonstration"),
            Player("outdoor", "Dehors", x: 18),
            Player("off-atlas", "Ailleurs", section: "another-region", location: "Lieu hors de la carte")
        ]), true);
        var group = model.Groups().Single();
        Check(group.Section.Id == "place-a" && group.Players.Count == 2, "an interior and exterior must share their server-provided section");
        Check(model.Players.Single(player => player.Id == "indoor").Location == "Maison de démonstration", "the interior place name must survive grouping");
        Check(group.Players.Select(player => player.Coordinates).Distinct().Count() == 2, "players sharing a map must retain individual tile coordinates");
        Check(model.Players.Count == 3 && model.Players.Single(player => player.Id == "off-atlas").Location == "Lieu hors de la carte", "a named place outside Kanto must remain in the list without a fabricated dot");
    }

    private static void FanNeverOverlapsOrLeavesTheCanvas()
    {
        const double width = 1000, height = 260, radius = 14;
        (double X, double Y)[] centers = [(500, 130), (1, 130), (999, 130), (500, 1), (500, 259), (1, 1), (999, 259)];
        foreach (var center in centers)
            for (int count = 2; count <= 8; count++)
            {
                var points = PlayerMapModel.Fan(center.X, center.Y, count, width, height);
                Check(points.Count == count, $"fan must include all {count} players near each edge");
                foreach (var point in points) Check(point.X - radius >= 0 && point.X + radius <= width && point.Y - radius >= 0 && point.Y + radius <= height, "a player circle extends beyond the canvas");
                for (int left = 0; left < count; left++)
                    for (int right = left + 1; right < count; right++)
                    {
                        double dx = points[left].X - points[right].X, dy = points[left].Y - points[right].Y;
                        Check(Math.Sqrt(dx * dx + dy * dy) >= 37.999, "28-pixel fan circles must retain at least a ten-pixel gap");
                    }
            }
        Check(PlayerMapModel.Fan(500, 130, 12, width, height).Count == 0, "large groups must use a list instead of an overlapping fan");
    }

    private static void SortsUseValuesAndBadgeCounts()
    {
        var model = new PlayerMapModel(); var now = DateTimeOffset.UtcNow;
        model.Update(Monitor([
            Player("a", "Anaïs", badges: 128, dex: 100, age: 1000, connected: now.AddHours(-1)),
            Player("b", "Émile", badges: 3, dex: 50, age: 3000, connected: now.AddHours(-3)),
            Player("c", "Zoé", badges: 0, dex: 200, age: 2000, connected: now.AddHours(-2))
        ], now), true);
        foreach (var test in new[] { ("name", "abc"), ("badges", "cab"), ("dex", "bac"), ("age", "acb"), ("connected", "bca") })
        {
            string sorted = string.Concat(PlayerMapModel.Sort(model.Players, test.Item1).Select(player => player.Id));
            Check(sorted == test.Item2, "column sorting is incorrect for " + test.Item1);
            Check(string.Concat(PlayerMapModel.Sort(model.Players, test.Item1, true).Select(player => player.Id)) == new string(test.Item2.Reverse().ToArray()), "descending order is incorrect for " + test.Item1);
        }
        Check(model.Players[0].BadgesText == "1 / 8" && model.Players[1].BadgesText == "2 / 8", "badges must display the mask's bit count, not its numeric value");
    }

    private static void CombinedFiltersAndStableUpdates()
    {
        var model = new PlayerMapModel();
        model.Update(Monitor([Player("a", "Anaïs"), Player("b", "Émile", room: "room-b", section: "place-b"), Player("c", "ANAIS", room: "room-b")]), true);
        model.Search = "aNaIs"; model.RoomFilter = "room-a"; model.PlaceFilter = "place-a";
        Check(model.Players.Where(model.Matches).Select(player => player.Id).SequenceEqual(["a"]), "accent-insensitive search must combine with room and place filters");
        model.RoomFilter = "room-b";
        Check(model.Players.Where(model.Matches).Select(player => player.Id).SequenceEqual(["c"]), "room filters must affect a shared place group");
        model.PlaceFilter = "place-b";
        Check(!model.Players.Any(model.Matches), "all filters must apply together");
        var selected = model.Players.Single(player => player.Id == "a"); int notifications = 0;
        selected.PropertyChanged += (_, _) => notifications++;
        model.Update(Monitor([Player("a", "Anaïs renommée", x: 19), Player("c", "ANAIS", room: "room-b")]), true);
        Check(ReferenceEquals(selected, model.Players.Single(player => player.Id == "a")) && selected.Name == "Anaïs renommée" && selected.Coordinates == "19, 3" && notifications > 0, "refresh must update the selected model object in place");
        Check(model.Search == "aNaIs" && model.RoomFilter == "room-b" && model.PlaceFilter == "place-b", "refresh must preserve search and filters");
        Check(model.Players.All(player => player.Id != "b"), "disconnected players must leave the list");
    }

    private static void AtlasRejectsUnsafeGeometry()
    {
        var invalid = Json(new { version = 1, width = 22, height = 15, sections = new[] {
            new { id = "negative", name = "Hors limites", x = -1, y = 0, width = 1, height = 1, kind = "city" },
            new { id = "too-wide", name = "Hors limites", x = 21, y = 0, width = 2, height = 1, kind = "city" },
            new { id = "zero", name = "Sans taille", x = 2, y = 2, width = 0, height = 1, kind = "city" } } });
        Check(MapAtlas.Parse(invalid).Sections.Count == 0, "invalid atlas rectangles must never reach drawing coordinates");
        Check(MapAtlas.Parse(Json(new { width = 0, height = 15, sections = new object[0] })).Sections.Count == 0, "a zero-width atlas must be unavailable");
    }

    private static void CommandsUseClosedArgumentsAndIdentifiers()
    {
        Reject(() => AdminContract.Request("terminal", Json(new { command = "ls" })), "a remote terminal action was accepted");
        Reject(() => AdminContract.Request("statistics", Json(new { action = "restart" })), "an action field overrode a read-only request");
        Reject(() => AdminContract.ValidateCommand("player.kick", Json(new { playerId = PlayerId, script = "anything" })), "unknown command arguments were accepted");
        Reject(() => AdminContract.ValidateCommand("unlisted.command", Json(new { })), "an unlisted command was accepted");
        foreach (string id in new[] { "not-an-id", Guid.Empty.ToString(), "30000000-0000-9000-8000-000000000001", "30000000-0000-4000-7000-000000000001" })
            Reject(() => AdminContract.ValidateCommand("player.kick", Json(new { playerId = id })), "invalid protocol UUID " + id);
        using var duplicate = JsonDocument.Parse("{\"playerId\":\"" + PlayerId + "\",\"playerId\":\"" + PlayerId + "\"}");
        Reject(() => AdminContract.ValidateCommand("player.kick", duplicate.RootElement), "duplicate fields were accepted");
        foreach (string hash in new[] { "abcd1234", new string('a', 43), new string('a', 65), new string('A', 64) })
            Reject(() => AdminContract.ValidateCommand("eclats.adjust", Json(new { accountHash = hash, amount = 1, reason = "Motif de test" })), "a short fingerprint, key or malformed hash became a command target");
        Reject(() => AdminContract.ParseResponse("{\"ok\":false,\"ok\":true,\"data\":{}}"), "an ambiguous response could claim success");
        var envelope = AdminContract.Request("admin.command", AdminContract.CommandFields(AdminContract.Plan("player.kick", Json(new { playerId = PlayerId }), "Joueur")));
        using var parsed = JsonDocument.Parse(envelope);
        Check(parsed.RootElement.GetProperty("action").GetString() == "admin.command" && Guid.TryParse(parsed.RootElement.GetProperty("requestId").GetString(), out _), "wire requests need an action and a fresh operation ID");
    }

    private static void CommandBoundariesAndDates()
    {
        foreach (int amount in new[] { -int.MaxValue, -1, 1, int.MaxValue }) AdminContract.ValidateCommand("eclats.adjust", Json(new { accountHash = Account, amount, reason = "Ajustement fictif" }));
        foreach (object amount in new object[] { 0, int.MinValue, 1.5, "1" }) Reject(() => AdminContract.ValidateCommand("eclats.adjust", Json(new { accountHash = Account, amount, reason = "Ajustement fictif" })), "an invalid adjustment amount was accepted");
        foreach (int amount in new[] { 1, 9999 }) AdminContract.ValidateCommand("casino.refund", Json(new { accountHash = Account, amount, reason = "Remboursement fictif" }));
        foreach (int amount in new[] { -1, 0, 10000 }) Reject(() => AdminContract.ValidateCommand("casino.refund", Json(new { accountHash = Account, amount, reason = "Remboursement fictif" })), "casino refund outside 1..9999");
        foreach (int capacity in new[] { 2, 10 }) AdminContract.ValidateCommand("room.update", Json(new { roomId = RoomId, capacity }));
        foreach (object capacity in new object[] { 0, 1, 11, 1.5, "10" }) Reject(() => AdminContract.ValidateCommand("room.update", Json(new { roomId = RoomId, capacity })), "capacity outside the protocol's 2..10 range");
        Reject(() => AdminContract.ValidateCommand("room.update", Json(new { roomId = RoomId })), "an empty room update was accepted");
        foreach (string reason in new[] { "", "ab", " leading", "trailing ", "line\nbreak", "tab\there", "nul\0here", new string('x', 241) })
            Reject(() => AdminContract.ValidateCommand("eclats.adjust", Json(new { accountHash = Account, amount = 1, reason })), "an invalid reason was accepted");
        void Ban(string? expiresAt) => AdminContract.ValidateCommand("account.ban", Json(new { accountHash = Account, reason = "Motif de bannissement", expiresAt }));
        Ban(null); Ban(DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        int futureYear = DateTimeOffset.UtcNow.Year + 1;
        foreach (string expiry in new[] { DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), DateTimeOffset.UtcNow.AddYears(11).ToString("O"),
            $"{futureYear}-02-30T12:00:00Z", $"{futureYear}-03-02", $"{futureYear}-03-02T12:00:00.0000000Z", $"{futureYear}-03-02T12:00:00+02:00" })
            Reject(() => Ban(expiry), "a ban date incompatible with the server protocol was accepted: " + expiry);
    }

    private static void ConfirmationsKeepExactNamesAndAmounts()
    {
        const string longName = "Éléonore de la Grande Prairie";
        var ban = AdminContract.Plan("account.ban", Json(new { accountHash = Account, reason = "Motif de démonstration", expiresAt = (string?)null }), longName);
        Check(ban.NeedsConfirmation && ban.Confirmation == longName && ban.Message.Contains("nouvelle clé"), "ban confirmation must require the exact name and explain the account limitation");
        const string roomName = "Les amis de l’aurore boréale";
        Check(AdminContract.Plan("room.close", Json(new { roomId = RoomId }), roomName).Confirmation == roomName, "room closure must require the complete room name");
        foreach (int amount in new[] { -15, 2147483647 })
            Check(AdminContract.Plan("eclats.adjust", Json(new { accountHash = Account, amount, reason = "Ajustement fictif" }), longName).Confirmation == amount.ToString(CultureInfo.InvariantCulture), "money confirmation must require the exact signed amount");
        Check(!AdminContract.Plan("room.update", Json(new { roomId = RoomId, visibility = "public" }), roomName).NeedsConfirmation, "a room update must not add an unrequested confirmation");
        Check(AdminContract.Plan("player.kick", Json(new { playerId = PlayerId }), longName).Confirmation is null, "kick requires a simple confirmation");
        Check(AdminContract.MaskAccount("Compte " + Account) == "Compte " + Account[..8], "full account fingerprints must be hidden in visible text");
    }

    private static JsonElement DemoConfiguration() => new DemoTransport().SendAdminAsync(new ConnectionSettings(), "config.get", Json(new { })).GetAwaiter().GetResult().GetProperty("config");
    private static void Same(JsonElement left, JsonElement right, string message) => Check(JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText())), message);
    private static void EconomyEditingPreservesUnchangedData()
    {
        var original = DemoConfiguration(); var editor = new EconomyEditor(); editor.Load(original);
        editor.AddEvent(); var added = editor.Events.Last(); added.Name = "Chromatiques de démonstration"; added.Kind = "Chromatiques (1 sur N)"; added.Value = 128;
        added.Start = "02/03/2030 14:30"; added.End = "04/03/2030 14:30";
        editor.AddBanner(); var limited = editor.Banners.Last(); limited.Name = "Une bannière avec des accents"; limited.Start = "02/03/2030 14:30"; limited.End = "04/03/2030 14:30";
        limited.Rates = "6000 / 3000 / 900 / 100"; limited.ShinyOneIn = 128; limited.Pity = 60;
        var exported = editor.Export(35, 300);
        foreach (string key in new[] { "sharedPools", "gains", "welcomePack" }) Same(original.GetProperty(key), exported.GetProperty(key), "editing prices or schedules changed " + key);
        Same(original.GetProperty("banners")[0].GetProperty("pools"), exported.GetProperty("banners")[0].GetProperty("pools"), "editing banner presentation changed species pools");
        Same(original.GetProperty("banners")[0].GetProperty("pools"), exported.GetProperty("banners")[1].GetProperty("pools"), "adding a limited banner lost its inherited pools");
        Check(exported.GetProperty("prices").GetProperty("single").GetInt32() == 35 && exported.GetProperty("prices").GetProperty("ten").GetInt32() == 300, "edited prices were not exported");
        Check(exported.GetProperty("events")[1].GetProperty("kind").GetString() == "shinyOneIn" && exported.GetProperty("events")[1].GetProperty("value").GetInt32() == 128, "event editing lost its kind or value");
        Check(exported.GetProperty("banners")[1].GetProperty("id").GetInt32() == 10 && !exported.GetProperty("banners")[1].GetProperty("permanent").GetBoolean(), "a limited banner needs an available ID and dates");
        Check(!exported.GetProperty("banners")[0].TryGetProperty("start", out _) && !exported.GetProperty("banners")[0].TryGetProperty("end", out _), "the permanent banner must stay unscheduled");
        editor.Events.Remove(added); editor.Banners.Remove(limited);
        var removed = editor.Export(35, 300);
        Check(removed.GetProperty("events").GetArrayLength() == original.GetProperty("events").GetArrayLength() && removed.GetProperty("banners").GetArrayLength() == 1, "removing newly added rows must preserve the existing configuration");
    }

    private static void EconomyDatesAndInvalidEdits()
    {
        string exported = EconomyEditor.ExportDate("02/03/2030 14:30");
        Check(exported == "2030-03-02T14:30:00.000Z" && Regex.IsMatch(exported, @"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,3})?Z\z"), "editor dates must be UTC with at most three fractional digits");
        Reject(() => EconomyEditor.ExportDate("30/02/2030 14:30"), "an invalid calendar date was exported");
        var defaults = new EconomyEditor(); defaults.Load(DemoConfiguration()); defaults.AddEvent(); defaults.AddBanner();
        var defaultExport = defaults.Export(30, 270);
        foreach (var row in new[] { defaultExport.GetProperty("events")[1], defaultExport.GetProperty("banners")[1] })
            foreach (string key in new[] { "start", "end" }) Check(Regex.IsMatch(row.GetProperty(key).GetString()!, @"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,3})?Z\z"), "a newly added schedule must be immediately compatible with the game validator");
        var original = JsonNode.Parse(DemoConfiguration().GetRawText())!;
        original["events"]![0]!["start"] = "2030-03-02T16:30:25.123+02:00";
        original["events"]![0]!["end"] = "2030-03-04T16:30:55.456+02:00";
        var editor = new EconomyEditor(); editor.Load(Json(original));
        var roundtrip = editor.Export(35, 300).GetProperty("events")[0];
        Check(PanelJson.Date(roundtrip.GetProperty("start")) == DateTimeOffset.Parse("2030-03-02T14:30:25.123Z", CultureInfo.InvariantCulture)
            && PanelJson.Date(roundtrip.GetProperty("end")) == DateTimeOffset.Parse("2030-03-04T14:30:55.456Z", CultureInfo.InvariantCulture), "changing prices must not round unchanged event times down to the minute");
        Reject(() => editor.Export(0, 300), "a zero price was exported"); Reject(() => editor.Export(35, 65536), "an oversized price was exported");
        editor.Banners[0].Rates = "6000 / 3000 / 999 / 0";
        Reject(() => editor.Export(35, 300), "banner probabilities did not sum to 10 000");
    }
}
