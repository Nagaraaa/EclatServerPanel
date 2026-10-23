using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EclatServerPanel;

/// <summary>Offline WPF workflows. Every transport operation uses synthetic data.</summary>
internal static class PanelUiTests
{
    internal static async Task RunAsync()
    {
        Check(Application.Current is not null && Application.Current.Dispatcher.CheckAccess(), "UI tests require the WPF dispatcher.");
        await RawSwitchesAsync();
        await RoomClosureAsync();
        await MonitorAndNetworkAsync();
        await FailedRefreshAsync();
        await QueuedNavigationAsync();
        await ServiceControlsAsync();
        await ServiceTransitionAsync();
        await RestartFreshnessAsync();
        await RestoreConfirmationAsync();
        await ConnectionResetAsync();
    }

    private static async Task RawSwitchesAsync()
    {
        using var test = await TestWindow.CreateAsync();
        foreach (var prefix in new[] { "RawSessions", "RawDiagnostics" })
        {
            var toggle = Find<System.Windows.Controls.Primitives.ToggleButton>(test.Window, prefix + "Toggle");
            toggle.ApplyTemplate();
            for (var index = 0; index < 4; index++)
            {
                toggle.IsChecked = true;
                await Task.Delay(180);
                Check(Find<FrameworkElement>(test.Window, prefix + "Box").Visibility == Visibility.Visible, "Raw data must open.");
                toggle.IsChecked = false;
                await Task.Delay(180);
                Check(Find<FrameworkElement>(test.Window, prefix + "Box").Visibility == Visibility.Collapsed, "Raw data must close.");
            }
        }
        Check(test.Window.IsVisible, "Switches must not close the panel.");
    }

