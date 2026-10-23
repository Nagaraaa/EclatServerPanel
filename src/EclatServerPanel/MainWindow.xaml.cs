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
    private ConnectionSettings settings;
    private bool busy;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(RefreshSeconds) };
    private string currentPage = "Overview";
    private readonly Dictionary<string, FrameworkElement> pages;
    private readonly Dictionary<string, RadioButton> navigation;
    private readonly Dictionary<string, (string Title, string Subtitle)> headings = new()
    {
        ["Overview"] = ("Vue d’ensemble", "État du jeu, activité et ressources du VPS en direct."),
        ["Sessions"] = ("Joueurs et salons", "Qui joue, et dans quel salon, d’après le serveur."),
        ["Logs"] = ("Journaux", "Dernières lignes du service, valeurs sensibles masquées."),
        ["Backups"] = ("Sauvegardes", "Instantanés cohérents de la base SQLite."),
        ["Diagnostics"] = ("Diagnostic", "Service, machine, réseau et journal en un coup d’œil."),
        ["Connection"] = ("Connexion", "Accès SSH au VPS, enregistré uniquement sur ce PC."),
    };
    private readonly Queue<double> playersHistory = new(), roomsHistory = new(), latencyHistory = new(), loadHistory = new();
    private List<LogLine> logLines = [];
    private DateTime? lastCheck;
    private TaskCompletionSource<bool>? dialog;
    private string? dialogRequired;

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
        ModeBadge.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        RefreshTrustChip();
        UpdateServiceButtons(null);
        LogSearch.TextChanged += (_, _) => LogSearchHint.Visibility = LogSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StateChanged += (_, _) => { Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; };
        Loaded += async (_, _) =>
        {
            navigation[string.IsNullOrEmpty(settings.Host) ? "Connection" : "Overview"].IsChecked = true;
            if (demo) Toast("Mode démonstration : informations fictives, aucune connexion au VPS.", ToastKind.Info);
            if (demo) await SeedDemoHistoryAsync();
            if (!string.IsNullOrEmpty(settings.Host)) await RefreshAsync();
            timer.Start();
            RestartRefreshRing();
        };
        timer.Tick += async (_, _) => { RestartRefreshRing(); if (!busy && currentPage is "Overview" or "Sessions" && !string.IsNullOrEmpty(settings.Host)) await RefreshAsync(); };
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => UpdateCheckAge();
        clock.Start();
        Closed += (_, _) => { timer.Stop(); clock.Stop(); };
    }

    // The demonstration starts with a few minutes of history, like a panel left open.
    private async Task SeedDemoHistoryAsync()
    {
        for (var i = 0; i < 14; i++)
        {
            var data = await transport.SendAsync(settings, "status");
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
        if (page == "Logs") await LoadLogsAsync();
        else if (page == "Backups") await LoadBackupsAsync();
        else if (page == "Diagnostics" && DiagnosticSections.Items.Count == 0) await LoadDiagnosticsAsync();
    }

    private void ShowPage(string name)
    {
        currentPage = name;
        foreach (var page in pages) page.Value.Visibility = page.Key == name ? Visibility.Visible : Visibility.Collapsed;
        Motion.Enter(pages[name]);
        (PageTitle.Text, PageSubtitle.Text) = headings[name];
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
        while (busy) await Task.Delay(50);
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
        if (busy) return;
        busy = true;
        ActivityText.Text = activity;
        SetBusy(true);
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Toast(ex.Message, ToastKind.Error);
            ActivityText.Text = "Action interrompue";
        }
        finally { busy = false; SetBusy(false); if (ActivityText.Text == activity) ActivityText.Text = "Prêt"; }
    }

    private void SetBusy(bool on)
    {
        Cursor = on ? System.Windows.Input.Cursors.AppStarting : null;
        BusyBar.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        RefreshButton.IsEnabled = !on;
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
        var age = (int)(DateTime.Now - lastCheck.Value).TotalSeconds;
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
        dialog = new TaskCompletionSource<bool>();
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
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => DialogLayer.Visibility = Visibility.Collapsed;
        DialogLayer.BeginAnimation(OpacityProperty, null);
        DialogCard.BeginAnimation(OpacityProperty, fade);
        DialogDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        dialog?.TrySetResult(result);
    }

    private void DialogInput_Changed(object sender, TextChangedEventArgs e) => DialogConfirm.IsEnabled = dialogRequired is null || DialogInput.Text == dialogRequired;
    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
    private void DialogConfirm_Click(object sender, RoutedEventArgs e) => CloseDialog(true);

    // ---- Status --------------------------------------------------------
    public async Task RefreshAsync() => await RunAsync("Vérification du service et du protocole…", async () =>
    {
        try { RenderStatus(await transport.SendAsync(settings, "status")); }
        catch
        {
            SetState(StateTone.Unknown, "État non confirmé", "Le dernier contrôle a échoué. Les valeurs affichées peuvent être périmées.");
            UpdatedText.Text = "Dernier contrôle en échec";
            PlayersValue.Show(null); RoomsValue.Show(null); LatencyValue.Show(null);
            ShowPeople([], [], "Activité non confirmée");
            throw;
        }
    });

    private enum StateTone { Online, Degraded, Offline, Unknown }

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
        bool active = Text(Get(service, "active")) is "active" or "True" or "true";
        bool responds = Get(protocol, "ok").ValueKind == JsonValueKind.True;
        if (responds) SetState(StateTone.Online, "En ligne", "Le service tourne et le jeu répond aux joueurs depuis le VPS.");
        else if (active) SetState(StateTone.Degraded, "Service actif, jeu indisponible", "Le processus tourne mais le protocole du jeu ne répond pas. Consultez les journaux avant de redémarrer.");
        else SetState(StateTone.Offline, "Service arrêté", "Le service est arrêté ou en échec. Le diagnostic précise son état.");
        UpdateServiceButtons(active);

        double? latency = responds && Get(protocol, "latencyMs").ValueKind == JsonValueKind.Number ? Get(protocol, "latencyMs").GetDouble() : null;
        string publication = Fr(Text(Get(network, "publication")));
        string tunnel = Fr(Text(Get(network, "tunnel")));
        bool networkStale = DateTimeOffset.TryParse(Text(Get(network, "updatedAt")), out var networkAt) && DateTimeOffset.UtcNow - networkAt > TimeSpan.FromMinutes(1);
        StatusChips.Children.Clear();
        Chip("\uE7F4", "Service", $"{Text(Get(service, "active"))} · {Text(Get(service, "sub"))}", active);
        Chip("\uEC4A", "Protocole", responds ? $"{latency:0.0} ms" : "sans réponse", responds);
        Chip("\uE774", "Annuaire Coop", publication + (networkStale ? " (ancien)" : ""), publication == "disponible" && !networkStale);
        Chip("\uE968", "Tunnel", tunnel + (networkStale ? " (ancien)" : ""), tunnel == "connecté" && !networkStale);

        var players = Get(monitor, "players");
        var rooms = Get(monitor, "rooms");
        bool fresh = active && responds && !(DateTimeOffset.TryParse(Text(Get(monitor, "updatedAt")), out var monitorAt) && DateTimeOffset.UtcNow - monitorAt > TimeSpan.FromMinutes(1));
        var playerItems = fresh ? PlayerItems(players, rooms) : [];
        var roomItems = fresh ? RoomItems(rooms, players) : [];
        PlayersValue.Show(fresh ? playerItems.Count : null);
        RoomsValue.Show(fresh ? roomItems.Count : null);
        LatencyValue.Show(latency);
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
        long rss = Number(Get(Get(monitor, "memory"), "rssBytes"));
        if (total > 0) { var used = total - available; MemoryGauge.Stroke = Tone((double)used / total); MemoryGauge.Animate((double)used / total); MemoryPercent.Text = $"{100d * used / total:0} %"; MemoryDetail.Text = $"{Gb(used)} / {Gb(total)}"; }
        else { MemoryGauge.Animate(0); MemoryPercent.Text = "—"; MemoryDetail.Text = "Mesure indisponible"; }
        if (diskTotal > 0) { var used = diskTotal - diskFree; DiskGauge.Stroke = Tone((double)used / diskTotal); DiskGauge.Animate((double)used / diskTotal); DiskPercent.Text = $"{100d * used / diskTotal:0} %"; DiskDetail.Text = $"{Gb(diskFree)} libres"; }
        else { DiskGauge.Animate(0); DiskPercent.Text = "—"; DiskDetail.Text = "Mesure indisponible"; }
        if (rss > 0 && total > 0)
        {
            var share = (double)rss / Math.Max(1, total - available);
            ProcessGauge.Stroke = (Brush)FindResource("Crystal"); ProcessGauge.Animate(share);
            ProcessValue.Text = (rss / 1048576d).ToString("0", French); ProcessDetail.Text = $"{share * 100:0} % de la mémoire utilisée";
        }
        else { ProcessGauge.Animate(0); ProcessValue.Text = "—"; ProcessDetail.Text = "Processus non mesuré"; }
        var load = Loads(Get(metrics, "load"));
        LoadText.Text = load.Count > 0 ? string.Join("  ·  ", load.Select(v => v.ToString("0.00", French))) : "—";
        if (load.Count > 0) Push(loadHistory, load[0]);
        LoadSpark.Show(loadHistory.ToList());

        var counters = Get(monitor, "counters");
        CounterValue(CounterConnections, Get(counters, "connections"));
        CounterValue(CounterDisconnections, Get(counters, "disconnections"));
        CounterValue(CounterRooms, Get(counters, "roomsCreated"));
        CounterValue(CounterJoins, Get(counters, "roomJoins"));
        CounterValue(CounterErrors, Get(counters, "errors"));
        var lastError = Get(monitor, "lastError");
        LastErrorText.Text = lastError.ValueKind == JsonValueKind.Object
            ? Text(Get(lastError, "code")) + (DateTimeOffset.TryParse(Text(Get(lastError, "at")), out var errorAt) ? " · " + errorAt.ToLocalTime().ToString("dd/MM HH:mm", French) : "")
            : "Aucun";
        LastErrorText.Foreground = (Brush)FindResource(lastError.ValueKind == JsonValueKind.Object ? "Warning" : "Success");

        SessionsText.Text = monitor.ValueKind == JsonValueKind.Undefined ? "Aucune donnée d’activité reçue." : Pretty(monitor);
        lastCheck = DateTime.Now;
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
        StartButton.Visibility = active == true ? Visibility.Collapsed : Visibility.Visible;
        RestartButton.Visibility = active == false ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = active == false ? Visibility.Collapsed : Visibility.Visible;
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
    private static void CounterValue(CountUpText target, JsonElement value) => target.Show(value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
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

    private static List<RoomItem> RoomItems(JsonElement rooms, JsonElement players)
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
            var capacity = Get(room, "capacity").ValueKind == JsonValueKind.Number ? Get(room, "capacity").GetInt32() : Math.Max(2, count);
            var isPrivate = First(room, "visibility") == "private";
            result.Add(new(name, isPrivate, count, capacity, names.Count > 0 ? string.Join(", ", names) : "Membres non communiqués"));
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
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (currentPage == "Logs") await LoadLogsAsync();
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
    {
        if (busy) return;
        var action = (string)((Button)sender).Tag;
        if (action is "stop" or "restart" && !await ConfirmAsync(action == "stop" ? "Arrêter le serveur ?" : "Redémarrer le serveur ?",
            "Les parties en cours seront interrompues. Les joueurs devront se reconnecter.", action == "stop" ? "Arrêter" : "Redémarrer", danger: true)) return;
        bool done = false;
        await RunAsync(action == "start" ? "Démarrage du service…" : action == "stop" ? "Arrêt du service…" : "Redémarrage du service…", async () =>
        {
            await transport.SendAsync(settings, action);
            Toast(action == "start" ? "Service démarré." : action == "stop" ? "Service arrêté." : "Service redémarré.");
            done = true;
        });
        if (done) { await Task.Delay(1100); await RefreshAsync(); }
    }

    // ---- Logs ----------------------------------------------------------
    private static readonly Regex JournalLine = new(@"^(?<time>\d{4}-\d\d-\d\dT[\d:]+(?:[+-]\d{4}|Z)?)\s+\S+\s+(?<unit>[^:\s]+):\s?(?<message>.*)$");
    private static readonly Regex ErrorWords = new(@"\b(error|erreur|fail(ed|ure)?|échec|exception|fatal|crash)\b", RegexOptions.IgnoreCase);
    private static readonly Regex WarnWords = new(@"\b(warn(ing)?|avertissement|indisponible|timeout|retry)\b", RegexOptions.IgnoreCase);

    private async Task LoadLogsAsync() => await RunAsync("Lecture des journaux…", async () =>
    {
        var data = await transport.SendAsync(settings, "logs");
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
            time = DateTimeOffset.TryParseExact(match.Groups["time"].Value, ["yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:sszz", "yyyy-MM-ddTHH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
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
        var data = await transport.SendAsync(settings, "diagnostics");
        DiagnosticsText.Text = Pretty(data);
        DiagnosticSections.ItemsSource = DiagnosticCards(data);
        DiagnosticsEmpty.Visibility = Visibility.Collapsed;
        Motion.Enter(DiagnosticSections);
    });

    private List<DiagnosticSection> DiagnosticCards(JsonElement data)
    {
        var status = Get(data, "status").ValueKind == JsonValueKind.Object ? Get(data, "status") : data;
        var service = Get(status, "service"); var protocol = Get(status, "protocol"); var network = Get(status, "network");
        var metrics = Get(status, "metrics"); var host = Get(status, "host"); var monitor = Get(status, "monitor");
        Brush good = (Brush)FindResource("Success"), bad = (Brush)FindResource("Warning"), plain = (Brush)FindResource("Text");
        bool active = Text(Get(service, "active")) == "active", responds = Get(protocol, "ok").ValueKind == JsonValueKind.True;
        long total = Number(Get(metrics, "memoryTotalBytes")), available = Number(Get(metrics, "memoryAvailableBytes"));
        long diskTotal = Number(Get(metrics, "diskTotalBytes")), diskFree = Number(Get(metrics, "diskFreeBytes"));
        var sections = new List<DiagnosticSection>
        {
            new("Service systemd", "\uE7F4", [new("État", Text(Get(service, "active")), active ? good : bad), new("Sous-état", Text(Get(service, "sub")), plain), new("PID", Text(Get(service, "pid")), plain)]),
            new("Protocole du jeu", "\uEC4A", [new("Réponse", responds ? "oui" : "non", responds ? good : bad), new("Latence", responds ? Text(Get(protocol, "latencyMs")) + " ms" : "—", plain), new("Erreur", Text(Get(protocol, "error")) is var err && err != "—" ? err : "aucune", err != "—" ? bad : plain)]),
            new("Machine", "\uE977", [new("Nom", Text(Get(host, "name")), plain), new("Démarrée depuis", Number(Get(host, "uptimeSeconds")) is var up and > 0 ? Duration(up) : "—", plain), new("Node.js", Text(Get(host, "nodeVersion")), plain)]),
            new("Ressources", "\uE9F5", [new("Mémoire", total > 0 ? $"{Gb(total - available)} / {Gb(total)}" : "—", plain), new("Disque libre", diskTotal > 0 ? $"{Gb(diskFree)} / {Gb(diskTotal)}" : "—", diskTotal > 0 && diskFree < diskTotal / 10 ? bad : plain), new("Charge", Loads(Get(metrics, "load")) is { Count: > 0 } l ? string.Join(" · ", l.Select(v => v.ToString("0.00", French))) : "—", plain)]),
            new("Réseau Coop", "\uE968", [new("Annuaire", Fr(Text(Get(network, "publication"))), Text(Get(network, "publication")) == "available" ? good : bad), new("Tunnel", Fr(Text(Get(network, "tunnel"))), Text(Get(network, "tunnel")) == "connected" ? good : bad), new("Mis à jour", DateTimeOffset.TryParse(Text(Get(network, "updatedAt")), out var at) ? at.ToLocalTime().ToString("HH:mm:ss") : "—", plain)]),
            new("Activité", "\uE716", [new("Joueurs", Get(monitor, "players").ValueKind == JsonValueKind.Array ? Get(monitor, "players").GetArrayLength().ToString() : "—", plain), new("Salons", Get(monitor, "rooms").ValueKind == JsonValueKind.Array ? Get(monitor, "rooms").GetArrayLength().ToString() : "—", plain), new("Sauvegardes", Text(Get(data, "backupCount")), plain)]),
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
    private async Task LoadBackupsAsync() => await RunAsync("Lecture des sauvegardes…", LoadBackupsCoreAsync);

    private async Task LoadBackupsCoreAsync()
    {
        var data = await transport.SendAsync(settings, "backups");
        var items = Get(data, "items");
        var backups = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Select(item => new BackupItem(Text(Get(item, "name")), Number(Get(item, "size")), Text(Get(item, "modifiedUtc")))).ToList() : [];
        for (var i = 0; i < backups.Count; i++) backups[i].Latest = i == 0;
        BackupRows.ItemsSource = backups;
        Motion.Enter(BackupRows);
        BackupsEmpty.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupCount.Show(backups.Count);
        BackupSize.Text = backups.Count == 0 ? "—" : $"{backups.Sum(b => b.Size) / 1048576d:0.0} Mo";
        BackupLatest.Text = backups.Count == 0 ? "Jamais" : backups[0].Age;
    }

    private async void Backups_Click(object sender, RoutedEventArgs e) => await LoadBackupsAsync();

    private async void CreateBackup_Click(object sender, RoutedEventArgs e) => await RunAsync("Création d’un instantané cohérent…", async () =>
    {
        var data = await transport.SendAsync(settings, "backup");
        Toast("Sauvegarde créée : " + Text(Get(data, "name")));
        await LoadBackupsCoreAsync();
    });

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ((Button)sender).Tag is not BackupItem item) return;
        if (demo) { Toast("Le téléchargement est disponible après connexion à un serveur réel.", ToastKind.Info); return; }
        var save = new SaveFileDialog { Title = "Enregistrer une copie de la base", Filter = "Base SQLite (*.sqlite)|*.sqlite", FileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.Name)) + ".sqlite", AddExtension = true, DefaultExt = ".sqlite" };
        if (save.ShowDialog(this) != true) return;
        await RunAsync("Téléchargement et vérification SHA-256…", async () =>
        {
            var data = await transport.SendAsync(settings, "download", item.Name);
            var bytes = SshTransport.VerifyDownload(data);
            var temp = save.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, save.FileName, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Toast("Base enregistrée sur ce PC. Son empreinte SHA-256 correspond à celle du serveur.");
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ((Button)sender).Tag is not BackupItem item) return;
        if (!await ConfirmAsync("Restaurer cette sauvegarde ?", $"Instantané du {item.When}. Les données actuelles seront remplacées et les parties interrompues. Le serveur crée d’abord une copie de retour arrière.", "Restaurer", danger: true, code: item.Name, requiredText: "RESTAURER")) return;
        await RunAsync("Restauration en cours. Patientez avant toute autre action…", async () =>
        {
            var data = await transport.SendAsync(settings, "restore", item.Name);
            if (Get(data, "restored").ValueKind != JsonValueKind.True) throw new InvalidOperationException("La restauration n’a pas été confirmée par le serveur.");
            Toast("Restauration terminée. Contrôlez le service et les données avant de reprendre les parties.");
            await LoadBackupsCoreAsync();
        });
    }

    // ---- Connection ----------------------------------------------------
    private ConnectionSettings ReadForm()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) throw new InvalidOperationException("Le port SSH doit être un nombre.");
        return new ConnectionSettings { Host = HostBox.Text.Trim(), User = UserBox.Text.Trim(), Port = port, KeyPath = Environment.ExpandEnvironmentVariables(KeyBox.Text.Trim()) };
    }

    private void RefreshTrustChip()
    {
        bool trusted = demo || File.Exists(ConnectionSettings.KnownHostsPath);
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
            var next = ReadForm(); next.Save(); settings = next;
            EndpointText.Text = $"{settings.User}@{settings.Host}";
            saved = true;
            return Task.CompletedTask;
        });
        if (saved) { NavOverview.IsChecked = true; await RefreshAsync(); }
    }

    private async void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (demo) { Toast("Aucune empreinte réelle n’est chargée en démonstration.", ToastKind.Info); return; }
        ConnectionSettings? form = null;
        (string KeyLine, string Fingerprint)? scan = null;
        await RunAsync("Lecture de l’empreinte publique du serveur…", async () => { form = ReadForm(); scan = await SshTransport.ScanFingerprintAsync(form); });
        if (form is null || scan is null) return;
        if (!await ConfirmAsync("Vérifier l’identité SSH", $"Serveur {form.Host}:{form.Port}. Comparez cette empreinte à celle de la console de l’hébergeur : sa lecture sur le réseau ne prouve pas l’identité du serveur. Confirmez seulement si les deux correspondent.",
            "J’ai comparé, faire confiance", danger: false, code: scan.Value.Fingerprint)) return;
        Directory.CreateDirectory(ConnectionSettings.DirectoryPath);
        await File.AppendAllTextAsync(ConnectionSettings.KnownHostsPath, scan.Value.KeyLine);
        RefreshTrustChip();
        Toast("Empreinte enregistrée sur ce PC. Vous pouvez enregistrer la connexion.");
    }
}

internal sealed class BackupItem(string name, long size, string modifiedUtc)
{
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
    public string Visibility => Private ? "Privé" : "Public";
    public Brush BadgeBackground => Motion.Hex(Private ? "#2A2550" : "#163A35");
    public Brush BadgeForeground => Motion.Hex(Private ? "#B3A8FF" : "#3DDC97");
    public string Occupancy => $"{Count} / {Capacity} joueurs" + (Count >= Capacity ? " · complet" : "");
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
