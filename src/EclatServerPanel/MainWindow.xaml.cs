using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EclatServerPanel;

public partial class MainWindow : Window
{
    private readonly IPanelTransport transport;
    private readonly bool demo;
    private ConnectionSettings settings;
    private bool busy;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private string currentPage = "Overview";
    private readonly Dictionary<string, FrameworkElement> pages;
    private readonly Dictionary<string, Button> navigation;

    public MainWindow(bool demo)
    {
        InitializeComponent();
        this.demo = demo;
        transport = demo ? new DemoTransport() : new SshTransport();
        settings = demo ? new ConnectionSettings { Host = "serveur.example", User = "eclat-panel", Port = 22, KeyPath = "Clé locale — démonstration" } : ConnectionSettings.Load();
        pages = new() { ["Overview"] = OverviewPage, ["Sessions"] = SessionsPage, ["Logs"] = LogsPage, ["Backups"] = BackupsPage, ["Diagnostics"] = DiagnosticsPage, ["Connection"] = ConnectionPage };
        navigation = new() { ["Overview"] = NavOverview, ["Sessions"] = NavSessions, ["Logs"] = NavLogs, ["Backups"] = NavBackups, ["Diagnostics"] = NavDiagnostics, ["Connection"] = NavConnection };
        HostBox.Text = settings.Host;
        UserBox.Text = settings.User;
        PortBox.Text = settings.Port.ToString();
        KeyBox.Text = settings.KeyPath;
        EndpointText.Text = string.IsNullOrEmpty(settings.Host) ? "Aucun serveur configuré" : $"{settings.User}@{settings.Host}";
        if (demo) { ModeText.Text = "DÉMONSTRATION · données fictives"; ShowMessage("Mode démonstration : les informations sont fictives, aucune connexion au VPS."); }
        ShowPage(string.IsNullOrEmpty(settings.Host) ? "Connection" : "Overview");
        Loaded += async (_, _) => { if (!string.IsNullOrEmpty(settings.Host)) await RefreshAsync(); timer.Start(); };
        timer.Tick += async (_, _) => { if (!busy && currentPage is "Overview" or "Sessions" && !string.IsNullOrEmpty(settings.Host)) await RefreshAsync(); };
        Closed += (_, _) => timer.Stop();
    }

