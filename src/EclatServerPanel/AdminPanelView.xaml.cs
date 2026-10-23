using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace EclatServerPanel;

public partial class AdminPanelView : UserControl
{
    private readonly EconomyEditor editor = new();
    private JsonElement overview;
    private string configHash = "";
    private ConnectionSettings? origin, configOrigin;
    private bool configLoaded, loading;
    private bool refreshingRooms;
    private string? roomDraftId;
    private string roomBaseCapacity = "", roomBaseVisibility = "";
    private bool RoomDraftChanged => RoomCapacity.Text != roomBaseCapacity || (RoomVisibility.SelectedValue as string ?? "") != roomBaseVisibility;
    internal Func<string, Task>? ReadRequested;
    internal Func<PlannedAdminCommand, ConnectionSettings?, Task>? ExecuteRequested;
    internal Func<string, JsonElement, ConnectionSettings?, Task>? ConfigRequested;
    internal Action<string>? Failed;
    public AdminPanelView()
    {
        InitializeComponent();
        CommandChoice.ItemsSource = new[] { new AdminChoice("player.kick", "Expulser un joueur"), new("account.ban", "Bannir un compte"), new("account.unban", "Lever un bannissement"), new("room.close", "Fermer un salon"), new("room.update", "Modifier un salon"), new("maintenance.set", "Mode maintenance"), new("eclats.adjust", "Créditer ou débiter des Éclats"), new("casino.refund", "Rendre des jetons de casino") };
        RoomVisibility.ItemsSource = new[] { new AdminChoice("public", "Public"), new("private", "Privé") }; RoomVisibility.SelectedValue = "public";
        EventsGrid.ItemsSource = editor.Events; BannersGrid.ItemsSource = editor.Banners;
        CommandChoice.SelectedIndex = 0; CommandsTab.IsChecked = true;
        Clear();
    }
    internal void ApplyOverview(JsonElement data, ConnectionSettings connection)
    {
        overview = data.Clone(); origin = connection.Snapshot();
        var players = PanelJson.Array(PanelJson.Get(data, "connectedPlayers")).ToList();
        var accounts = PanelJson.Array(PanelJson.Get(data, "accounts")).Concat(players)
            .Where(item => Regex.IsMatch(PanelJson.String(PanelJson.Get(item, "accountHash")), "\\A[a-f0-9]{64}\\z"))
            .GroupBy(item => PanelJson.String(PanelJson.Get(item, "accountHash"))).Select(group => group.First()).ToList();
        Choices(PlayerChoice, players.Select(item => new AdminChoice(PanelJson.String(PanelJson.Get(item, "id")), PanelJson.String(PanelJson.Get(item, "name")), PanelJson.String(PanelJson.Get(item, "name")))));
        Choices(AccountChoice, accounts.Select(Account));
        Choices(BanChoice, PanelJson.Array(PanelJson.Get(data, "bans")).Select(item =>
        {
            string hash = PanelJson.String(PanelJson.Get(item, "accountHash"));
            string name = accounts.Where(account => PanelJson.String(PanelJson.Get(account, "accountHash")) == hash).Select(account => PanelJson.String(PanelJson.Get(account, "name"))).FirstOrDefault() ?? "Compte";
            return new AdminChoice(hash, $"{name} · {Short(hash)} · {PanelJson.String(PanelJson.Get(item, "reason"))}", name);
        }));
        bool preserveRoomDraft = RoomDraftChanged;
        refreshingRooms = true;
        Choices(RoomChoice, PanelJson.Array(PanelJson.Get(data, "rooms")).Select(item => new AdminChoice(PanelJson.String(PanelJson.Get(item, "id")), $"{PanelJson.String(PanelJson.Get(item, "name"))} · {PanelJson.Text(PanelJson.Get(item, "count"))}/{PanelJson.Text(PanelJson.Get(item, "capacity"))}", PanelJson.String(PanelJson.Get(item, "name")))));
        refreshingRooms = false;
        if (!preserveRoomDraft || roomDraftId != RoomChoice.SelectedValue as string) LoadRoomOptions();
        bool maintenance = PanelJson.Get(data, "maintenance").ValueKind == JsonValueKind.True;
        MaintenanceEnabled.IsChecked = maintenance;
        MaintenanceState.Text = $"{(maintenance ? "Maintenance active" : "Connexions ouvertes")} · {players.Count} joueur(s) encore connecté(s)";
        AdminState.Text = "Commandes disponibles. Les modifications sont inscrites dans le journal d’administration.";
    }
    private static string Short(string value) => value.Length > 8 ? value[..8] : value;
    private static AdminChoice Account(JsonElement item)
    {
        string hash = PanelJson.String(PanelJson.Get(item, "accountHash"));
        string name = PanelJson.String(PanelJson.Get(item, "name")); if (name.Length == 0) name = "Compte " + Short(hash);
        return new(hash, $"{name} · {Short(hash)}" + (PanelJson.Get(item, "balance").ValueKind == JsonValueKind.Number ? $" · {PanelJson.Text(PanelJson.Get(item, "balance"))} Éclats" : ""), name);
    }
    private static void Choices(ComboBox box, IEnumerable<AdminChoice> values)
    {
        string? selected = box.SelectedValue as string;
        box.ItemsSource = values.ToList(); box.SelectedValue = selected;
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
    }
    private void Command_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PlayerTarget is null) return;
        string command = CommandChoice.SelectedValue as string ?? "";
        void Show(FrameworkElement view, bool visible) => view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Show(PlayerTarget, command == "player.kick"); Show(AccountTarget, command is "account.ban" or "eclats.adjust" or "casino.refund");
        Show(BanTarget, command == "account.unban"); Show(RoomTarget, command is "room.close" or "room.update");
        Show(DurationTarget, command == "account.ban"); Show(RoomOptions, command == "room.update"); Show(MaintenanceEnabled, command == "maintenance.set");
        Show(MoneyTarget, command is "eclats.adjust" or "casino.refund"); Show(ReasonTarget, command is "account.ban" or "eclats.adjust" or "casino.refund");
        AmountHint.Text = command == "casino.refund" ? "De 1 à 9 999 jetons, remis par le parcours de réclamation du jeu." : "Montant positif pour créditer, négatif pour débiter. Le serveur refusera un solde négatif.";
        if (command == "room.update") LoadRoomOptions();
    }
    private void Room_Changed(object sender, SelectionChangedEventArgs e) { if (!refreshingRooms && RoomCapacity is not null) LoadRoomOptions(); }
    private void LoadRoomOptions()
    {
        roomDraftId = RoomChoice.SelectedValue as string;
        var room = PanelJson.Array(PanelJson.Get(overview, "rooms")).FirstOrDefault(item => PanelJson.String(PanelJson.Get(item, "id")) == roomDraftId);
        roomBaseCapacity = PanelJson.Text(PanelJson.Get(room, "capacity"), "");
        roomBaseVisibility = PanelJson.String(PanelJson.Get(room, "visibility"));
        RoomCapacity.Text = roomBaseCapacity; RoomVisibility.SelectedValue = roomBaseVisibility;
    }
    private async void Execute_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        string command = CommandChoice.SelectedValue as string ?? "";
        int Number(TextBox input) => int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : throw new InvalidOperationException("Indiquez un nombre entier.");
        AdminChoice Choice(ComboBox combo) => combo.SelectedItem as AdminChoice ?? throw new InvalidOperationException("Choisissez une cible dans la liste actualisée.");
        string target; object arguments;
        switch (command)
        {
            case "player.kick": var player = Choice(PlayerChoice); target = player.Name; arguments = new { playerId = player.Id }; break;
            case "account.ban":
                var account = Choice(AccountChoice); target = account.Name; int hours = PermanentBan.IsChecked == true ? 0 : Number(BanHours);
                if (PermanentBan.IsChecked != true && hours is < 1 or > 87600) throw new InvalidOperationException("Indiquez de 1 à 87 600 heures ou choisissez une durée illimitée.");
                arguments = new { accountHash = account.Id, reason = ReasonBox.Text.Trim(), expiresAt = PermanentBan.IsChecked == true ? null : DateTimeOffset.UtcNow.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) }; break;
            case "account.unban": var ban = Choice(BanChoice); target = ban.Name; arguments = new { accountHash = ban.Id }; break;
            case "room.close": var room = Choice(RoomChoice); target = room.Name; arguments = new { roomId = room.Id }; break;
            case "room.update":
                var updated = Choice(RoomChoice); target = updated.Name;
                if (roomDraftId != updated.Id) throw new InvalidOperationException("Relisez les paramètres de ce salon.");
                var roomChanges = new Dictionary<string, object> { ["roomId"] = updated.Id };
                if (RoomCapacity.Text != roomBaseCapacity) roomChanges["capacity"] = Number(RoomCapacity);
                if (RoomVisibility.SelectedValue is string visibility && visibility != roomBaseVisibility) roomChanges["visibility"] = visibility;
                if (roomChanges.Count == 1) throw new InvalidOperationException("Aucun paramètre du salon n’a changé.");
                arguments = roomChanges; break;
            case "maintenance.set": target = "Maintenance"; arguments = new { enabled = MaintenanceEnabled.IsChecked == true }; break;
            default: var money = Choice(AccountChoice); target = money.Name; arguments = new { accountHash = money.Id, amount = Number(AmountBox), reason = ReasonBox.Text.Trim() }; break;
        }
        var plan = AdminContract.Plan(command, JsonSerializer.SerializeToElement(arguments), target);
        if (ExecuteRequested is null) throw new InvalidOperationException("Le contrôleur d’administration n’est pas disponible.");
        await ExecuteRequested(plan, origin);
    });
    private async void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (CommandsBody is null) return;
        string tab = (string)((RadioButton)sender).Tag;
        CommandsBody.Visibility = tab == "commands" ? Visibility.Visible : Visibility.Collapsed;
        ConfigBody.Visibility = tab == "config" ? Visibility.Visible : Visibility.Collapsed;
        StatsBody.Visibility = tab == "stats" ? Visibility.Visible : Visibility.Collapsed;
        if (ReadRequested is not null && !loading && (tab == "stats" || tab == "config" && !configLoaded)) await Guard(() => ReadRequested(tab == "stats" ? "statistics" : "config"));
    }
    internal void ApplyConfiguration(JsonElement data, ConnectionSettings connection)
    {
        var config = PanelJson.Get(data, "config");
        bool invalid = PanelJson.Get(data, "fileInvalid").ValueKind == JsonValueKind.True;
        if (invalid) config = PanelJson.Get(data, "recoveryConfig");
        editor.Load(config); configHash = PanelJson.String(PanelJson.Get(data, "sha256")); configOrigin = connection.Snapshot(); configLoaded = true;
        SinglePrice.Text = editor.Price("single").ToString(CultureInfo.InvariantCulture); TenPrice.Text = editor.Price("ten").ToString(CultureInfo.InvariantCulture);
        ConfigJson.Text = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        ConfigurationResult(data);
        if (invalid)
        {
            ConfigState.Text = "Fichier invalide : les champs proposent la configuration par défaut. Corrigez-le ou restaurez une version de l’historique.";
            ConfigState.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
            ConfigJson.Text = PanelJson.String(PanelJson.Get(data, "raw"), ConfigJson.Text);
        }
    }
    internal void ConfigurationResult(JsonElement data)
    {
        string state = PanelJson.String(PanelJson.Get(PanelJson.Get(data, "status"), "state"), "unknown");
        ConfigState.Text = state switch { "accepted" => "Configuration acceptée par le jeu", "rejected" => "Configuration refusée — le jeu conserve sa dernière version valide", "pending" => "Écriture effectuée, chargement pas encore confirmé", _ => "Chargement de la configuration non confirmé" };
        ConfigState.Foreground = (System.Windows.Media.Brush)FindResource(state == "accepted" ? "Success" : "Warning");
    }
    internal void ApplyHistory(JsonElement data)
        => Choices(ConfigVersions, PanelJson.Array(PanelJson.Get(data, "items")).Select(item => new AdminChoice(PanelJson.String(PanelJson.Get(item, "id")), $"{PanelJson.Date(PanelJson.Get(item, "modifiedUtc"))?.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {PanelJson.String(PanelJson.Get(item, "reason"))}")));
    internal void ApplyStatistics(JsonElement data)
    {
        StatsScope.Text = PanelJson.String(PanelJson.Get(data, "activityScope")) is "connections" or "authenticatedConnections" ? "Activité des comptes connectés par jour UTC, sur les 30 derniers jours. Lecture seule de la base." : "Activité économique par jour UTC, sur les 30 derniers jours. L’ancien serveur ne dispose pas de l’historique des connexions.";
        if (PanelJson.Date(PanelJson.Get(data, "activityAvailableSince")) is { } since) StatsScope.Text += $" Historique disponible depuis le {since.UtcDateTime:dd/MM/yyyy HH:mm} UTC.";
        var sections = new List<StatisticsSection>();
        var totals = PanelJson.Get(data, "totals");
        sections.Add(new("Ensemble du serveur", ["Comptes : " + PanelJson.Text(PanelJson.Get(totals, "accounts")), "Éclats en circulation : " + PanelJson.Text(PanelJson.Get(totals, "eclatsInCirculation")), "Tirages : " + PanelJson.Text(PanelJson.Get(totals, "pulls")), "Œufs chromatiques obtenus : " + PanelJson.Text(PanelJson.Get(totals, "shiny"))]));
        sections.Add(new("Tirages par bannière", PanelJson.Array(PanelJson.Get(data, "drawsByBanner")).Select(item => $"Bannière {PanelJson.Text(PanelJson.Get(item, "banner"))} · {PanelJson.Text(PanelJson.Get(item, "pulls"))} tirage(s), {PanelJson.Text(PanelJson.Get(item, "eggs"))} œuf(s), {PanelJson.Text(PanelJson.Get(item, "shiny"))} chromatique(s)").ToList()));
        sections.Add(new("Casino — mises et gains", PanelJson.Array(PanelJson.Get(data, "casinoByGame")).Select(item => $"{Game(PanelJson.String(PanelJson.Get(item, "game")))} · {Currency(PanelJson.String(PanelJson.Get(item, "currency")))} · {PanelJson.Text(PanelJson.Get(item, "rounds"))} partie(s), mises {PanelJson.Text(PanelJson.Get(item, "wagers"))}, gains {PanelJson.Text(PanelJson.Get(item, "payouts"))}").ToList()));
        sections.Add(new("Joueurs actifs par jour", PanelJson.Array(PanelJson.Get(data, "activePlayersByDay")).Select(item => $"{PanelJson.Text(PanelJson.Get(item, "day"))} · {PanelJson.Text(PanelJson.Get(item, "players"))} joueur(s)").ToList()));
        StatsTables.ItemsSource = sections;
    }
    private static string Game(string value) => value switch { "slots" => "Machines", "plinko" => "Plinko", "blackjack" => "Blackjack", _ => value };
    private static string Currency(string value) => value switch { "coins" => "jetons", "eclats" => "Éclats", _ => value };
    private JsonElement EditedConfig()
    {
        EventsGrid.CommitEdit(); EventsGrid.CommitEdit(DataGridEditingUnit.Row, true); BannersGrid.CommitEdit(); BannersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!int.TryParse(SinglePrice.Text, out int single) || !int.TryParse(TenPrice.Text, out int ten)) throw new InvalidOperationException("Les prix doivent être des nombres entiers.");
        return editor.Export(single, ten);
    }
    private async void SaveConfig_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var fields = JsonSerializer.SerializeToElement(new { requestId = Guid.NewGuid().ToString(), config = EditedConfig(), expectedSha256 = configHash, reason = ConfigReason.Text.Trim() });
        AdminContract.Request("config.save", fields);
        if (ConfigRequested is not null) await ConfigRequested("config.save", fields, configOrigin);
    });
    private async void RestoreConfig_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (ConfigVersions.SelectedValue is not string version) throw new InvalidOperationException("Choisissez une version dans l’historique.");
        var fields = JsonSerializer.SerializeToElement(new { requestId = Guid.NewGuid().ToString(), version, expectedSha256 = configHash, reason = ConfigReason.Text.Trim() });
        AdminContract.Request("config.restore", fields);
        if (ConfigRequested is not null) await ConfigRequested("config.restore", fields, configOrigin);
    });
    private async void ConfigReload_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (ReadRequested is not null) await ReadRequested("config"); });
    private async void StatsReload_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (ReadRequested is not null) await ReadRequested("statistics"); });
    private async void AddEvent_Click(object sender, RoutedEventArgs e) => await Guard(() => { editor.AddEvent(); return Task.CompletedTask; });
    private void RemoveEvent_Click(object sender, RoutedEventArgs e) { if (EventsGrid.SelectedItem is EventEditorRow row) editor.Events.Remove(row); }
    private async void AddBanner_Click(object sender, RoutedEventArgs e) => await Guard(() => { editor.AddBanner(); return Task.CompletedTask; });
    private async void RemoveBanner_Click(object sender, RoutedEventArgs e) => await Guard(() => { if (BannersGrid.SelectedItem is BannerEditorRow row) { if (row.Permanent) throw new InvalidOperationException("La bannière permanente doit être conservée."); editor.Banners.Remove(row); } return Task.CompletedTask; });
    private async void ExportJson_Click(object sender, RoutedEventArgs e) => await Guard(() => { ConfigJson.Text = JsonSerializer.Serialize(EditedConfig(), new JsonSerializerOptions { WriteIndented = true }); return Task.CompletedTask; });
    private async void ImportJson_Click(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        using var document = JsonDocument.Parse(ConfigJson.Text); editor.Load(document.RootElement);
        SinglePrice.Text = editor.Price("single").ToString(CultureInfo.InvariantCulture); TenPrice.Text = editor.Price("ten").ToString(CultureInfo.InvariantCulture); return Task.CompletedTask;
    });
    private async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetError(ex.Message); Failed?.Invoke(ex.Message); }
    }
    internal void SetError(string value) { AdminState.Text = value; AdminState.Foreground = (System.Windows.Media.Brush)FindResource("Warning"); }
    internal void InvalidateRead(string action)
    {
        if (action == "config")
        {
            configLoaded = false; configOrigin = null; configHash = "";
            ConfigState.Text = "Relecture en échec : les champs conservés ne confirment pas la configuration actuelle.";
            ConfigState.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
        }
        else if (action == "statistics") { StatsTables.ItemsSource = Array.Empty<StatisticsSection>(); StatsScope.Text = "Les statistiques n’ont pas pu être actualisées."; }
        else
        {
            origin = null;
            foreach (var combo in new[] { PlayerChoice, AccountChoice, BanChoice, RoomChoice }) combo.ItemsSource = Array.Empty<AdminChoice>();
            MaintenanceState.Text = "État de maintenance et cibles non confirmés";
        }
    }
    internal void Clear()
    {
        loading = true; origin = configOrigin = null; overview = default; configLoaded = false; configHash = "";
        foreach (var combo in new[] { PlayerChoice, AccountChoice, BanChoice, RoomChoice, ConfigVersions }) combo.ItemsSource = Array.Empty<AdminChoice>();
        editor.Events.Clear(); editor.Banners.Clear(); ConfigJson.Text = SinglePrice.Text = TenPrice.Text = ConfigReason.Text = "";
        StatsTables.ItemsSource = Array.Empty<StatisticsSection>(); StatsScope.Text = ""; MaintenanceState.Text = "Contrôle du serveur nécessaire";
        AdminState.Text = "Chargez les commandes d’un serveur compatible."; ConfigState.Text = "Configuration à charger"; loading = false;
    }
    internal void CaptureTab(string tab) => (tab == "config" ? ConfigTab : tab == "stats" ? StatsTab : CommandsTab).IsChecked = true;
    private sealed record StatisticsSection(string Title, IReadOnlyList<string> Rows);
}
