using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EclatServerPanel;

public partial class MainWindow : Window
{
    const int RefreshSeconds = 20, HistoryLength = 30;
    static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    static readonly string[] AvatarColors = ["#5EE6FF", "#8B7BFF", "#3DDC97", "#FFB547", "#FF8EA3", "#7FB2FF"];
    private readonly IPanelTransport transport;
    private readonly bool demo;
    private readonly bool isolatedSettings;
    private ConnectionSettings settings;
    private bool busy;
    private bool closed, lastCheckFailed;
    private bool suppressNavigationLoad;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int pendingOperations;
    private ConnectionSettings? operationSettings;
    private ConnectionSettings ActiveSettings => operationSettings ?? settings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(RefreshSeconds) };
    private string currentPage = "Overview";
    private readonly Dictionary<string, FrameworkElement> pages;
    private readonly Dictionary<string, RadioButton> navigation;
    private readonly Dictionary<string, string> headings = new()
    {
        ["Overview"] = "Vue d’ensemble", ["Sessions"] = "Joueurs et salons",
        ["Logs"] = "Journaux", ["Backups"] = "Sauvegardes", ["Diagnostics"] = "Diagnostic", ["Connection"] = "Connexion", ["Map"] = "Carte des joueurs", ["Commands"] = "Administration",
    };
    private readonly Queue<double> playersHistory = new(), roomsHistory = new(), latencyHistory = new(), loadHistory = new();
    private List<LogLine> logLines = [];
    private DateTime? lastCheck;
    private TaskCompletionSource<bool>? dialog;
    private string? dialogRequired;
    private long? lastGamePid, excludedGamePid;

    public MainWindow(bool demo) : this(demo, null, null, true) { }

    internal MainWindow(bool demo, IPanelTransport? injectedTransport, ConnectionSettings? injectedSettings, bool automaticRefresh)
    {
        InitializeComponent();
        this.demo = demo;
        isolatedSettings = injectedSettings is not null;
        transport = injectedTransport ?? (demo ? new DemoTransport() : new SshTransport());
        settings = injectedSettings ?? (demo ? new ConnectionSettings { Host = "serveur.example", User = "eclat-panel", Port = 22, KeyPath = "Clé locale — démonstration" } : ConnectionSettings.Load());
        pages = new() { ["Overview"] = OverviewPage, ["Sessions"] = SessionsPage, ["Logs"] = LogsPage, ["Backups"] = BackupsPage, ["Diagnostics"] = DiagnosticsPage, ["Connection"] = ConnectionPage };
        navigation = new() { ["Overview"] = NavOverview, ["Sessions"] = NavSessions, ["Logs"] = NavLogs, ["Backups"] = NavBackups, ["Diagnostics"] = NavDiagnostics, ["Connection"] = NavConnection };
        pages["Map"] = MapPage; pages["Commands"] = CommandsPage; navigation["Map"] = NavMap; navigation["Commands"] = NavCommands;
        InitializeAdministration();
        ReloadConnections();
        HostBox.Text = settings.Host;
        UserBox.Text = settings.User;
        PortBox.Text = settings.Port.ToString();
        KeyBox.Text = settings.KeyPath;
        EndpointText.Text = string.IsNullOrEmpty(settings.Host) ? "Aucun serveur configuré" : $"{settings.User}@{settings.Host}";
        ModeBadge.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        RefreshTrustChip();
        HostBox.TextChanged += (_, _) => RefreshTrustChip();
        PortBox.TextChanged += (_, _) => RefreshTrustChip();
        UpdateServiceButtons(null);
        LogSearch.TextChanged += (_, _) => LogSearchHint.Visibility = LogSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StateChanged += (_, _) => { Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; };
        Loaded += async (_, _) =>
        {
            suppressNavigationLoad = true;
            navigation[string.IsNullOrEmpty(settings.Host) ? "Connection" : "Overview"].IsChecked = true;
            suppressNavigationLoad = false;
            if (!automaticRefresh) return;
            if (demo) Toast("Mode démonstration : informations fictives, aucune connexion au VPS.", ToastKind.Info);
            if (demo) await SeedDemoHistoryAsync();
            if (!string.IsNullOrEmpty(settings.Host)) await RefreshAsync();
            timer.Start();
            RestartRefreshRing();
        };
        timer.Tick += async (_, _) =>
        {
            RestartRefreshRing();
            if (!busy && dialog is null && !string.IsNullOrEmpty(settings.Host))
            {
                if (currentPage == "Diagnostics") await LoadDiagnosticsAsync();
                else if (currentPage == "Commands") await LoadAdministrationAsync();
                else await RefreshAsync();
            }
        };
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => { UpdateCheckAge(); MapPage.Tick(); };
        clock.Start();
        Closed += (_, _) => { closed = true; timer.Stop(); clock.Stop(); dialog?.TrySetResult(false); dialog = null; };
    }

    // The demonstration starts with a few minutes of history, like a panel left open.
    private async Task SeedDemoHistoryAsync()
    {
        for (var i = 0; i < 14; i++)
        {
            var data = await transport.SendAsync(ActiveSettings, "status");
            var monitor = Get(data, "monitor");
            Push(playersHistory, Get(monitor, "players").GetArrayLength());
            Push(roomsHistory, Get(monitor, "rooms").GetArrayLength() - (i < 4 ? 1 : 0));
            Push(latencyHistory, Get(Get(data, "protocol"), "latencyMs").GetDouble());
            Push(loadHistory, Loads(Get(Get(data, "metrics"), "load"))[0]);
        }
    }

    // ---- Window chrome -------------------------------------------------
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Navigation ----------------------------------------------------
    private async void Navigate_Checked(object sender, RoutedEventArgs e)
    {
        if (pages is null) return;
        var page = (string)((RadioButton)sender).Tag;
        ShowPage(page);
        if (suppressNavigationLoad) return;
        if (page == "Logs") { if (AdminLogTab.IsChecked == true) await LoadAdminAuditAsync(); else await LoadLogsAsync(); }
        else if (page == "Commands") await LoadAdministrationAsync();
        else if (page == "Backups") await LoadBackupsAsync();
        else if (page == "Diagnostics") await LoadDiagnosticsAsync();
        else if (page is "Overview" or "Sessions" or "Map" && !string.IsNullOrEmpty(settings.Host)) await RefreshAsync();
    }

    private void ShowPage(string name)
    {
        currentPage = name;
        foreach (var page in pages) page.Value.Visibility = page.Key == name ? Visibility.Visible : Visibility.Collapsed;
        if (name != "Map") MapPage.CloseOverlay();
        Motion.Enter(pages[name]);
        PageTitle.Text = headings[name];
        Motion.Enter(PageTitle, 6, 220);
        MovePill(navigation[name]);
    }

    // The selection pill slides to the chosen entry.
    private void MovePill(RadioButton target)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var y = target.TranslatePoint(new Point(0, 0), NavList).Y;
            var move = (TranslateTransform)NavPill.RenderTransform;
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
        }, DispatcherPriority.Loaded);
    }

    private void SeeSessions_Click(object sender, RoutedEventArgs e) => NavSessions.IsChecked = true;

    public async Task WaitForIdleAsync()
    {
        while (pendingOperations > 0) await Task.Delay(20);
    }

    // Capture helper: show a page and let its animations settle.
    internal async Task ShowPageForCaptureAsync(string name)
    {
        navigation[name].IsChecked = true;
        await Task.Delay(150);
        await WaitForIdleAsync();
        await Task.Delay(1400);
    }

    internal async Task ShowRestoreDialogForCaptureAsync()
    {
        _ = ConfirmAsync("Restaurer cette sauvegarde ?", "Instantané du vendredi 23 octobre · 17:10. Les données actuelles seront remplacées et les parties interrompues. Le serveur crée d’abord une copie de retour arrière.", "Restaurer", danger: true, code: "2026-10-23_171000.tar.gz", requiredText: "RESTAURER");
        await Task.Delay(600);
    }

    // ---- Busy state, toasts and refresh ring ---------------------------
    private async Task RunAsync(string activity, Func<Task> work)
    {
        if (closed) return;
        pendingOperations++;
        await operationGate.WaitAsync();
        bool failed = false;
        try
        {
            if (closed) return;
            busy = true;
            operationSettings = settings.Snapshot();
            ActivityText.Text = activity;
            SetBusy(true);
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed = true;
            Toast(ex.Message, ToastKind.Error);
            ActivityText.Text = "Action interrompue";
        }
        finally
        {
            operationSettings = null;
            busy = false;
            pendingOperations--;
            if (!closed) { SetBusy(false); if (!failed) ActivityText.Text = "Prêt"; }
            operationGate.Release();
        }
    }

    private void SetBusy(bool on)
    {
        Cursor = on ? System.Windows.Input.Cursors.AppStarting : null;
        BusyBar.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        RefreshButton.IsEnabled = !on;
        foreach (var page in pages.Values) page.IsEnabled = !on;
        var spin = (Storyboard)FindResource("Spin");
        var sweep = (Storyboard)FindResource("Busy");
        if (on) { spin.Begin(this, true); sweep.Begin(this, true); }
        else { spin.Stop(this); sweep.Stop(this); }
    }

    private void RestartRefreshRing()
    {
        RefreshRing.BeginAnimation(ArcGauge.ValueProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(RefreshSeconds)));
    }

    private void UpdateCheckAge()
    {
        if (lastCheck is null) return;
        if (lastCheckFailed) { UpdatedText.Text = "Dernier contrôle en échec"; return; }
        var age = (int)(DateTime.Now - lastCheck.Value).TotalSeconds;
        if (age > PanelData.MaximumAge.TotalSeconds) { MarkStatusUnknown("Le dernier contrôle date de plus d’une minute. Actualisez pour connaître l’état actuel."); return; }
        UpdatedText.Text = age < 5 ? "Contrôlé à l’instant" : age < 60 ? $"Contrôlé il y a {age} s" : $"Contrôlé à {lastCheck:HH:mm}";
    }

    internal enum ToastKind { Info, Success, Error }

    private void Toast(string message, ToastKind kind = ToastKind.Success)
    {
        var (icon, color, back) = kind switch
        {
            ToastKind.Error => ("\uEA39", "#FF5C7A", "#2A1424"),
            ToastKind.Info => ("\uE946", "#5EE6FF", "#12223F"),
            _ => ("\uE930", "#3DDC97", "#10282A"),
        };
        var text = new TextBlock { Text = message, Foreground = (Brush)FindResource("Text"), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16, Foreground = Motion.Hex(color), Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(text, 1);
        row.Children.Add(glyph); row.Children.Add(text);
        var card = new Border
        {
            Background = Motion.Hex(back), BorderBrush = Motion.Hex(color), BorderThickness = new Thickness(1, 1, 1, 1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 13, 16, 13), Margin = new Thickness(0, 10, 0, 0), Child = row, Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
            RenderTransform = new TranslateTransform(40, 0), Opacity = 0,
        };
        Toasts.Children.Add(card);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
        void Dismiss()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => Toasts.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(220)));
        }
        card.MouseLeftButtonUp += (_, _) => Dismiss();
        var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 9 : 5) };
        life.Tick += (_, _) => { life.Stop(); Dismiss(); };
        life.Start();
        while (Toasts.Children.Count > 4) Toasts.Children.RemoveAt(0);
    }

    // ---- In-window confirmation -----------------------------------------
    private Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger, string? code = null, string? requiredText = null)
    {
        dialog?.TrySetResult(false);
        dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogRequired = requiredText;
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogCode.Text = code ?? "";
        DialogCodeBox.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        DialogInputBox.Visibility = requiredText is null ? Visibility.Collapsed : Visibility.Visible;
        DialogInputLabel.Text = requiredText is null ? "" : $"SAISISSEZ {requiredText} POUR CONFIRMER";
        DialogInput.Text = "";
        DialogConfirm.Content = confirmText;
        DialogConfirm.Style = (Style)FindResource(danger ? "DangerButton" : "Primary");
        DialogConfirm.IsEnabled = requiredText is null;
        DialogIcon.Text = danger ? "\uE7BA" : "\uE83D";
        DialogIcon.Foreground = (Brush)FindResource(danger ? "Danger" : "Cyan");
        DialogIconBox.Background = Motion.Hex(danger ? "#3A1A2A" : "#12304A");
        DialogLayer.Visibility = Visibility.Visible;
        DialogDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        var scale = (ScaleTransform)DialogCard.RenderTransform;
        var pop = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        DialogCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        (requiredText is null ? (Control)DialogCancel : DialogInput).Focus();
        return dialog.Task;
    }

    private void CloseDialog(bool result)
    {
        if (result && dialogRequired is not null && DialogInput.Text != dialogRequired) return;
        var completedDialog = dialog;
        if (completedDialog is null) return;
        dialog = null;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => { if (dialog is null) DialogLayer.Visibility = Visibility.Collapsed; };
        DialogLayer.BeginAnimation(OpacityProperty, null);
        DialogCard.BeginAnimation(OpacityProperty, fade);
        DialogDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        completedDialog.TrySetResult(result);
    }

    private void DialogInput_Changed(object sender, TextChangedEventArgs e) => DialogConfirm.IsEnabled = dialogRequired is null || DialogInput.Text == dialogRequired;
    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
    private void DialogConfirm_Click(object sender, RoutedEventArgs e) => CloseDialog(true);

    // ---- Status --------------------------------------------------------
    public async Task RefreshAsync() => await RunAsync("Vérification du service et du protocole…", async () =>
    {
        try { RenderStatus(await transport.SendAsync(ActiveSettings, "status")); }
        catch
        {
            MarkStatusUnknown("Le dernier contrôle a échoué. Actualisez pour connaître l’état du serveur.");
            throw;
        }
    });

    private enum StateTone { Online, Degraded, Offline, Unknown }

    private void MarkStatusUnknown(string detail)
    {
        lastCheckFailed = true;
        SetState(StateTone.Unknown, "État non confirmé", detail);
        UpdatedText.Text = "Dernier contrôle en échec";
        UpdateServiceButtons(null);
        StatusChips.Children.Clear();
        PlayersValue.Show(null); RoomsValue.Show(null); LatencyValue.Show(null);
        PlayersBadge.Visibility = Visibility.Collapsed;
        ShowPeople([], [], "Activité non confirmée");
        SessionsText.Text = "Activité non confirmée : aucun état actuel disponible.";
        UptimeValue.Text = UptimeDetail.Text = HostText.Text = "—";
        MemoryGauge.Animate(0); DiskGauge.Animate(0); ProcessGauge.Animate(0);
        MemoryPercent.Text = DiskPercent.Text = ProcessValue.Text = "—";
        MemoryDetail.Text = DiskDetail.Text = ProcessDetail.Text = "Mesure non confirmée";
        LoadText.Text = LastErrorText.Text = "—";
        foreach (var counter in new[] { CounterConnections, CounterDisconnections, CounterRooms, CounterJoins, CounterErrors }) counter.Show(null);
        DiagnosticSections.ItemsSource = Array.Empty<DiagnosticSection>();
        DiagnosticsText.Text = "Diagnostic non confirmé : aucun état actuel disponible.";
        DiagnosticsEmpty.Visibility = Visibility.Visible;
        MapPage.Apply(default, false);
    }

    private void ResetConnectionData()
    {
        MapPage.Clear(); CommandsPage.Clear(); AdminJournal.Clear();
        lastCheck = null;
        lastGamePid = excludedGamePid = null;
        MarkStatusUnknown("Connexion modifiée. Le nouvel hôte doit être contrôlé.");
        playersHistory.Clear(); roomsHistory.Clear(); latencyHistory.Clear(); loadHistory.Clear();
        PlayersSpark.Show([]); RoomsSpark.Show([]); LatencySpark.Show([]); LoadSpark.Show([]);
        logLines.Clear(); ApplyLogFilter(false);
        BackupRows.ItemsSource = Array.Empty<BackupItem>();
        BackupsEmpty.Visibility = Visibility.Visible;
        BackupCount.Show(null); BackupSize.Text = BackupLatest.Text = "—";
        DiagnosticSections.ItemsSource = Array.Empty<DiagnosticSection>();
        DiagnosticsText.Text = "Aucun diagnostic pour cette connexion.";
        DiagnosticsEmpty.Visibility = Visibility.Visible;
    }

    private void SetState(StateTone tone, string title, string detail)
    {
        var color = tone switch { StateTone.Online => "#3DDC97", StateTone.Degraded => "#FFB547", StateTone.Offline => "#FF5C7A", _ => "#59638C" };
        var brush = Motion.Hex(color);
        if (ServerStatus.Text != title) { ServerStatus.Text = title; Motion.Enter(ServerStatus, 8, 260); }
        StatusDetail.Text = detail;
        Orb.Fill = new RadialGradientBrush(Lighten(color), (Color)ColorConverter.ConvertFromString(color)) { GradientOrigin = new Point(0.35, 0.3) };
        PulseRing.Stroke = brush;
        HaloColor.Color = (Color)ColorConverter.ConvertFromString("#66" + color[1..]);
        OrbIcon.Text = tone switch { StateTone.Online => "\uE930", StateTone.Degraded => "\uE7BA", StateTone.Offline => "\uE711", _ => "\uE703" };
        SideDot.Fill = brush; SideHalo.Fill = brush;
        SideStatus.Text = tone switch { StateTone.Online => "En ligne", StateTone.Degraded => "Jeu indisponible", StateTone.Offline => "Arrêté", _ => "État inconnu" };
        var pulse = (Storyboard)FindResource("Pulse");
        if (tone == StateTone.Online) pulse.Begin(this, true); else { pulse.Stop(this); PulseRing.Opacity = 0; }
    }

    static Color Lighten(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        byte Mix(byte v) => (byte)(v + (255 - v) * 0.45);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private void RenderStatus(JsonElement data)
    {
        var service = Get(data, "service");
        var protocol = Get(data, "protocol");
        var metrics = Get(data, "metrics");
        var monitor = Get(data, "monitor");
        var network = Get(data, "network");
        var host = Get(data, "host");
        var serviceState = Text(Get(service, "active"));
        if (serviceState is not ("active" or "inactive" or "failed" or "activating" or "deactivating" or "reloading"))
            throw new InvalidOperationException("La réponse ne contient pas un état de service reconnu.");
        bool active = serviceState == "active";
        bool transition = serviceState is "activating" or "deactivating" or "reloading";
        bool responds = Get(protocol, "ok").ValueKind == JsonValueKind.True;
        bool maintenance = Get(protocol, "maintenance").ValueKind == JsonValueKind.True || Get(monitor, "maintenance").ValueKind == JsonValueKind.True;
        if (active && responds && maintenance) SetState(StateTone.Degraded, "Maintenance", "Les nouvelles connexions sont refusées ; les joueurs présents peuvent terminer leur partie.");
        else if (active && responds) SetState(StateTone.Online, "En ligne", "Le jeu répond sur le VPS. L’annuaire et le tunnel indiquent l’état déclaré de l’accès Internet.");
        else if (transition) SetState(StateTone.Degraded, "Service en transition", "Le service change d’état. Le prochain contrôle précisera le résultat.");
        else if (responds) SetState(StateTone.Degraded, "Jeu détecté, service arrêté", "Le port du jeu répond mais le service attendu n’est pas actif. Consultez le diagnostic.");
        else if (active) SetState(StateTone.Degraded, "Service actif, jeu indisponible", "Le processus tourne mais le protocole du jeu ne répond pas. Consultez les journaux avant de redémarrer.");
        else SetState(StateTone.Offline, "Service arrêté", "Le service est arrêté ou en échec. Le diagnostic précise son état.");
        UpdateServiceButtons(transition ? null : active);

        double? latency = responds && Get(protocol, "latencyMs").ValueKind == JsonValueKind.Number ? Get(protocol, "latencyMs").GetDouble() : null;
        string publication = Fr(Text(Get(network, "publication")));
        string tunnel = Fr(Text(Get(network, "tunnel")));
        bool networkStale = !PanelData.IsRecent(network, DateTimeOffset.UtcNow);
        StatusChips.Children.Clear();
        Chip("\uE7F4", "Service", $"{Text(Get(service, "active"))} · {Text(Get(service, "sub"))}", active);
        Chip("\uEC4A", "Protocole", responds ? $"{latency:0.0} ms" : "sans réponse", responds);
        LatencyValue.ToolTip = "Durée de l’échange de contrôle effectué localement sur le VPS.";
        Chip("\uE774", "Annuaire Coop", publication + (networkStale ? " (ancien)" : ""), publication == "disponible" && !networkStale);
        Chip("\uE968", "Tunnel", tunnel + (networkStale ? " (ancien)" : ""), tunnel == "connecté" && !networkStale);

        var players = Get(monitor, "players");
        var rooms = Get(monitor, "rooms");
        long monitorPid = Number(Get(monitor, "pid"));
        bool fresh = PanelData.HasActivity(monitor, active, responds, DateTimeOffset.UtcNow)
            && (excludedGamePid is null || monitorPid > 0 && monitorPid != excludedGamePid);
        if (fresh && monitorPid > 0) { lastGamePid = monitorPid; excludedGamePid = null; }
        MapPage.Apply(monitor, fresh);
        var playerItems = fresh ? PlayerItems(players, rooms) : [];
        var roomItems = fresh ? RoomItems(rooms, players) : [];
        SetCounter(PlayersValue, fresh ? playerItems.Count : null);
        SetCounter(RoomsValue, fresh ? roomItems.Count : null);
        SetCounter(LatencyValue, latency);
        if (fresh) { Push(playersHistory, playerItems.Count); Push(roomsHistory, roomItems.Count); }
        if (latency is { } ms) Push(latencyHistory, ms);
        PlayersSpark.Show(playersHistory.ToList()); RoomsSpark.Show(roomsHistory.ToList()); LatencySpark.Show(latencyHistory.ToList());
        PlayersBadge.Visibility = fresh && playerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayersBadgeText.Text = playerItems.Count.ToString();
        ShowPeople(playerItems, roomItems, fresh ? null : "Activité non confirmée : le jeu ne répond pas ou ses données sont anciennes.");

        long gameUptime = Number(Get(monitor, "uptimeSeconds")), vpsUptime = Number(Get(host, "uptimeSeconds"));
        UptimeValue.Text = fresh && gameUptime > 0 ? Duration(gameUptime) : "—";
        UptimeDetail.Text = "VPS : " + (vpsUptime > 0 ? Duration(vpsUptime) : "—") + (Text(Get(host, "nodeVersion")) is var node && node != "—" ? " · Node " + node.TrimStart('v') : "");
        HostText.Text = Text(Get(host, "name")) is var name && name != "—" ? name : "";

        long total = Number(Get(metrics, "memoryTotalBytes")), available = Number(Get(metrics, "memoryAvailableBytes"));
        long diskTotal = Number(Get(metrics, "diskTotalBytes")), diskFree = Number(Get(metrics, "diskFreeBytes"));
        long rss = fresh ? Number(Get(Get(monitor, "memory"), "rssBytes")) : 0;
        if (ValidResourcePair(metrics, "memoryTotalBytes", "memoryAvailableBytes")) { var used = total - available; MemoryGauge.Stroke = Tone((double)used / total); MemoryGauge.Animate((double)used / total); MemoryPercent.Text = $"{100d * used / total:0} %"; MemoryDetail.Text = $"{Gb(used)} / {Gb(total)}"; }
        else { MemoryGauge.Animate(0); MemoryPercent.Text = "—"; MemoryDetail.Text = "Mesure indisponible"; }
        if (ValidResourcePair(metrics, "diskTotalBytes", "diskFreeBytes")) { var used = diskTotal - diskFree; DiskGauge.Stroke = Tone((double)used / diskTotal); DiskGauge.Animate((double)used / diskTotal); DiskPercent.Text = $"{100d * used / diskTotal:0} %"; DiskDetail.Text = $"{Gb(diskFree)} libres"; }
        else { DiskGauge.Animate(0); DiskPercent.Text = "—"; DiskDetail.Text = "Mesure indisponible"; }
        if (rss > 0 && total > 0)
        {
            var share = (double)rss / total;
            ProcessGauge.Stroke = (Brush)FindResource("Crystal"); ProcessGauge.Animate(share);
            ProcessValue.Text = (rss / 1048576d).ToString("0", French); ProcessDetail.Text = $"{share * 100:0} % de la mémoire du VPS";
        }
        else { ProcessGauge.Animate(0); ProcessValue.Text = "—"; ProcessDetail.Text = "Processus non mesuré"; }
        var load = Loads(Get(metrics, "load"));
        LoadText.Text = load.Count > 0 ? string.Join("  ·  ", load.Select(v => v.ToString("0.00", French))) : "—";
        if (load.Count > 0) Push(loadHistory, load[0]);
        LoadSpark.Show(loadHistory.ToList());

        var counters = fresh ? Get(monitor, "counters") : default;
        CounterValue(CounterConnections, Get(counters, "connections"));
        CounterValue(CounterDisconnections, Get(counters, "disconnections"));
        CounterValue(CounterRooms, Get(counters, "roomsCreated"));
        CounterValue(CounterJoins, Get(counters, "roomJoins"));
        CounterValue(CounterErrors, Get(counters, "errors"));
        var lastError = fresh ? Get(monitor, "lastError") : default;
        LastErrorText.Text = !fresh ? "Non confirmé" : lastError.ValueKind == JsonValueKind.Object
            ? Text(Get(lastError, "code")) + (DateTimeOffset.TryParse(Text(Get(lastError, "at")), out var errorAt) ? " · " + errorAt.ToLocalTime().ToString("dd/MM HH:mm", French) : "")
            : "Aucun";
        LastErrorText.Foreground = (Brush)FindResource(!fresh ? "Muted" : lastError.ValueKind == JsonValueKind.Object ? "Warning" : "Success");

        SessionsText.Text = fresh ? Pretty(monitor) : "Activité non confirmée : données absentes ou périmées.";
        lastCheck = DateTime.Now;
        lastCheckFailed = false;
        UpdateCheckAge();
    }

    private void Chip(string icon, string label, string value, bool good)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = (Brush)FindResource(good ? "Success" : "Warning"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        row.Children.Add(new TextBlock { Text = label + " ", Foreground = (Brush)FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        StatusChips.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = row });
    }

    private void UpdateServiceButtons(bool? active)
    {
        // Only the actions that make sense now are offered.
        StartButton.Visibility = active == false ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = active == true ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = active == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPeople(List<PlayerItem> players, List<RoomItem> rooms, string? unavailable)
    {
        OnlinePlayers.ItemsSource = players.Take(6).ToList();
        OnlineEmpty.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlineEmptyText.Text = unavailable ?? "Personne pour l’instant";
        PlayerList.ItemsSource = players;
        PlayersEmpty.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayersEmptyText.Text = unavailable ?? "Aucun joueur connecté";
        RoomCards.ItemsSource = rooms;
        RoomsEmpty.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RoomsEmptyText.Text = unavailable ?? "Aucun salon ouvert";
    }

    private static string Fr(string state) => state switch { "available" => "disponible", "unavailable" => "indisponible", "connected" => "connecté", "disconnected" => "déconnecté", var v => v };
    private static Brush Tone(double ratio) => ratio > 0.9 ? Motion.Hex("#FF5C7A") : ratio > 0.75 ? Motion.Hex("#FFB547") : (Brush)Application.Current.FindResource("Crystal");
    private static void Push(Queue<double> history, double value) { history.Enqueue(value); while (history.Count > HistoryLength) history.Dequeue(); }
    private static void SetCounter(CountUpText target, double? value)
    {
        target.Show(value);
        // WPF does not raise a value-change callback for an animation 0 -> 0.
        if (value == 0 && target.Value == 0) target.Text = 0d.ToString(target.Format, French) + target.Suffix;
    }
    private static void CounterValue(CountUpText target, JsonElement value) => SetCounter(target, value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
    private static string Gb(long bytes) => (bytes / 1073741824d).ToString(bytes >= 10L * 1073741824 ? "0" : "0.0", French) + " Go";
    private static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} j {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00}" : $"{span.Minutes} min";
    }
    private static List<double> Loads(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToList(),
        JsonValueKind.String => value.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN).Where(v => !double.IsNaN(v)).ToList(),
        _ => [],
    };

    // Accept the server's monitor format, and older field names when present.
    private static List<PlayerItem> PlayerItems(JsonElement players, JsonElement rooms)
    {
        var roomNames = new Dictionary<string, string>();
        if (rooms.ValueKind == JsonValueKind.Array)
            foreach (var room in rooms.EnumerateArray())
                if (First(room, "id", "roomId", "code") is { } id) roomNames[id] = First(room, "name", "code") ?? id;
        var result = new List<PlayerItem>();
        if (players.ValueKind != JsonValueKind.Array) return result;
        foreach (var player in players.EnumerateArray())
        {
            var name = First(player, "name", "playerName", "trainerName", "username", "nickname") ?? "Joueur";
            var roomId = First(player, "roomId", "roomCode");
            var room = roomId is null ? "Hors salon" : "Salon " + (roomNames.TryGetValue(roomId, out var label) ? label : roomId);
            result.Add(new(name, room, Avatar(name)));
        }
        return result;
    }

    private List<RoomItem> RoomItems(JsonElement rooms, JsonElement players)
    {
        var members = new Dictionary<string, List<string>>();
        if (players.ValueKind == JsonValueKind.Array)
            foreach (var player in players.EnumerateArray())
                if (First(player, "roomId", "roomCode") is { } room) { if (!members.TryGetValue(room, out var list)) members[room] = list = []; list.Add(First(player, "name") ?? "Joueur"); }
        var result = new List<RoomItem>();
        if (rooms.ValueKind != JsonValueKind.Array) return result;
        foreach (var room in rooms.EnumerateArray())
        {
            var id = First(room, "id", "roomId", "code") ?? "";
            var name = First(room, "name", "code") ?? "Salon";
            var names = members.TryGetValue(id, out var list) ? list : Get(room, "players").ValueKind == JsonValueKind.Array ? Get(room, "players").EnumerateArray().Select(p => p.ToString()).ToList() : [];
            var count = Get(room, "count").ValueKind == JsonValueKind.Number ? Get(room, "count").GetInt32() : names.Count;
            var capacity = Get(room, "capacity").ValueKind == JsonValueKind.Number ? Get(room, "capacity").GetInt32() : 0;
            var isPrivate = First(room, "visibility") == "private";
            result.Add(new(name, isPrivate, count, capacity, names.Count > 0 ? string.Join(", ", names) : "Membres non communiqués") { Id = id, Origin = ActiveSettings.Snapshot() });
        }
        return result;
    }

    private static string? First(JsonElement element, params string[] keys)
    {
        foreach (var key in keys) { var value = Get(element, key); if (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())) return value.GetString(); }
        return null;
    }
    private static Brush Avatar(string name) => Motion.Hex(AvatarColors[(int)((uint)name.Aggregate(17, (h, c) => h * 31 + c) % AvatarColors.Length)]);

    private static JsonElement Get(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "—" : value.ToString();
    private static long Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : value.ValueKind == JsonValueKind.Number ? (long)value.GetDouble() : 0;
    private static bool ValidResourcePair(JsonElement data, string totalKey, string freeKey)
        => Get(data, totalKey).ValueKind == JsonValueKind.Number && Get(data, totalKey).TryGetInt64(out var total) && total > 0 && Get(data, freeKey).ValueKind == JsonValueKind.Number
            && Get(data, freeKey).TryGetInt64(out var free) && free >= 0 && free <= total;
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (currentPage == "Logs") { if (AdminLogTab.IsChecked == true) await LoadAdminAuditAsync(); else await LoadLogsAsync(); }
        else if (currentPage == "Commands") await LoadAdministrationAsync();
        else if (currentPage == "Backups") await LoadBackupsAsync();
        else if (currentPage == "Diagnostics") await LoadDiagnosticsAsync();
        else await RefreshAsync();
        RestartRefreshRing();
    }

    private void RawSessions_Changed(object sender, RoutedEventArgs e) => Reveal(RawSessionsBox, RawSessionsToggle.IsChecked == true);
    private void RawDiagnostics_Changed(object sender, RoutedEventArgs e) => Reveal(RawDiagnosticsBox, RawDiagnosticsToggle.IsChecked == true);
    private static void Reveal(FrameworkElement element, bool show) { element.Visibility = show ? Visibility.Visible : Visibility.Collapsed; if (show) Motion.Enter(element, 8, 200); }

    // ---- Service control -----------------------------------------------
    private async void Service_Click(object sender, RoutedEventArgs e)
        => await ControlServiceAsync((string)((Button)sender).Tag);

    internal Task ControlServiceAsync(string action) => RunAsync("Préparation de l’action…", async () =>
    {
        if (action is not ("start" or "stop" or "restart")) throw new InvalidOperationException("Action de service inconnue.");
        if (action is "stop" or "restart" && !await ConfirmAsync(action == "stop" ? "Arrêter le serveur ?" : "Redémarrer le serveur ?",
            "Les parties en cours seront interrompues. Les joueurs devront se reconnecter.", action == "stop" ? "Arrêter" : "Redémarrer", danger: true)) return;
        if (closed) return;
        ActivityText.Text = action == "start" ? "Démarrage du service…" : action == "stop" ? "Arrêt du service…" : "Redémarrage du service…";
        bool acknowledged = false;
        try
        {
            await transport.SendAsync(ActiveSettings, action); acknowledged = true;
            if (action is "start" or "restart") excludedGamePid = lastGamePid;
        }
        finally { await RefreshAfterActionAsync(action, acknowledged); }
    });

    private async Task RefreshAfterActionAsync(string action, bool acknowledged)
    {
        try
        {
            var state = await transport.SendAsync(ActiveSettings, "status");
            if (closed) return;
            RenderStatus(state);
            bool active = Text(Get(Get(state, "service"), "active")) == "active";
            bool responds = Get(Get(state, "protocol"), "ok").ValueKind == JsonValueKind.True;
            bool stopped = Text(Get(Get(state, "service"), "active")) == "inactive";
            if (acknowledged)
                Toast(action == "stop" && stopped ? "Service arrêté." : action != "stop" && active && responds ? "Le service est actif et le jeu répond." : "Commande reçue. Le dernier état du service est affiché ; le jeu peut encore être en démarrage.",
                    action == "stop" && stopped || action != "stop" && active && responds ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarkStatusUnknown("L’action a été envoyée, mais son résultat actuel n’a pas pu être contrôlé. Actualisez avant de décider d’une nouvelle action.");
            Toast("Contrôle après action indisponible : " + ex.Message, ToastKind.Info);
        }
    }

    // ---- Logs ----------------------------------------------------------
    private static readonly Regex JournalLine = new(@"^(?<time>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:[+-]\d\d:?\d\d|Z)?)\s+\S+\s+(?<unit>[^:\s]+):\s?(?<message>.*)$");
    private static readonly Regex ErrorWords = new(@"\b(error|erreur|fail(ed|ure)?|échec|exception|fatal|crash)\b", RegexOptions.IgnoreCase);
    private static readonly Regex WarnWords = new(@"\b(warn(ing)?|avertissement|indisponible|timeout|retry)\b", RegexOptions.IgnoreCase);

    private async Task LoadLogsAsync() => await RunAsync("Lecture des journaux…", async () =>
    {
        if (currentPage != "Logs") return;
        var data = await transport.SendAsync(ActiveSettings, "logs");
        logLines = Text(Get(data, "text")).Split('\n').Where(line => line.Trim().Length > 0).Select(ParseLog).ToList();
        ApplyLogFilter(scrollToEnd: true);
    });

    private static LogLine ParseLog(string raw)
    {
        var match = JournalLine.Match(raw.TrimEnd('\r'));
        string time = "", message = raw.TrimEnd('\r');
        if (match.Success)
        {
            message = match.Groups["message"].Value;
            time = DateTimeOffset.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToLocalTime().ToString("HH:mm:ss") : match.Groups["time"].Value[11..Math.Min(19, match.Groups["time"].Value.Length)];
        }
        else if (Regex.Match(message, @"^(?<t>\d\d:\d\d:\d\d)\s+(?<m>.*)$") is { Success: true } simple) { time = simple.Groups["t"].Value; message = simple.Groups["m"].Value; }
        var level = ErrorWords.IsMatch(message) ? LogLevel.Error : WarnWords.IsMatch(message) ? LogLevel.Warn : LogLevel.Info;
        return new LogLine(time, level, message);
    }

    private void LogFilter_Changed(object sender, RoutedEventArgs e) { if (LogLines is not null) ApplyLogFilter(scrollToEnd: false); }

    private void ApplyLogFilter(bool scrollToEnd)
    {
        var query = LogSearch.Text.Trim();
        var minimum = LogError.IsChecked == true ? LogLevel.Error : LogWarn.IsChecked == true ? LogLevel.Warn : LogLevel.Info;
        var visible = logLines.Where(line => line.Severity >= minimum && (query.Length == 0 || line.Message.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        LogLines.ItemsSource = visible;
        LogCount.Text = $"{visible.Count} ligne(s) sur {logLines.Count}" + (logLines.Count(l => l.Severity == LogLevel.Error) is var errors and > 0 ? $" · {errors} erreur(s)" : "");
        if (scrollToEnd && visible.Count > 0) LogLines.ScrollIntoView(visible[^1]);
    }

    private async void Logs_Click(object sender, RoutedEventArgs e) => await LoadLogsAsync();

    // ---- Diagnostics ---------------------------------------------------
    private async Task LoadDiagnosticsAsync() => await RunAsync("Diagnostic du serveur…", async () =>
    {
        if (currentPage != "Diagnostics") return;
        try
        {
            var data = await transport.SendAsync(ActiveSettings, "diagnostics");
            RenderStatus(Get(data, "status"));
            DiagnosticsText.Text = Pretty(data);
            DiagnosticSections.ItemsSource = DiagnosticCards(data);
            DiagnosticsEmpty.Visibility = Visibility.Collapsed;
            Motion.Enter(DiagnosticSections);
        }
        catch
        {
            DiagnosticSections.ItemsSource = Array.Empty<DiagnosticSection>();
            DiagnosticsText.Text = "Le diagnostic n’a pas pu être actualisé.";
            DiagnosticsEmpty.Visibility = Visibility.Visible;
            MarkStatusUnknown("Le diagnostic a échoué. Actualisez pour connaître l’état du serveur.");
            throw;
        }
    });

    private List<DiagnosticSection> DiagnosticCards(JsonElement data)
    {
        var status = Get(data, "status").ValueKind == JsonValueKind.Object ? Get(data, "status") : data;
        var service = Get(status, "service"); var protocol = Get(status, "protocol"); var network = Get(status, "network");
        var metrics = Get(status, "metrics"); var host = Get(status, "host"); var monitor = Get(status, "monitor");
        Brush good = (Brush)FindResource("Success"), bad = (Brush)FindResource("Warning"), plain = (Brush)FindResource("Text");
        bool active = Text(Get(service, "active")) == "active", responds = Get(protocol, "ok").ValueKind == JsonValueKind.True;
        bool fresh = PanelData.HasActivity(monitor, active, responds, DateTimeOffset.UtcNow)
            && (excludedGamePid is null || Number(Get(monitor, "pid")) > 0 && Number(Get(monitor, "pid")) != excludedGamePid);
        bool networkFresh = PanelData.IsRecent(network, DateTimeOffset.UtcNow);
        long total = Number(Get(metrics, "memoryTotalBytes")), available = Number(Get(metrics, "memoryAvailableBytes"));
        long diskTotal = Number(Get(metrics, "diskTotalBytes")), diskFree = Number(Get(metrics, "diskFreeBytes"));
        var sections = new List<DiagnosticSection>
        {
            new("Service systemd", "\uE7F4", [new("État", Text(Get(service, "active")), active ? good : bad), new("Sous-état", Text(Get(service, "sub")), plain), new("PID", Text(Get(service, "pid")), plain)]),
            new("Protocole du jeu", "\uEC4A", [new("Réponse", responds ? "oui" : "non", responds ? good : bad), new("Réponse locale VPS", responds ? Text(Get(protocol, "latencyMs")) + " ms" : "—", plain), new("Erreur", Text(Get(protocol, "error")) is var err && err != "—" ? err : "aucune", err != "—" ? bad : plain)]),
            new("Machine", "\uE977", [new("Nom", Text(Get(host, "name")), plain), new("Démarrée depuis", Number(Get(host, "uptimeSeconds")) is var up and > 0 ? Duration(up) : "—", plain), new("Node.js", Text(Get(host, "nodeVersion")), plain)]),
            new("Ressources", "\uE9F5", [new("Mémoire", ValidResourcePair(metrics, "memoryTotalBytes", "memoryAvailableBytes") ? $"{Gb(total - available)} / {Gb(total)}" : "—", plain), new("Disque libre", ValidResourcePair(metrics, "diskTotalBytes", "diskFreeBytes") ? $"{Gb(diskFree)} / {Gb(diskTotal)}" : "—", diskTotal > 0 && diskFree < diskTotal / 10 ? bad : plain), new("Charge", Loads(Get(metrics, "load")) is { Count: > 0 } l ? string.Join(" · ", l.Select(v => v.ToString("0.00", French))) : "—", plain)]),
            new("Réseau Coop", "\uE968", [new("Annuaire", networkFresh ? Fr(Text(Get(network, "publication"))) : "Non confirmé", networkFresh && Text(Get(network, "publication")) == "available" ? good : bad), new("Tunnel", networkFresh ? Fr(Text(Get(network, "tunnel"))) : "Non confirmé", networkFresh && Text(Get(network, "tunnel")) == "connected" ? good : bad), new("Mis à jour", DateTimeOffset.TryParse(Text(Get(network, "updatedAt")), out var at) ? at.ToLocalTime().ToString("HH:mm:ss") : "—", plain)]),
            new("Activité", "\uE716", [new("Joueurs", fresh ? Get(monitor, "players").GetArrayLength().ToString() : "—", plain), new("Salons", fresh ? Get(monitor, "rooms").GetArrayLength().ToString() : "—", plain), new("Sauvegardes", Text(Get(data, "backupCount")), plain)]),
        };
        if (Get(data, "journal").ValueKind == JsonValueKind.String)
        {
            var recent = Get(data, "journal").GetString()!.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(5).Select(ParseLog);
            sections.Add(new("Journal récent", "\uE8A5", recent.Select(line => new DiagnosticRow(line.Time, line.Message.Length > 60 ? line.Message[..60] + "…" : line.Message, line.Severity == LogLevel.Error ? (Brush)FindResource("Danger") : line.Severity == LogLevel.Warn ? bad : plain)).ToList()));
        }
        return sections;
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await LoadDiagnosticsAsync();

    // ---- Backups -------------------------------------------------------
    private async Task LoadBackupsAsync() => await RunAsync("Lecture des sauvegardes…", async () =>
    {
        if (currentPage == "Backups") await LoadBackupsCoreAsync();
    });

    private async Task LoadBackupsCoreAsync()
    {
        var data = await transport.SendAsync(ActiveSettings, "backups");
        var items = Get(data, "items");
        var backups = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Select(item => new BackupItem(Text(Get(item, "name")), Number(Get(item, "size")), Text(Get(item, "modifiedUtc")), ActiveSettings.Snapshot())).ToList() : [];
        for (var i = 0; i < backups.Count; i++) backups[i].Latest = i == 0;
        BackupRows.ItemsSource = backups;
        Motion.Enter(BackupRows);
        BackupsEmpty.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetCounter(BackupCount, backups.Count);
        BackupSize.Text = backups.Count == 0 ? "—" : $"{backups.Sum(b => b.Size) / 1048576d:0.0} Mo";
        BackupLatest.Text = backups.Count == 0 ? "Jamais" : backups[0].Age;
    }

    private async void Backups_Click(object sender, RoutedEventArgs e) => await LoadBackupsAsync();

    private async void CreateBackup_Click(object sender, RoutedEventArgs e) => await RunAsync("Création d’un instantané cohérent…", async () =>
    {
        var data = await transport.SendAsync(ActiveSettings, "backup");
        Toast("Sauvegarde créée : " + Text(Get(data, "name")));
        await RefreshBackupListAfterActionAsync();
    });

    private async Task RefreshBackupListAfterActionAsync()
    {
        try { await LoadBackupsCoreAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BackupRows.ItemsSource = Array.Empty<BackupItem>();
            BackupsEmpty.Visibility = Visibility.Visible;
            BackupCount.Show(null); BackupSize.Text = BackupLatest.Text = "—";
            Toast("Action confirmée, mais la liste des sauvegardes n’a pas pu être relue : " + ex.Message, ToastKind.Info);
        }
    }

    private void CheckBackupOrigin(BackupItem item)
    {
        if (item.Origin != ActiveSettings) throw new InvalidOperationException("La connexion a changé. Actualisez les sauvegardes avant de choisir cet instantané.");
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ((Button)sender).Tag is not BackupItem item) return;
        if (demo) { Toast("Le téléchargement est disponible après connexion à un serveur réel.", ToastKind.Info); return; }
        var save = new SaveFileDialog { Title = "Enregistrer une copie de la base", Filter = "Base SQLite (*.sqlite)|*.sqlite", FileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.Name)) + ".sqlite", AddExtension = true, DefaultExt = ".sqlite" };
        if (save.ShowDialog(this) != true) return;
        await DownloadBackupAsync(item, save.FileName);
    }

    internal Task DownloadBackupAsync(BackupItem item, string destination) => RunAsync("Téléchargement et vérification SHA-256…", async () =>
    {
        CheckBackupOrigin(item);
        var data = await transport.SendAsync(ActiveSettings, "download", item.Name);
        await PanelFiles.SaveDownloadAsync(data, destination);
        Toast("Base enregistrée sur ce PC. Son empreinte SHA-256 correspond à celle du serveur.");
    });

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ((Button)sender).Tag is not BackupItem item) return;
        await RestoreBackupAsync(item);
    }

    internal Task RestoreBackupAsync(BackupItem item) => RunAsync("Préparation de la restauration…", async () =>
    {
        CheckBackupOrigin(item);
        if (!await ConfirmAsync("Restaurer cette sauvegarde ?", $"Instantané du {item.When}. Les données actuelles seront remplacées et les parties interrompues. Le serveur crée d’abord une copie de retour arrière.", "Restaurer", danger: true, code: item.Name, requiredText: "RESTAURER") || closed) return;
        ActivityText.Text = "Restauration en cours. Patientez avant toute autre action…";
        try
        {
            var data = await transport.SendAsync(ActiveSettings, "restore", item.Name);
            if (Get(data, "restored").ValueKind != JsonValueKind.True) throw new InvalidOperationException("La restauration n’a pas été confirmée par le serveur.");
            excludedGamePid = lastGamePid;
            Toast("Restauration terminée. Contrôlez le service et les données avant de reprendre les parties.");
            await RefreshBackupListAfterActionAsync();
        }
        finally { await RefreshAfterActionAsync("restore", false); }
    });

    // ---- Connection ----------------------------------------------------
    private ConnectionSettings ReadForm()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) throw new InvalidOperationException("Le port SSH doit être un nombre.");
        return new ConnectionSettings { Host = HostBox.Text.Trim(), User = UserBox.Text.Trim(), Port = port, KeyPath = Environment.ExpandEnvironmentVariables(KeyBox.Text.Trim()) };
    }

    private void RefreshTrustChip()
    {
        bool trusted = demo;
        try { if (!trusted && transport is SshTransport) trusted = KnownHostsTrust.HasTrustedHost(ReadForm()); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        TrustText.Text = trusted ? "✓ Empreinte enregistrée" : "Empreinte à vérifier";
        TrustText.Foreground = (Brush)FindResource(trusted ? "Success" : "Warning");
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choisir la clé SSH privée", Filter = "Clés SSH et autres fichiers|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) KeyBox.Text = picker.FileName;
    }

    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (demo) { Toast("Quittez le mode démonstration pour enregistrer une connexion réelle.", ToastKind.Info); return; }
        bool saved = false;
        await RunAsync("Enregistrement des réglages locaux…", () =>
        {
            var next = ReadForm(); next.Save(); ApplyConnection(next); ReloadConnections();
            saved = true;
            return Task.CompletedTask;
        });
        if (saved)
        {
            suppressNavigationLoad = true; NavOverview.IsChecked = true; suppressNavigationLoad = false;
            await RefreshAsync();
        }
    }

    internal void ApplyConnection(ConnectionSettings next)
    {
        settings = next.Snapshot();
        EndpointText.Text = $"{settings.User}@{settings.Host}";
        ResetConnectionData(); RefreshTrustChip();
    }

    private void ReloadConnections()
    {
        SavedConnections.ItemsSource = demo || isolatedSettings ? new[] { settings } : ConnectionSettings.LoadProfiles();
        SavedConnections.SelectedItem = ((IEnumerable<ConnectionSettings>)SavedConnections.ItemsSource).FirstOrDefault(p => p == settings);
    }

    private void SavedConnection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SavedConnections.SelectedItem is not ConnectionSettings selected || pages is null) return;
        HostBox.Text = selected.Host; UserBox.Text = selected.User;
        PortBox.Text = selected.Port.ToString(); KeyBox.Text = selected.KeyPath;
        ApplyConnection(selected);
    }

    private async void CloseRoom_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RoomItem room) return;
        await CloseRoomAsync(room);
    }

    internal Task CloseRoomAsync(RoomItem room) => RunAsync("Fermeture du salon…", async () =>
        {
            if (room.Origin != ActiveSettings) throw new InvalidOperationException("La connexion a changé. Actualisez la liste des salons.");
            if (!await ConfirmAsync("Fermer ce salon ?", $"{room.Name} : les {room.Count} joueur(s) quitteront le salon. Le salon sera supprimé.", "Fermer le salon", danger: true, requiredText: room.Name) || closed) return;
            await transport.SendAsync(ActiveSettings, "close-room", room.Id);
            RenderStatus(await transport.SendAsync(ActiveSettings, "status"));
            Toast("Salon fermé.");
        });

    private async void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (demo) { Toast("Aucune empreinte réelle n’est chargée en démonstration.", ToastKind.Info); return; }
        await RunAsync("Lecture de l’empreinte publique du serveur…", async () =>
        {
            var form = ReadForm().Snapshot();
            var scan = await SshTransport.ScanFingerprintAsync(form);
            if (!await ConfirmAsync("Vérifier l’identité SSH", $"Serveur {form.Host}:{form.Port}. Comparez cette empreinte à celle de la console de l’hébergeur : sa lecture sur le réseau ne prouve pas l’identité du serveur. Confirmez seulement si les deux correspondent.",
                "J’ai comparé, faire confiance", danger: false, code: scan.Fingerprint) || closed) return;
            if (ReadForm() != form) throw new InvalidOperationException("Les réglages ont changé. Vérifiez à nouveau l’empreinte de cette connexion.");
            KnownHostsTrust.SaveTrustedKey(form, scan.KeyLine);
            RefreshTrustChip();
            Toast("Empreinte enregistrée sur ce PC. Vous pouvez enregistrer la connexion.");
        });
    }
}