    private void ShowPage(string name)
    {
        currentPage = name;
        foreach (var page in pages) page.Value.Visibility = page.Key == name ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in navigation) item.Value.Background = item.Key == name ? new SolidColorBrush(Color.FromRgb(47, 72, 57)) : Brushes.Transparent;
        PageTitle.Text = navigation[name].Content.ToString();
    }
    public async Task WaitForIdleAsync()
    {
        while (busy) await Task.Delay(50);
    }
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        var page = ((Button)sender).Tag.ToString()!;
        ShowPage(page);
        if (page == "Logs") await LoadLogsAsync();
        else if (page == "Backups") await LoadBackupsAsync();
        else if (page == "Diagnostics") await LoadDiagnosticsAsync();
    }
    private void ShowMessage(string message, bool error = false)
    {
        MessageText.Text = message;
        MessageBar.Visibility = Visibility.Visible;
        MessageBar.Background = (Brush)new BrushConverter().ConvertFromString(error ? "#F9E4DE" : "#F2EACF")!;
        MessageText.Foreground = (Brush)new BrushConverter().ConvertFromString(error ? "#873C2B" : "#665025")!;
    }
    private async Task RunAsync(string activity, Func<Task> work, bool clearMessage = true)
    {
        if (busy) return;
        busy = true;
        ActivityText.Text = activity;
        Cursor = System.Windows.Input.Cursors.Wait;
        if (clearMessage && !demo) MessageBar.Visibility = Visibility.Collapsed;
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowMessage(ex.Message, true);
            ActivityText.Text = "Action interrompue";
        }
        finally { busy = false; Cursor = null; if (ActivityText.Text == activity) ActivityText.Text = "Prêt"; }
    }
    public async Task RefreshAsync() => await RunAsync("Vérification du service et du protocole…", async () =>
    {
        try
        {
            var data = await transport.SendAsync(settings, "status");
            RenderStatus(data);
        }
        catch
        {
            ServerStatus.Text = "État non confirmé";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(174, 121, 51));
            StatusDetail.Text = "Le dernier contrôle a échoué. Les valeurs précédentes peuvent être périmées.";
            UpdatedText.Text = "Dernier contrôle en échec";
            PlayersValue.Text = "—";
            RoomsValue.Text = "Salons : —";
            LatencyValue.Text = "—";
            PlayerItems.ItemsSource = new[] { new ActivityItem("Activité non confirmée", "Le dernier contrôle du serveur a échoué.") };
            RoomItems.ItemsSource = new[] { new ActivityItem("Salons non confirmés", "Le dernier contrôle du serveur a échoué.") };
            throw;
        }
    });

    private void RenderStatus(JsonElement data)
    {
        var service = Get(data, "service");
        var protocol = Get(data, "protocol");
        var metrics = Get(data, "metrics");
        var monitor = Get(data, "monitor");
        var network = Get(data, "network");
        bool active = Text(Get(service, "active")) is "active" or "True" or "true";
        bool responds = Get(protocol, "ok").ValueKind == JsonValueKind.True;
        ServerStatus.Text = responds ? "Accessible" : active ? "Service actif, jeu indisponible" : "Service arrêté";
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(responds ? "#3F7A53" : "#B57D39")!;
        StatusDetail.Text = responds ? "Le service fonctionne et le protocole du jeu répond depuis le VPS." : active ? "Le processus tourne, mais le protocole du jeu ne répond pas. Consultez les journaux avant un redémarrage." : "Le service est arrêté ou en échec. Le diagnostic permet de préciser son état.";
        string publication = Text(Get(network, "publication")) switch { "available" => "disponible", "unavailable" => "indisponible", var v => v };
        string tunnel = Text(Get(network, "tunnel")) switch { "connected" => "connecté", "disconnected" => "déconnecté", var v => v };
        NetworkText.Text = $"Annuaire Coop : {publication}   ·   Tunnel : {tunnel}";
        if (DateTimeOffset.TryParse(Text(Get(network, "updatedAt")), out var updatedAt) && DateTimeOffset.UtcNow - updatedAt > TimeSpan.FromMinutes(1)) NetworkText.Text += "   ·   état ancien";
        var players = Get(monitor, "players");
        var rooms = Get(monitor, "rooms");
        bool activityFresh = active && responds;
        if (DateTimeOffset.TryParse(Text(Get(monitor, "updatedAt")), out var monitorUpdated) && DateTimeOffset.UtcNow - monitorUpdated > TimeSpan.FromMinutes(1)) activityFresh = false;
        PlayersValue.Text = activityFresh ? Count(players) : "—";
        RoomsValue.Text = "Salons : " + (activityFresh ? Count(rooms) : "—");
        LatencyValue.Text = responds ? Text(Get(protocol, "latencyMs")) + " ms" : "—";
        long total = Number(Get(metrics, "memoryTotalBytes")), available = Number(Get(metrics, "memoryAvailableBytes"));
        MemoryValue.Text = total > 0 ? $"{(total - available) / 1073741824d:0.0} Go" : "—";
        MemoryDetail.Text = total > 0 ? $"sur {total / 1073741824d:0.0} Go" : "Mesure indisponible";
        SessionsText.Text = monitor.ValueKind == JsonValueKind.Undefined ? "Aucune donnée d’activité reçue." : Pretty(monitor);
        PlayerItems.ItemsSource = activityFresh ? ActivityItems(players, false) : new[] { new ActivityItem("Activité non confirmée", "Le jeu ne répond pas ou les données d’activité sont anciennes.") };
        RoomItems.ItemsSource = activityFresh ? ActivityItems(rooms, true) : new[] { new ActivityItem("Salons non confirmés", "Les derniers détails restent consultables ci-dessous.") };
        UpdatedText.Text = "Contrôlé à " + DateTime.Now.ToString("HH:mm:ss");
    }
    private static JsonElement Get(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "—" : value.ToString();
    private static long Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private static string Count(JsonElement value) => value.ValueKind switch { JsonValueKind.Number => value.ToString(), JsonValueKind.Array => value.GetArrayLength().ToString(), JsonValueKind.Object => Get(value, "count").ValueKind == JsonValueKind.Number ? Get(value, "count").ToString() : value.EnumerateObject().Count().ToString(), _ => "—" };
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static List<ActivityItem> ActivityItems(JsonElement collection, bool rooms)
    {
        var result = new List<ActivityItem>();
        if (collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in collection.EnumerateArray())
            {
                string title = "";
                foreach (var field in rooms ? new[] { "code", "roomCode", "id", "roomId", "name" } : new[] { "name", "playerName", "trainerName", "username", "nickname", "id" })
                {
                    var value = Get(item, field);
                    if (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())) { title = value.GetString()!; break; }
                }
                var details = new List<string>();
                if (rooms)
                {
                    var members = Get(item, "players");
                    if (members.ValueKind == JsonValueKind.Array) details.Add($"{members.GetArrayLength()} joueur(s)");
                    var state = Get(item, "status");
                    if (state.ValueKind == JsonValueKind.String) details.Add(state.ToString());
                }
                else
                {
                    foreach (var field in new[] { "roomCode", "roomId" })
                    {
                        var room = Get(item, field);
                        if (room.ValueKind == JsonValueKind.String) { details.Add("Salon " + room.GetString()); break; }
                    }
                }
                result.Add(new(rooms ? "Salon " + (string.IsNullOrEmpty(title) ? "sans identifiant" : title) : string.IsNullOrEmpty(title) ? "Joueur connecté" : title, details.Count > 0 ? string.Join(" · ", details) : "Voir les détails de l’activité pour les informations complètes."));
            }
        }
        if (result.Count == 0) result.Add(new(rooms ? "Aucun salon ouvert" : "Aucun joueur connecté", "Le prochain contrôle actualisera cette liste."));
        return result;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Service_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var action = ((Button)sender).Tag.ToString()!;
        if (action is "stop" or "restart" && MessageBox.Show(this, action == "stop" ? "Arrêter le serveur ? Les parties en cours seront interrompues." : "Redémarrer le serveur ? Les parties en cours seront interrompues.", "Confirmer l’action", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        bool done = false;
        await RunAsync("Commande du service en cours…", async () =>
        {
            await transport.SendAsync(settings, action);
            ShowMessage(action == "start" ? "Commande de démarrage exécutée." : action == "stop" ? "Commande d’arrêt exécutée." : "Commande de redémarrage exécutée.");
            done = true;
        });
        if (done) { await Task.Delay(1100); await RefreshAsync(); }
    }
    private async Task LoadLogsAsync() => await RunAsync("Lecture des journaux…", async () => { var data = await transport.SendAsync(settings, "logs"); LogsText.Text = Text(Get(data, "text")); LogsText.ScrollToEnd(); });
    private async void Logs_Click(object sender, RoutedEventArgs e) => await LoadLogsAsync();
    private async Task LoadDiagnosticsAsync() => await RunAsync("Diagnostic du serveur…", async () => { var data = await transport.SendAsync(settings, "diagnostics"); DiagnosticsText.Text = Pretty(data); });
    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await LoadDiagnosticsAsync();
    private async Task LoadBackupsAsync() => await RunAsync("Lecture des sauvegardes…", LoadBackupsCoreAsync);
    private async Task LoadBackupsCoreAsync()
    {
        var data = await transport.SendAsync(settings, "backups");
        var items = Get(data, "items");
        var backups = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Select(item => new BackupItem(Text(Get(item, "name")), Number(Get(item, "size")), Text(Get(item, "modifiedUtc")))).ToList() : [];
        BackupList.ItemsSource = backups;
        BackupList.SelectedIndex = backups.Count > 0 ? 0 : -1;
        BackupDetail.Text = backups.Count == 0 ? "Aucun instantané disponible sur le VPS." : $"{backups.Count} instantané(s) disponible(s) · la liste est mise à jour à la demande.";
    }
    private async void Backups_Click(object sender, RoutedEventArgs e) => await LoadBackupsAsync();
    private async void CreateBackup_Click(object sender, RoutedEventArgs e) => await RunAsync("Création d’un instantané cohérent…", async () =>
    {
        var data = await transport.SendAsync(settings, "backup");
        ShowMessage("Sauvegarde créée : " + Text(Get(data, "name")));
        await LoadBackupsCoreAsync();
    });
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (demo) { ShowMessage("Le téléchargement est disponible après connexion à un serveur réel."); return; }
        if (BackupList.SelectedItem is not BackupItem item) { ShowMessage("Choisissez un instantané."); return; }
        var save = new SaveFileDialog { Title = "Enregistrer une copie de la base", Filter = "Base SQLite (*.sqlite)|*.sqlite", FileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.Name)) + ".sqlite", AddExtension = true, DefaultExt = ".sqlite" };
        if (save.ShowDialog(this) != true) return;
        await RunAsync("Téléchargement et vérification SHA-256…", async () =>
        {
            var data = await transport.SendAsync(settings, "download", item.Name);
            var bytes = SshTransport.VerifyDownload(data);
            var temp = save.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, save.FileName, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            ShowMessage("Base enregistrée sur votre PC. L’empreinte SHA-256 correspond à celle du serveur.");
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (BackupList.SelectedItem is not BackupItem item) { ShowMessage("Choisissez un instantané."); return; }
        if (!ConfirmDialog.Show(this, "Restaurer une sauvegarde", $"Instantané : {item.Name}\n\nLes données actuelles seront remplacées et les parties interrompues. Une copie de retour arrière sera créée sur le serveur.\n\nSaisissez RESTAURER pour confirmer.", "Restaurer", "RESTAURER")) return;
        await RunAsync("Restauration en cours. Patientez avant toute autre action…", async () =>
        {
            var data = await transport.SendAsync(settings, "restore", item.Name);
            if (Get(data, "restored").ValueKind != JsonValueKind.True) throw new InvalidOperationException("La restauration n’a pas été confirmée par le serveur.");
            ShowMessage("Restauration terminée. Contrôlez le service et les données avant de reprendre les parties.");
            await LoadBackupsCoreAsync();
        });
    }
    private ConnectionSettings ReadForm()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) throw new InvalidOperationException("Le port SSH doit être un nombre.");
        return new ConnectionSettings { Host = HostBox.Text.Trim(), User = UserBox.Text.Trim(), Port = port, KeyPath = Environment.ExpandEnvironmentVariables(KeyBox.Text.Trim()) };
    }
    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir la clé SSH privée", Filter = "Clés SSH et autres fichiers|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) KeyBox.Text = dialog.FileName;
    }
    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (demo) { ShowMessage("Quittez le mode démonstration pour enregistrer une connexion réelle."); return; }
        bool saved = false;
        await RunAsync("Enregistrement des réglages locaux…", () =>
        {
            var next = ReadForm(); next.Save(); settings = next;
            EndpointText.Text = $"{settings.User}@{settings.Host}";
            saved = true;
            return Task.CompletedTask;
        });
        if (saved) { ShowPage("Overview"); await RefreshAsync(); }
    }
    private async void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (demo) { ShowMessage("Aucune empreinte réelle n’est chargée en démonstration."); return; }
        await RunAsync("Lecture de l’empreinte publique du serveur…", async () =>
        {
            var form = ReadForm();
            var scan = await SshTransport.ScanFingerprintAsync(form);
            var message = $"Serveur : {form.Host}:{form.Port}\n\n{scan.Fingerprint}\n\nComparez cette empreinte à celle affichée dans la console de l’hébergeur. Sa lecture sur le réseau ne prouve pas l’identité du serveur.\n\nConfirmez uniquement si les deux empreintes correspondent.";
            if (!ConfirmDialog.Show(this, "Vérifier l’identité SSH", message, "J’ai comparé, faire confiance")) return;
            Directory.CreateDirectory(ConnectionSettings.DirectoryPath);
            await File.AppendAllTextAsync(ConnectionSettings.KnownHostsPath, scan.KeyLine);
            ShowMessage("Empreinte enregistrée sur ce PC. Vous pouvez enregistrer la connexion.");
        });
    }
}

internal sealed record BackupItem(string Name, long Size, string ModifiedUtc)
{
    public string Label => $"{Name}   ·   {Size / 1048576d:0.0} Mo";
}

internal sealed record ActivityItem(string Label, string Detail);

internal sealed class ConfirmDialog : Window
{
    public static bool Show(Window owner, string title, string message, string buttonText, string? requiredText = null)
    {
        var dialog = new ConfirmDialog { Owner = owner, Title = title, Width = 600, SizeToContent = SizeToContent.Height, MinHeight = 260, MaxHeight = 680, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 20) });
        TextBox? input = null;
        if (requiredText is not null) { input = new TextBox { Margin = new Thickness(0, 0, 0, 20) }; panel.Children.Add(input); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var confirm = new Button { Content = buttonText, Style = (Style)Application.Current.FindResource("Primary"), IsEnabled = requiredText is null };
        if (input is not null) input.TextChanged += (_, _) => confirm.IsEnabled = input.Text == requiredText;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons); dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }
}
