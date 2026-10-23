using System.Globalization;
using System.Text.Json;

namespace EclatServerPanel;

internal static class PanelData
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);

    internal static bool IsRecent(JsonElement value, DateTimeOffset now)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("updatedAt", out var time)
            || time.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return false;
        var age = now - at;
        return age >= TimeSpan.FromSeconds(-10) && age <= MaximumAge;
    }

    internal static bool HasActivity(JsonElement monitor, bool active, bool responds, DateTimeOffset now)
        => active && responds && IsRecent(monitor, now)
            && monitor.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "running"
            && (!monitor.TryGetProperty("processCurrent", out var current) || current.ValueKind == JsonValueKind.True)
            && monitor.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array
            && monitor.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array;
}