    private static async Task RoomClosureAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await test.Window.RefreshAsync();
        var rooms = Find<ItemsControl>(test.Window, "RoomCards");
        Check(rooms.Items.OfType<RoomItem>().All(room => room.Id.Length > 0 && room.Origin == test.Settings), "Room actions must retain the ID and originating connection.");
        var transport = new DemoTransport();
        await transport.SendAsync(test.Settings, "close-room", "r1");
        var status = await transport.SendAsync(test.Settings, "status");
        Check(!status.GetProperty("monitor").GetProperty("rooms").EnumerateArray().Any(room => room.GetProperty("id").GetString() == "r1"), "Closed room must disappear.");
        Check(!status.GetProperty("monitor").GetProperty("players").EnumerateArray().Any(player => player.GetProperty("roomId").GetString() == "r1"), "Closed room members must disconnect.");
        Check(status.GetProperty("monitor").GetProperty("rooms").GetArrayLength() == 2, "Other rooms must remain open.");
        try { SshTransport.ValidateRequest("close-room", "../../bad"); throw new Exception("Invalid ID accepted"); }
        catch (InvalidOperationException) { }
    }

    private static async Task MonitorAndNetworkAsync()
    {
        using var test = await TestWindow.CreateAsync();
        test.Transport.StatusTransform = status =>
        {
            status["monitor"] = Monitor(0);
            return status;
        };
        await Bounded(test.Window.RefreshAsync(), "fresh zero activity");
        Check(Text(test.Window, "PlayersValue") == "0", "A valid zero-player sample must display zero, not unknown.");
        Check(Text(test.Window, "RoomsValue") == "0", "A valid zero-room sample must display zero, not unknown.");
        Check(Text(test.Window, "ProcessValue") == "64", "Fresh process RSS must use the monitor's bytes.");

        var invalid = new (string Name, Action<JsonObject> Apply)[]
        {
            ("absent", status => status.Remove("monitor")),
            ("bad arrays", status => { var monitor = Monitor(0); monitor["players"] = "invalid"; status["monitor"] = monitor; }),
            ("bad timestamp", status => { var monitor = Monitor(0); monitor["updatedAt"] = "not a timestamp"; status["monitor"] = monitor; }),
            ("stopped monitor", status => { var monitor = Monitor(0); monitor["status"] = "stopped"; status["monitor"] = monitor; }),
            ("different process", status => { var monitor = Monitor(0); monitor["processCurrent"] = false; status["monitor"] = monitor; }),
            ("stale", status => { var monitor = Monitor(0); monitor["updatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"); status["monitor"] = monitor; }),
            ("future", status => { var monitor = Monitor(0); monitor["updatedAt"] = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"); status["monitor"] = monitor; })
        };
        foreach (var sample in invalid)
        {
            test.Transport.StatusTransform = status => { sample.Apply(status); return status; };
            await Bounded(test.Window.RefreshAsync(), sample.Name + " monitor");
            Check(Text(test.Window, "PlayersValue") == "—", sample.Name + " monitor must not invent a player count.");
            Check(Text(test.Window, "RoomsValue") == "—", sample.Name + " monitor must not invent a room count.");
            Check(Text(test.Window, "ProcessValue") == "—", sample.Name + " monitor must not present cached RSS as current.");
            Check(Find<ItemsControl>(test.Window, "PlayerList").Items.Count == 0, sample.Name + " monitor must clear the visible player list.");
        }

        test.Transport.StatusTransform = status =>
        {
            status["monitor"] = Monitor(10);
            status["network"] = new JsonObject { ["publication"] = "available", ["tunnel"] = "connected", ["updatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O") };
            return status;
        };
        await Bounded(test.Window.RefreshAsync(), "ten players and stale network");
        await UntilAsync(() => Text(test.Window, "PlayersValue") == "10", "ten-player counter animation");
        Check(Find<ItemsControl>(test.Window, "PlayerList").Items.Count == 10, "The sessions list must retain all ten players.");
        var room = Find<ItemsControl>(test.Window, "RoomCards").Items.OfType<RoomItem>().Single();
        Check(room.Count == 10 && room.Capacity == 10 && room.Occupancy.Contains("10 / 10"), "Room occupancy must use the actual ten-player capacity.");
        CheckStaleChip(test.Window, "Annuaire Coop");
        CheckStaleChip(test.Window, "Tunnel");
    }

    private static async Task FailedRefreshAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await Bounded(test.Window.RefreshAsync(), "initial successful refresh");
        Find<RadioButton>(test.Window, "NavDiagnostics").IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), "initial diagnostic cards");
        Check(Find<ItemsControl>(test.Window, "DiagnosticSections").Items.Count > 0, "Diagnostic cards must exist before testing their invalidation.");
        test.Transport.Failures["status"] = new InvalidOperationException("Synthetic unavailable status");
        await Bounded(test.Window.RefreshAsync(), "failed refresh");
        Check(Text(test.Window, "UpdatedText") == "Dernier contrôle en échec", "A failed refresh must remain identified as a failure.");
        await Task.Delay(1250); // Let the actual one-second UI clock tick.
        Check(Text(test.Window, "UpdatedText") == "Dernier contrôle en échec", "The clock must not relabel a failed refresh as a successful recent check.");
        Check(Text(test.Window, "ServerStatus") == "État non confirmé", "Failed SSH status must leave the server state unknown.");
        Check(Text(test.Window, "PlayersValue") == "—", "Failed status must clear the last player count.");
        Check(Find<ItemsControl>(test.Window, "DiagnosticSections").Items.Count == 0, "Failed status must also clear stale green diagnostic cards.");
    }

    private static async Task QueuedNavigationAsync()
    {
        using var test = await TestWindow.CreateAsync();
        test.Transport.BlockNextStatus = true;
        var refresh = test.Window.RefreshAsync();
        await Bounded(test.Transport.StatusEntered.Task, "blocked status entered");
        Find<RadioButton>(test.Window, "NavLogs").IsChecked = true;
        await Task.Delay(40);
        Check(test.Transport.Count("logs") == 0, "Navigation must queue behind the current request.");
        test.Transport.StatusRelease.TrySetResult(true);
        await Bounded(refresh, "released status");
        await Bounded(test.Window.WaitForIdleAsync(), "queued log request");
        Check(test.Transport.Count("status") == 1 && test.Transport.Count("logs") == 1, "A logs navigation during refresh must execute once, not be dropped or duplicated.");
        Check(Find<ListBox>(test.Window, "LogLines").Items.Count > 0, "Queued navigation must populate the real log view.");
        Check(Find<ListBox>(test.Window, "LogLines").Items.OfType<LogLine>().Any(line => line.Time.Length == 8 && !line.Message.Contains("eclat-vps node[")), "ISO log timestamps with a colon in the timezone must fill the time column and strip the journal prefix.");
    }

    private static async Task ServiceControlsAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await Bounded(test.Window.RefreshAsync(), "service initial state");
        var cancelled = test.Window.ControlServiceAsync("stop");
        await DialogAsync(test.Window, "stop cancellation");
        Click(test.Window, "DialogCancel");
        await Bounded(cancelled, "cancelled stop");
        Check(test.Transport.Count("stop") == 0, "Cancelling stop must send no stop command.");
        await DialogClosedAsync(test.Window);

        int beforeStatus = test.Transport.Count("status");
        var stopped = test.Window.ControlServiceAsync("stop");
        await DialogAsync(test.Window, "stop confirmation");
        int beforeDialogRequests = test.Transport.Calls.Count;
        var queuedRefresh = test.Window.RefreshAsync();
        await Task.Delay(40);
        Check(test.Transport.Calls.Count == beforeDialogRequests, "A refresh queued during confirmation must not contact SSH before the decision.");
        Check(test.Transport.Count("stop") == 0, "A stop must remain unsent while its confirmation is open.");
        Click(test.Window, "DialogConfirm");
        await Bounded(Task.WhenAll(stopped, queuedRefresh), "confirmed stop and queued refresh");
        Check(test.Transport.Count("stop") == 1, "A confirmed stop must be sent exactly once.");
        Check(test.Transport.Count("status") == beforeStatus + 2, "Stop must perform its post-action check followed by the queued refresh exactly once each.");
        Check(Text(test.Window, "ServerStatus") == "Service arrêté", "The displayed result must follow the post-stop status.");
        Check(Text(test.Window, "ActivityText") == "Prêt", "A completed service action and queued refresh must return to Ready.");
        await DialogClosedAsync(test.Window);

        test.Transport.Failures["restart"] = new TimeoutException("Synthetic command timeout");
        int callsBefore = test.Transport.Calls.Count;
        var timedOut = test.Window.ControlServiceAsync("restart");
        await DialogAsync(test.Window, "restart timeout confirmation");
        Click(test.Window, "DialogConfirm");
        await Bounded(timedOut, "timed-out restart with status recheck");
        Check(test.Transport.Count("restart") == 1, "A timed-out mutation must never be retried automatically.");
        Check(test.Transport.Calls.Skip(callsBefore).Select(call => call.Action).SequenceEqual(new[] { "restart", "status" }), "A command timeout must trigger exactly one status recheck.");
        Check(Text(test.Window, "ServerStatus") == "Service arrêté", "The post-timeout view must use confirmed state, not pretend that restart succeeded.");
    }

    private static async Task ServiceTransitionAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await Bounded(test.Window.RefreshAsync(), "initial state before slow stop");
        test.Transport.StatusTransform = status =>
        {
            status["service"]!.AsObject()["active"] = "deactivating";
            status["service"]!.AsObject()["sub"] = "stop-sigterm";
            status["protocol"]!.AsObject()["ok"] = false;
            status["protocol"]!.AsObject()["latencyMs"] = null;
            return status;
        };
        var stop = test.Window.ControlServiceAsync("stop");
        await DialogAsync(test.Window, "slow stop confirmation");
        Click(test.Window, "DialogConfirm");
        await Bounded(stop, "stop still deactivating");
        Check(test.Transport.Count("stop") == 1, "A slow stop must remain a single request.");
        Check(Text(test.Window, "ServerStatus") == "Service en transition", "Deactivating must not be presented as Service stopped.");
        Check(Find<Button>(test.Window, "StartButton").Visibility == Visibility.Collapsed, "Start must stay hidden while stop is still in progress.");
        Check(Text(test.Window, "ActivityText") == "Prêt", "An acknowledged action with a transitional service must finish the UI operation.");
    }

    private static async Task RestartFreshnessAsync()
    {
        using var test = await TestWindow.CreateAsync();
        long gamePid = 1111;
        test.Transport.StatusTransform = status =>
        {
            var monitor = Monitor(10);
            monitor["pid"] = gamePid;
            monitor["processCurrent"] = true;
            status["monitor"] = monitor;
            // The launcher and actual Node game are separate processes.
            status["service"]!.AsObject()["pid"] = 9999;
            return status;
        };
        await Bounded(test.Window.RefreshAsync(), "initial game process sample");
        await UntilAsync(() => Text(test.Window, "PlayersValue") == "10", "initial ten-player process sample");
        Check(Text(test.Window, "ProcessValue") == "64", "A fresh game process may differ from systemd's launcher PID.");

        var restart = test.Window.ControlServiceAsync("restart");
        await DialogAsync(test.Window, "restart game process confirmation");
        Click(test.Window, "DialogConfirm");
        await Bounded(restart, "restart with old monitor process");
        Check(test.Transport.Count("restart") == 1, "Restart must be requested only once.");
        Check(Text(test.Window, "PlayersValue") == "—", "The old game PID must not prove current activity after restart.");
        Check(Text(test.Window, "ProcessValue") == "—", "Old game-process RSS must be hidden after restart.");
        Check(Find<ItemsControl>(test.Window, "PlayerList").Items.Count == 0, "Old game-process players must be cleared after restart.");

        await Bounded(test.Window.RefreshAsync(), "old game process still present");
        Check(Text(test.Window, "PlayersValue") == "—", "Fresh timestamps cannot revalidate the excluded pre-restart game PID.");
        gamePid = 1112;
        await Bounded(test.Window.RefreshAsync(), "new game process sample");
        await UntilAsync(() => Text(test.Window, "PlayersValue") == "10", "new process activity confirmed");
        Check(Text(test.Window, "ProcessValue") == "64", "The new game PID must re-enable its actual RSS sample.");
        Check(Find<ItemsControl>(test.Window, "PlayerList").Items.Count == 10, "A fresh new Node process must restore all ten visible players.");
        Check(Text(test.Window, "ActivityText") == "Prêt", "Successful post-restart checks must finish Ready.");
    }

    private static async Task RestoreConfirmationAsync()
    {
        using var test = await TestWindow.CreateAsync();
        var item = await test.Transport.SnapshotAsync(test.Settings);
        var cancelled = test.Window.RestoreBackupAsync(item);
        await DialogAsync(test.Window, "restore cancellation");
        var input = Find<TextBox>(test.Window, "DialogInput");
        foreach (string wrong in new[] { "restaurer", " RESTAURER", "RESTAURER " })
        {
            input.Text = wrong;
            Check(!Find<Button>(test.Window, "DialogConfirm").IsEnabled, "Restoration must require the exact confirmation text.");
            Click(test.Window, "DialogConfirm"); // Exercise the handler guard, not only disabled styling.
            await Task.Delay(20);
            Check(!cancelled.IsCompleted && test.Transport.Count("restore") == 0, "Wrong confirmation text must not restore any data.");
        }
        Click(test.Window, "DialogCancel");
        await Bounded(cancelled, "cancelled restoration");
        Check(test.Transport.Count("restore") == 0, "Cancelling restoration must make no remote mutation.");
        await DialogClosedAsync(test.Window);

        var restored = test.Window.RestoreBackupAsync(item);
        await DialogAsync(test.Window, "typed restoration confirmation");
        Find<TextBox>(test.Window, "DialogInput").Text = "RESTAURER";
        Check(Find<Button>(test.Window, "DialogConfirm").IsEnabled, "Exact RESTAURER must enable confirmation.");
        Click(test.Window, "DialogConfirm");
        await Bounded(restored, "confirmed restoration");
        Check(test.Transport.Count("restore") == 1, "A confirmed restoration must be sent only once.");
        Check(test.Transport.Calls.Single(call => call.Action == "restore").Name == item.Name, "Restoration must use the selected snapshot's actual name.");
        Check(test.Transport.Count("backups") >= 1 && test.Transport.Count("status") >= 1, "Restoration must reload snapshots and recheck service state.");
        await DialogClosedAsync(test.Window);

        var wrongOrigin = new BackupItem(item.Name, item.Size, DateTimeOffset.UtcNow.ToString("O"), test.Settings with { Host = "other.invalid" });
        int calls = test.Transport.Calls.Count;
        await Bounded(test.Window.RestoreBackupAsync(wrongOrigin), "wrong-origin restoration rejection");
        Check(test.Transport.Calls.Count == calls, "An old host's snapshot must be rejected before any request.");
        Check(Find<Grid>(test.Window, "DialogLayer").Visibility == Visibility.Collapsed, "Wrong-origin snapshots must not offer restoration confirmation.");
        Check(Text(test.Window, "ActivityText") == "Action interrompue", "Wrong-origin refusal must remain visible as an interrupted action.");
    }

    private static async Task ConnectionResetAsync()
    {
        using var test = await TestWindow.CreateAsync();
        test.Transport.StatusTransform = status => { status["monitor"] = Monitor(10); return status; };
        await Bounded(test.Window.RefreshAsync(), "old host activity");
        foreach (string page in new[] { "Logs", "Backups", "Diagnostics", "Overview" })
        {
            Find<RadioButton>(test.Window, "Nav" + page).IsChecked = true;
            await Bounded(test.Window.WaitForIdleAsync(), "populate old host " + page);
        }
        Check(Find<ListBox>(test.Window, "LogLines").Items.Count > 0, "Old-host logs must be populated before the reset test.");
        Check(Find<ItemsControl>(test.Window, "BackupRows").Items.Count > 0, "Old-host backups must be populated before the reset test.");
        Check(Find<ItemsControl>(test.Window, "DiagnosticSections").Items.Count > 0, "Old-host diagnostics must be populated before the reset test.");
        await Task.Delay(200);
        Check(DrawingCount(Find<Sparkline>(test.Window, "PlayersSpark")) > 0, "The old-host chart must contain real rendered history before reset.");

        int callsBefore = test.Transport.Calls.Count;
        test.Window.ApplyConnection(test.Settings with { Host = "next.invalid" });
        await Task.Delay(150);
        Check(test.Transport.Calls.Count == callsBefore, "Changing settings must not silently contact an unverified new host.");
        Check(Text(test.Window, "EndpointText").Contains("next.invalid"), "The endpoint label must identify the newly selected host.");
        Check(Find<ListBox>(test.Window, "LogLines").Items.Count == 0, "Host changes must clear previous logs.");
        Check(Find<ItemsControl>(test.Window, "BackupRows").Items.Count == 0, "Host changes must clear previous snapshots.");
        Check(Find<ItemsControl>(test.Window, "DiagnosticSections").Items.Count == 0, "Host changes must clear previous diagnostics.");
        Check(Text(test.Window, "DiagnosticsText").Contains("Aucun diagnostic"), "Old raw diagnostic data must not survive a host change.");
        foreach (string name in new[] { "PlayersValue", "RoomsValue", "LatencyValue", "BackupCount", "CounterConnections", "CounterDisconnections", "CounterRooms", "CounterJoins", "CounterErrors" })
            Check(Text(test.Window, name) == "—", "Host changes must invalidate the old host's " + name + ".");
        foreach (string name in new[] { "PlayersSpark", "RoomsSpark", "LatencySpark", "LoadSpark" })
            Check(DrawingCount(Find<Sparkline>(test.Window, name)) == 0, "Host changes must clear rendered graph " + name + ".");
        Check(Find<ItemsControl>(test.Window, "PlayerList").Items.Count == 0, "Host changes must clear the previous player list.");
    }

    private static JsonObject Monitor(int players)
    {
        var members = new JsonArray();
        for (int index = 0; index < players; index++) members.Add(new JsonObject { ["id"] = "player-" + index, ["name"] = "Joueur" + index, ["roomId"] = "room-ten" });
        var rooms = new JsonArray();
        if (players > 0) rooms.Add(new JsonObject { ["id"] = "room-ten", ["name"] = "Salon dix", ["visibility"] = "public", ["count"] = players, ["capacity"] = 10 });
        return new JsonObject
        {
            ["status"] = "running", ["pid"] = 1111, ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["uptimeSeconds"] = 120,
            ["players"] = members, ["rooms"] = rooms, ["memory"] = new JsonObject { ["rssBytes"] = 64 * 1024 * 1024 },
            ["counters"] = new JsonObject { ["connections"] = 12, ["disconnections"] = 2, ["roomsCreated"] = players == 0 ? 0 : 1, ["roomJoins"] = 10, ["roomLeaves"] = 0, ["errors"] = 0 },
            ["lastError"] = null
        };
    }

    private static void CheckStaleChip(MainWindow window, string label)
    {
        var chips = Find<Panel>(window, "StatusChips");
        var rows = chips.Children.OfType<Border>().Select(border => border.Child).OfType<StackPanel>();
        var row = rows.Single(panel => panel.Children.OfType<TextBlock>().Any(text => text.Text.Trim() == label));
        Check(row.Children.OfType<TextBlock>().Any(text => text.Text.Contains("ancien")), label + " must mark a stale network declaration.");
        var icon = row.Children.OfType<TextBlock>().First();
        Check(icon.Foreground is SolidColorBrush brush && brush.Color == ((SolidColorBrush)window.FindResource("Warning")).Color, label + " must use the warning tone for stale data.");
    }

    private static int DrawingCount(FrameworkElement element)
    {
        element.UpdateLayout();
        return VisualTreeHelper.GetDrawing(element)?.Children.Count ?? 0;
    }

    private static async Task DialogAsync(MainWindow window, string operation)
        => await UntilAsync(() => Find<Grid>(window, "DialogLayer").Visibility == Visibility.Visible, operation + " dialog");

    private static async Task DialogClosedAsync(MainWindow window)
        => await UntilAsync(() => Find<Grid>(window, "DialogLayer").Visibility == Visibility.Collapsed, "confirmation dialog close");

    private static void Click(MainWindow window, string name)
        => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Text(MainWindow window, string name)
        => window.FindName(name) switch { TextBlock text => text.Text, TextBox box => box.Text, _ => throw new InvalidOperationException("UI test control is not textual: " + name) };

    private static T Find<T>(MainWindow window, string name) where T : class
        => window.FindName(name) as T ?? throw new InvalidOperationException("UI test control not found: " + name);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Panel UI test failed: " + message);
    }

    private static async Task UntilAsync(Func<bool> condition, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("Panel UI test timed out: " + operation);
            await Task.Delay(20);
        }
    }

    private static async Task Bounded(Task work, string operation)
    {
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(8))) != work) throw new TimeoutException("Panel UI test timed out: " + operation);
        await work;
    }

    private sealed class TestWindow : IDisposable
    {
        internal readonly FakeTransport Transport = new();
        internal readonly ConnectionSettings Settings = new() { Host = "tests.invalid", User = "eclat-panel", KeyPath = "fake" };
        internal MainWindow Window { get; }
        private TestWindow()
        {
            Window = new MainWindow(false, Transport, Settings, automaticRefresh: false)
            {
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000
            };
        }
        internal static async Task<TestWindow> CreateAsync()
        {
            var test = new TestWindow();
            test.Window.Show();
            await UntilAsync(() => test.Window.IsLoaded, "offscreen window load");
            await test.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            return test;
        }
        public void Dispose()
        {
            Transport.StatusRelease.TrySetResult(true);
            Window.Close();
        }
    }

    private sealed class FakeTransport : IPanelTransport
    {
        private readonly DemoTransport demo = new();
        internal readonly List<(string Action, string? Name)> Calls = [];
        internal readonly Dictionary<string, Exception> Failures = [];
        internal Func<JsonObject, JsonObject>? StatusTransform;
        internal bool BlockNextStatus;
        internal readonly TaskCompletionSource<bool> StatusEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> StatusRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Count(string action) => Calls.Count(call => call.Action == action);

        public async Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null)
        {
            Calls.Add((action, name));
            if (action == "status" && BlockNextStatus)
            {
                BlockNextStatus = false;
                StatusEntered.TrySetResult(true);
                await StatusRelease.Task;
            }
            if (Failures.TryGetValue(action, out var failure)) throw failure;
            var data = await demo.SendAsync(settings, action, name);
            if (action == "status" && StatusTransform is not null)
                return JsonSerializer.SerializeToElement(StatusTransform(JsonNode.Parse(data.GetRawText())!.AsObject()));
            return data;
        }

        internal async Task<BackupItem> SnapshotAsync(ConnectionSettings settings)
        {
            // Read the fake fixture directly so preparing it is not counted as a UI request.
            var data = await demo.SendAsync(settings, "backups");
            var item = data.GetProperty("items")[0];
            return new BackupItem(item.GetProperty("name").GetString()!, item.GetProperty("size").GetInt64(), item.GetProperty("modifiedUtc").GetString()!, settings.Snapshot());
        }
    }
}
