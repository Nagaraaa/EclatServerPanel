using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace EclatServerPanel;

internal static class PanelJson
{
    internal static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    internal static string String(JsonElement value, string fallback = "") => value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    internal static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    internal static string Text(JsonElement value, string fallback = "—") => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback : value.ToString();
    internal static DateTimeOffset? Date(JsonElement value) => DateTimeOffset.TryParse(String(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    internal static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}

internal sealed record MapSection(string Id, string Name, double X, double Y, double Width, double Height, string Kind);
internal sealed record MapAtlas(double Width, double Height, IReadOnlyList<MapSection> Sections)
{
    internal static MapAtlas Empty { get; } = new(1, 1, []);
    internal static MapAtlas Parse(JsonElement value)
    {
        double width = PanelJson.Number(PanelJson.Get(value, "width")) ?? 0, height = PanelJson.Number(PanelJson.Get(value, "height")) ?? 0;
        if (width is <= 0 or > 256 || height is <= 0 or > 256) return Empty;
        var sections = new List<MapSection>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in PanelJson.Array(PanelJson.Get(value, "sections")).Take(512))
        {
            string id = PanelJson.Text(PanelJson.Get(item, "id"), ""), name = PanelJson.String(PanelJson.Get(item, "name"));
            double x = PanelJson.Number(PanelJson.Get(item, "x")) ?? -1, y = PanelJson.Number(PanelJson.Get(item, "y")) ?? -1;
            double w = PanelJson.Number(PanelJson.Get(item, "width")) ?? 0, h = PanelJson.Number(PanelJson.Get(item, "height")) ?? 0;
            if (id.Length == 0 || !ids.Add(id) || x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height) continue;
            sections.Add(new(id, name, x, y, w, h, PanelJson.String(PanelJson.Get(item, "kind"), "area")));
        }
        return new(width, height, sections);
    }
}

internal sealed class MapPlayer : INotifyPropertyChanged
{
    public string Id { get; }
    public string Name { get; private set; } = "";
    public string RoomId { get; private set; } = "";
    public string Room { get; private set; } = "Hors salon";
    public string SectionId { get; private set; } = "";
    public string Location { get; private set; } = "Position inconnue";
    public string Coordinates { get; private set; } = "—";
    public string Map { get; private set; } = "—";
    public int? Badges { get; private set; }
    public int? Dex { get; private set; }
    public string BadgesText => Badges is { } n ? $"{BadgeCount} / 8" : "—";
    public int BadgeCount => Badges is { } n ? System.Numerics.BitOperations.PopCount((uint)n) : 0;
    public bool[] BadgePips => Enumerable.Range(0, 8).Select(index => Badges is { } n && (n & (1 << index)) != 0).ToArray();
    public double DexRatio => Dex is { } n ? Math.Clamp(n / 386d, 0, 1) : 0;
    public int RoomTone => MapPalette.Tone(RoomId);
    public System.Windows.Media.Brush RoomBrush => MapPalette.Brush(RoomTone);
    public string Initial => Name.Length == 0 ? "?" : Name[..1].ToUpper(CultureInfo.GetCultureInfo("fr-FR"));
    public string Freshness => AgeMs is not { } age ? "Position inconnue" : age > 10000 ? $"Position ancienne · {Age}" : $"En direct · {Age}";
    public string DexText => Dex?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public DateTimeOffset? ConnectedAt { get; private set; }
    public double? BaseAgeMs { get; private set; }
    private DateTimeOffset sampleAt;
    public bool HasPlace { get; private set; }
    public double? AgeMs => BaseAgeMs is { } age ? age + Math.Max(0, (DateTimeOffset.UtcNow - sampleAt).TotalMilliseconds) : null;
    public bool IsStale => AgeMs is not { } age || age > 10000;
    public string Age => AgeMs is { } age ? age < 1000 ? "à l’instant" : $"{(int)(age / 1000)} s" : "inconnu";
    public string Connected => ConnectedAt is { } at ? Duration(Math.Max(0, (DateTimeOffset.UtcNow - at).TotalSeconds)) : "—";
    public string Detail => $"{Name}\n{Location} · {Room}\nCarte {Map} · {Coordinates}\nPosition : {Age}";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal MapPlayer(string id) => Id = id;
    internal void Update(JsonElement value, IReadOnlyDictionary<string, string> rooms, DateTimeOffset sampled)
    {
        Name = PanelJson.String(PanelJson.Get(value, "name"), "Joueur");
        RoomId = PanelJson.String(PanelJson.Get(value, "roomId"));
        Room = rooms.TryGetValue(RoomId, out var room) ? room : RoomId.Length == 0 ? "Hors salon" : RoomId;
        var place = PanelJson.Get(value, "place");
        HasPlace = place.ValueKind == JsonValueKind.Object;
        SectionId = HasPlace ? PanelJson.Text(PanelJson.Get(place, "section"), "") : "";
        Location = HasPlace ? PanelJson.String(PanelJson.Get(place, "name"), "Lieu non renseigné") : "Position inconnue";
        Map = HasPlace ? $"{PanelJson.Text(PanelJson.Get(place, "mapGroup"))}:{PanelJson.Text(PanelJson.Get(place, "mapNum"))}" : "—";
        Coordinates = HasPlace ? $"{PanelJson.Text(PanelJson.Get(place, "x"))}, {PanelJson.Text(PanelJson.Get(place, "y"))}" : "—";
        ConnectedAt = PanelJson.Date(PanelJson.Get(value, "connectedAt"));
        BaseAgeMs = PanelJson.Number(PanelJson.Get(value, "stateAgeMs")) is { } age && age >= 0 ? age : null;
        sampleAt = sampled;
        var progress = PanelJson.Get(value, "progress");
        Badges = PanelJson.Number(PanelJson.Get(progress, "badges")) is { } badges && badges is >= 0 and <= 255 ? (int)badges : null;
        Dex = PanelJson.Number(PanelJson.Get(progress, "dex")) is { } dex && dex is >= 0 and <= 386 ? (int)dex : null;
        Tick();
    }
    internal void Tick() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    private static string Duration(double seconds) => seconds >= 86400 ? $"{(int)(seconds / 86400)} j {(int)(seconds / 3600) % 24} h" : seconds >= 3600 ? $"{(int)(seconds / 3600)} h {(int)(seconds / 60) % 60} min" : $"{(int)(seconds / 60)} min";
}

internal sealed record MapGroup(MapSection Section, IReadOnlyList<MapPlayer> Players);
internal readonly record struct FanPoint(double X, double Y);
internal sealed class PlayerMapModel
{
    public ObservableCollection<MapPlayer> Players { get; } = [];
    public MapAtlas Atlas { get; private set; } = MapAtlas.Empty;
    public string Search { get; set; } = "";
    public string RoomFilter { get; set; } = "";
    public string PlaceFilter { get; set; } = "";
    internal void Update(JsonElement monitor, bool available)
    {
        var atlas = MapAtlas.Parse(PanelJson.Get(monitor, "mapAtlas"));
        if (atlas.Sections.Count > 0) Atlas = atlas;
        var rooms = PanelJson.Array(PanelJson.Get(monitor, "rooms")).ToDictionary(room => PanelJson.String(PanelJson.Get(room, "id")), room => PanelJson.String(PanelJson.Get(room, "name")));
        var seen = new HashSet<string>();
        var sampled = PanelJson.Date(PanelJson.Get(monitor, "updatedAt")) ?? DateTimeOffset.UtcNow;
        if (available)
            foreach (var value in PanelJson.Array(PanelJson.Get(monitor, "players")).Take(1280))
            {
                string id = PanelJson.String(PanelJson.Get(value, "id"));
                if (id.Length == 0 || !seen.Add(id)) continue;
                var player = Players.FirstOrDefault(p => p.Id == id);
                if (player is null) { player = new(id); Players.Add(player); }
                player.Update(value, rooms, sampled);
            }
        for (int index = Players.Count - 1; index >= 0; index--) if (!seen.Contains(Players[index].Id)) Players.RemoveAt(index);
    }
    internal bool Matches(MapPlayer player) => (Search.Length == 0 || CultureInfo.GetCultureInfo("fr-FR").CompareInfo.IndexOf(player.Name, Search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
        && (RoomFilter.Length == 0 || player.RoomId == RoomFilter) && (PlaceFilter.Length == 0 || (player.SectionId.Length == 0 ? "?" : player.SectionId) == PlaceFilter);
    internal IReadOnlyList<MapGroup> Groups() => Players.Where(Matches).Where(player => player.HasPlace)
        .GroupBy(player => player.SectionId).Select(group => Atlas.Sections.FirstOrDefault(section => section.Id == group.Key) is { } section ? new MapGroup(section, group.ToList()) : null)
        .OfType<MapGroup>().ToList();
    internal void Clear() { Players.Clear(); Atlas = MapAtlas.Empty; Search = RoomFilter = PlaceFilter = ""; }
    internal static IReadOnlyList<FanPoint> Fan(double x, double y, int count, double width, double height, double diameter = 28)
    {
        if (count is < 1 or > 8 || width < 200 || height < 200) return [];
        double radius = Math.Max(44, (diameter + 10) / (2 * Math.Sin(Math.PI / Math.Max(2, count))));
        double centerX = Math.Clamp(x, radius + diameter, width - radius - diameter), centerY = Math.Clamp(y, radius + diameter, height - radius - diameter);
        return Enumerable.Range(0, count).Select(index => { double angle = -Math.PI / 2 + index * 2 * Math.PI / count; return new FanPoint(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle)); }).ToList();
    }
    internal static IEnumerable<MapPlayer> Sort(IEnumerable<MapPlayer> players, string column, bool descending = false)
    {
        IOrderedEnumerable<MapPlayer> sorted = column switch
        {
            "room" => players.OrderBy(p => p.Room, StringComparer.CurrentCultureIgnoreCase),
            "place" => players.OrderBy(p => p.Location, StringComparer.CurrentCultureIgnoreCase),
            "badges" => players.OrderBy(p => p.Badges is { } n ? System.Numerics.BitOperations.PopCount((uint)n) : -1),
            "dex" => players.OrderBy(p => p.Dex ?? -1),
            "age" => players.OrderBy(p => p.AgeMs ?? double.MaxValue),
            "connected" => players.OrderBy(p => p.ConnectedAt),
            _ => players.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        return descending ? sorted.Reverse() : sorted;
    }
}
