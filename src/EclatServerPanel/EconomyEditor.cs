using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EclatServerPanel;

internal sealed class EconomyEditor
{
    private JsonObject configuration = new();
    public ObservableCollection<EventEditorRow> Events { get; } = [];
    public ObservableCollection<BannerEditorRow> Banners { get; } = [];
    internal void Load(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Le serveur n’a pas fourni de configuration modifiable.");
        configuration = JsonNode.Parse(value.GetRawText())!.AsObject(); Events.Clear(); Banners.Clear();
        foreach (var item in PanelJson.Array(PanelJson.Get(value, "events"))) Events.Add(new(item));
        foreach (var item in PanelJson.Array(PanelJson.Get(value, "banners"))) Banners.Add(new(item));
    }
    internal int Price(string name) => configuration["prices"]?[name]?.GetValue<int>() ?? 0;
    internal JsonElement Export(int single, int ten)
    {
        if (single is < 1 or > 65535 || ten is < 1 or > 65535) throw new InvalidOperationException("Les prix doivent être compris entre 1 et 65 535 Éclats.");
        var result = configuration.DeepClone().AsObject(); result["prices"] = new JsonObject { ["single"] = single, ["ten"] = ten };
        result["events"] = new JsonArray(Events.Select(row => (JsonNode)row.Export()).ToArray());
        result["banners"] = new JsonArray(Banners.Select(row => (JsonNode)row.Export()).ToArray());
        return JsonSerializer.SerializeToElement(result);
    }
    internal void AddEvent()
    {
        int id = Events.Count == 0 ? 1 : Events.Max(row => row.Id) + 1;
        Events.Add(new(JsonSerializer.SerializeToElement(new { id, name = "Nouvel évènement", kind = "gainMultiplier", value = 2, start = ConfigDate(DateTimeOffset.UtcNow), end = ConfigDate(DateTimeOffset.UtcNow.AddDays(7)) })));
    }
    internal void AddBanner()
    {
        int id = Enumerable.Range(10, 8).FirstOrDefault(id => Banners.All(row => row.Id != id));
        if (id == 0 || Banners.Count == 0) throw new InvalidOperationException("Aucun emplacement de bannière limitée n’est disponible.");
        var source = Banners[0].Export(); source["id"] = id; source["name"] = "Nouvelle bannière"; source["permanent"] = false;
        source["start"] = ConfigDate(DateTimeOffset.UtcNow); source["end"] = ConfigDate(DateTimeOffset.UtcNow.AddDays(7));
        Banners.Add(new(JsonSerializer.SerializeToElement(source)));
    }
    internal static string DisplayDate(JsonElement value) => PanelJson.Date(value)?.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")) ?? "";
    private static string ConfigDate(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    internal static string ExportDate(string value)
    {
        if (!DateTime.TryParseExact(value.Trim(), "dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out var at)) throw new InvalidOperationException("Indiquez une date UTC au format jj/mm/aaaa hh:mm.");
        return DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}

internal sealed class EventEditorRow
{
    private readonly string originalStart, originalEnd, displayedStart, displayedEnd;
    public int Id { get; set; }
    public string Name { get; set; }
    public string Kind { get; set; }
    public int Value { get; set; }
    public string Start { get; set; }
    public string End { get; set; }
    internal EventEditorRow(JsonElement value)
    {
        Id = (int)(PanelJson.Number(PanelJson.Get(value, "id")) ?? 0); Name = PanelJson.String(PanelJson.Get(value, "name"));
        Kind = PanelJson.String(PanelJson.Get(value, "kind")) == "shinyOneIn" ? "Chromatiques (1 sur N)" : "Gains multipliés";
        Value = (int)(PanelJson.Number(PanelJson.Get(value, "value")) ?? 1); Start = EconomyEditor.DisplayDate(PanelJson.Get(value, "start")); End = EconomyEditor.DisplayDate(PanelJson.Get(value, "end"));
        originalStart = PanelJson.String(PanelJson.Get(value, "start")); originalEnd = PanelJson.String(PanelJson.Get(value, "end")); displayedStart = Start; displayedEnd = End;
    }
    internal JsonObject Export() => new() { ["id"] = Id, ["name"] = Name, ["kind"] = Kind == "Chromatiques (1 sur N)" ? "shinyOneIn" : "gainMultiplier", ["value"] = Value, ["start"] = Start == displayedStart ? originalStart : EconomyEditor.ExportDate(Start), ["end"] = End == displayedEnd ? originalEnd : EconomyEditor.ExportDate(End) };
}

internal sealed class BannerEditorRow
{
    private readonly JsonObject source;
    private readonly string displayedStart, displayedEnd;
    public int Id { get; set; }
    public string Name { get; set; }
    public bool Permanent { get; set; }
    public string Start { get; set; }
    public string End { get; set; }
    public int ShinyOneIn { get; set; }
    public int Pity { get; set; }
    public string Rates { get; set; }
    public string Featured { get; set; }
    internal BannerEditorRow(JsonElement value)
    {
        source = JsonNode.Parse(value.GetRawText())!.AsObject(); Id = (int)(PanelJson.Number(PanelJson.Get(value, "id")) ?? 0); Name = PanelJson.String(PanelJson.Get(value, "name"));
        Permanent = PanelJson.Get(value, "permanent").ValueKind == JsonValueKind.True; Start = EconomyEditor.DisplayDate(PanelJson.Get(value, "start")); End = EconomyEditor.DisplayDate(PanelJson.Get(value, "end"));
        displayedStart = Start; displayedEnd = End;
        ShinyOneIn = (int)(PanelJson.Number(PanelJson.Get(value, "shinyOneIn")) ?? 4096); Pity = (int)(PanelJson.Number(PanelJson.Get(value, "pity")) ?? 80);
        Rates = string.Join(" / ", PanelJson.Array(PanelJson.Get(value, "rates")).Select(rate => PanelJson.Text(rate)));
        Featured = string.Join(", ", PanelJson.Array(PanelJson.Get(value, "featured")).Select(id => PanelJson.Text(id)));
    }
    internal JsonObject Export()
    {
        int[] Read(string text, char separator) => text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part => int.TryParse(part, out int n) ? n : throw new InvalidOperationException("Les taux et les vedettes doivent contenir des nombres entiers.")).ToArray();
        var result = source.DeepClone().AsObject(); result["id"] = Id; result["name"] = Name; result["permanent"] = Permanent;
        result["shinyOneIn"] = ShinyOneIn; result["pity"] = Pity;
        var rates = Read(Rates, '/');
        if (rates.Length != 4 || rates.Any(rate => rate < 0) || rates.Sum() != 10000) throw new InvalidOperationException("Indiquez quatre taux séparés par /, dont la somme vaut 10 000.");
        result["rates"] = JsonSerializer.SerializeToNode(rates); result["featured"] = JsonSerializer.SerializeToNode(Read(Featured, ','));
        if (Permanent) { result.Remove("start"); result.Remove("end"); }
        else { result["start"] = Start == displayedStart ? source["start"]?.DeepClone() : EconomyEditor.ExportDate(Start); result["end"] = End == displayedEnd ? source["end"]?.DeepClone() : EconomyEditor.ExportDate(End); }
        return result;
    }
}