internal sealed class BackupItem(string name, long size, string modifiedUtc, ConnectionSettings? origin = null)
{
    public ConnectionSettings? Origin { get; } = origin;
    public string Name { get; } = name;
    public long Size { get; } = size;
    public bool Latest { get; set; }
    public Visibility LatestVisibility => Latest ? Visibility.Visible : Visibility.Collapsed;
    public string SizeText => $"{Size / 1048576d:0.0} Mo";
    DateTimeOffset? At => DateTimeOffset.TryParse(modifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToLocalTime() : null;
    public string When => At?.ToString("dddd d MMMM · HH:mm", CultureInfo.GetCultureInfo("fr-FR")) ?? Name;
    public string Age
    {
        get
        {
            if (At is not { } at) return "—";
            var age = DateTimeOffset.Now - at;
            return age.TotalMinutes < 60 ? $"il y a {Math.Max(1, (int)age.TotalMinutes)} min" : age.TotalHours < 48 ? $"il y a {(int)age.TotalHours} h" : $"il y a {(int)age.TotalDays} j";
        }
    }
}

internal sealed record PlayerItem(string Name, string Room, Brush Avatar)
{
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
}

internal sealed record RoomItem(string Name, bool Private, int Count, int Capacity, string Members)
{
    public string Id { get; init; } = "";
    public ConnectionSettings? Origin { get; init; }
    public string Visibility => Private ? "Privé" : "Public";
    public Brush BadgeBackground => Motion.Hex(Private ? "#2A2550" : "#163A35");
    public Brush BadgeForeground => Motion.Hex(Private ? "#B3A8FF" : "#3DDC97");
    public string Occupancy => Capacity > 0 ? $"{Count} / {Capacity} joueurs" + (Count >= Capacity ? " · complet" : "") : $"{Count} joueurs · capacité inconnue";
    public GridLength FillStar => new(Math.Clamp(Count, 0, Math.Max(1, Capacity)), GridUnitType.Star);
    public GridLength RestStar => new(Math.Max(0, Capacity - Count), GridUnitType.Star);
}

internal enum LogLevel { Info, Warn, Error }

internal sealed record LogLine(string Time, LogLevel Severity, string Message)
{
    public string Level => Severity switch { LogLevel.Error => "ERR", LogLevel.Warn => "WARN", _ => "INFO" };
    public Brush LevelBackground => Motion.Hex(Severity switch { LogLevel.Error => "#3A1A2A", LogLevel.Warn => "#3A2E18", _ => "#152645" });
    public Brush LevelForeground => Motion.Hex(Severity switch { LogLevel.Error => "#FF5C7A", LogLevel.Warn => "#FFB547", _ => "#5EE6FF" });
    public Brush MessageForeground => Motion.Hex(Severity switch { LogLevel.Error => "#FFB3C1", LogLevel.Warn => "#FFE0A8", _ => "#C9D2F2" });
}

internal sealed record DiagnosticRow(string Key, string Value, Brush Tone);
internal sealed record DiagnosticSection(string Title, string Icon, List<DiagnosticRow> Rows);
