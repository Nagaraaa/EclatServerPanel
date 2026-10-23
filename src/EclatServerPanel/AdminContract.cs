using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EclatServerPanel;

internal sealed record AdminChoice(string Id, string Label, string Name = "");
internal sealed record PlannedAdminCommand(string Command, JsonElement Arguments, string Title, string Message, string? Confirmation, bool NeedsConfirmation = true);
internal static class AdminContract
{
    internal static readonly string[] Commands = ["player.kick", "account.ban", "account.unban", "room.close", "room.update", "maintenance.set", "eclats.adjust", "casino.refund"];
    private static bool Uuid(JsonElement value) => value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString()!, "\\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[1-8][a-fA-F0-9]{3}-[89abAB][a-fA-F0-9]{3}-[a-fA-F0-9]{12}\\z");
    private static bool Hash(JsonElement value) => Regex.IsMatch(PanelJson.String(value), "\\A[a-f0-9]{64}\\z");
    private static bool Integer(JsonElement value, int min, int max) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n >= min && n <= max;
    private static bool Reason(JsonElement value) { string reason = PanelJson.String(value); return reason.Length is >= 3 and <= 240 && reason == reason.Trim() && !reason.Any(char.IsControl); }
    private static void Require(bool valid, string message = "Les arguments de la commande sont invalides.") { if (!valid) throw new InvalidOperationException(message); }
    private static void Fields(JsonElement value, params string[] allowed) => Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(property => allowed.Contains(property.Name)) && value.EnumerateObject().Select(property => property.Name).Distinct().Count() == value.EnumerateObject().Count());
    internal static void ValidateCommand(string command, JsonElement args)
    {
        Require(Commands.Contains(command), "Commande d’administration inconnue.");
        switch (command)
        {
            case "player.kick": Fields(args, "playerId"); Require(Uuid(PanelJson.Get(args, "playerId")), "Choisissez un joueur connecté."); break;
            case "account.ban":
                Fields(args, "accountHash", "reason", "expiresAt"); Require(Hash(PanelJson.Get(args, "accountHash")), "Choisissez un compte connu."); Require(Reason(PanelJson.Get(args, "reason")), "Indiquez un motif de 3 à 240 caractères.");
                var expiry = PanelJson.Get(args, "expiresAt"); Require(expiry.ValueKind == JsonValueKind.Null || Regex.IsMatch(PanelJson.String(expiry), "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,3})?Z\\z") && PanelJson.Date(expiry) is { } at && at > DateTimeOffset.UtcNow && at <= DateTimeOffset.UtcNow.AddYears(10), "La fin du bannissement doit être une date UTC future, dans les dix prochaines années."); break;
            case "account.unban": Fields(args, "accountHash", "reason"); Require(Hash(PanelJson.Get(args, "accountHash"))); if (args.TryGetProperty("reason", out var reason)) Require(Reason(reason)); break;
            case "room.close": Fields(args, "roomId"); Require(Uuid(PanelJson.Get(args, "roomId")), "Choisissez un salon existant."); break;
            case "room.update":
                Fields(args, "roomId", "capacity", "visibility"); Require(Uuid(PanelJson.Get(args, "roomId")), "Choisissez un salon existant.");
                Require(args.TryGetProperty("capacity", out var capacity) || args.TryGetProperty("visibility", out _));
                if (args.TryGetProperty("capacity", out capacity)) Require(Integer(capacity, 2, 10), "La capacité doit être comprise entre 2 et 10.");
                if (args.TryGetProperty("visibility", out var visibility)) Require(PanelJson.String(visibility) is "public" or "private"); break;
            case "maintenance.set": Fields(args, "enabled"); Require(PanelJson.Get(args, "enabled").ValueKind is JsonValueKind.True or JsonValueKind.False); break;
            case "eclats.adjust": case "casino.refund":
                Fields(args, "accountHash", "amount", "reason"); Require(Hash(PanelJson.Get(args, "accountHash")), "Choisissez un compte connu.");
                Require(Integer(PanelJson.Get(args, "amount"), command == "casino.refund" ? 1 : -2147483647, command == "casino.refund" ? 9999 : int.MaxValue) && PanelJson.Get(args, "amount").GetInt32() != 0,
                    command == "casino.refund" ? "Indiquez de 1 à 9 999 jetons." : "Indiquez un montant entier non nul.");
                Require(Reason(PanelJson.Get(args, "reason")), "Indiquez un motif de 3 à 240 caractères."); break;
        }
    }
    internal static PlannedAdminCommand Plan(string command, JsonElement arguments, string target)
    {
        ValidateCommand(command, arguments);
        return command switch
        {
            "player.kick" => new(command, arguments.Clone(), "Expulser ce joueur ?", $"La connexion de {target} sera fermée.", null),
            "account.ban" => new(command, arguments.Clone(), "Bannir ce compte ?", $"Compte de {target}. Une nouvelle clé permet de contourner un bannissement par compte.", target),
            "account.unban" => new(command, arguments.Clone(), "Lever ce bannissement ?", $"Le compte de {target} pourra à nouveau se connecter.", null),
            "room.close" => new(command, arguments.Clone(), "Fermer ce salon ?", $"Les membres quitteront {target} et le salon sera supprimé.", target),
            "room.update" => new(command, arguments.Clone(), "Modifier ce salon", target, null, false),
            "maintenance.set" => new(command, arguments.Clone(), "Changer le mode maintenance ?", PanelJson.Get(arguments, "enabled").ValueKind == JsonValueKind.True ? "Les nouvelles connexions seront refusées. Les joueurs déjà connectés pourront finir leur partie." : "Les nouvelles connexions seront à nouveau acceptées.", null),
            _ => new(command, arguments.Clone(), command == "casino.refund" ? "Rendre ces jetons ?" : "Modifier le solde Éclats ?", $"Compte de {target}. Le motif et le résultat seront inscrits dans le journal." + (command == "casino.refund" ? " Les jetons sont remis par le parcours de réclamation du jeu." : ""), PanelJson.Get(arguments, "amount").GetInt32().ToString(CultureInfo.InvariantCulture))
        };
    }
    internal static JsonElement CommandFields(PlannedAdminCommand plan, string? requestId = null) => JsonSerializer.SerializeToElement(new { requestId = requestId ?? Guid.NewGuid().ToString(), command = plan.Command, arguments = plan.Arguments });
    internal static string Request(string action, JsonElement fields)
    {
        switch (action)
        {
            case "admin.overview": case "admin.audit": case "config.get": case "config.history": case "statistics": Fields(fields); break;
            case "admin.command": Fields(fields, "requestId", "command", "arguments"); Require(Uuid(PanelJson.Get(fields, "requestId"))); ValidateCommand(PanelJson.String(PanelJson.Get(fields, "command")), PanelJson.Get(fields, "arguments")); break;
            case "config.save":
                Fields(fields, "requestId", "config", "expectedSha256", "reason"); Require(Uuid(PanelJson.Get(fields, "requestId")) && Hash(PanelJson.Get(fields, "expectedSha256")) && Reason(PanelJson.Get(fields, "reason")));
                Require(PanelJson.Get(fields, "config").ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(PanelJson.Get(fields, "config").GetRawText()) <= 65536, "La configuration doit être un objet JSON de 64 Kio au maximum."); break;
            case "config.restore": Fields(fields, "requestId", "version", "expectedSha256", "reason"); Require(Uuid(PanelJson.Get(fields, "requestId")) && Hash(PanelJson.Get(fields, "expectedSha256")) && Reason(PanelJson.Get(fields, "reason")) && Regex.IsMatch(PanelJson.String(PanelJson.Get(fields, "version")), "\\Aeclats-[0-9]{8}T[0-9]{12}Z-[a-f0-9]{8}\\.json\\z")); break;
            default: throw new InvalidOperationException("Action d’administration inconnue.");
        }
        var root = new JsonObject { ["action"] = action };
        foreach (var property in fields.EnumerateObject()) root[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        string result = root.ToJsonString(); Require(Encoding.UTF8.GetByteCount(result) <= 96 * 1024, "La requête est trop volumineuse."); return result;
    }
    internal static JsonElement ParseResponse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 48 });
            var root = document.RootElement;
            UniqueProperties(root);
            Require(root.ValueKind == JsonValueKind.Object && PanelJson.Get(root, "ok").ValueKind is JsonValueKind.True or JsonValueKind.False, "Réponse d’administration invalide.");
            if (PanelJson.Get(root, "ok").ValueKind != JsonValueKind.True) throw new InvalidOperationException(PanelResponse.SafeError(PanelJson.Text(PanelJson.Get(root, "error"), "La commande n’a pas été confirmée.")));
            var data = PanelJson.Get(root, "data"); Require(data.ValueKind == JsonValueKind.Object, "Réponse d’administration incomplète."); return data.Clone();
        }
        catch (JsonException) { throw new InvalidOperationException("Réponse d’administration invalide."); }
    }
    internal static string MaskAccount(string value) => Regex.Replace(value, "(?<![a-fA-F0-9])[a-fA-F0-9]{64}(?![a-fA-F0-9])", match => match.Value[..8]);
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { Require(names.Add(property.Name), "Réponse d’administration ambiguë."); UniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) UniqueProperties(child);
    }
}
