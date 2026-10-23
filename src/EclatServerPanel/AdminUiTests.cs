using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace EclatServerPanel;

/// <summary>Real WPF workflows with memory-only game data and an injected transport.</summary>
internal static class AdminUiTests
{
    internal static async Task RunAsync()
    {
        Check(Application.Current.Dispatcher.CheckAccess(), "The WPF dispatcher is required.");
        await MapViewsAsync();
        await GroupSelectionAsync();
        await ConfirmationsAsync();
        await RoomEditAsync();
        await QueuedAndUncertainCommandAsync();
        await StaleOriginAsync();
        await ConfigurationAndStatisticsAsync();
        await JournalAndConnectionResetAsync();
        await FailedResponsesAsync();
    }

    private static async Task MapViewsAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Map");
        var map = Find<PlayerMapView>(test.Window, "MapPage");
        var table = Find<DataGrid>(map, "PlayerTable");
        var canvas = Find<PlayerMapCanvas>(map, "MapCanvas");
        Check(map.Model.Players.Count == 20 && table.Items.Count == 20, "A refresh must expose all twenty synthetic players.");
        Check(map.Model.Players.Count(player => !player.HasPlace) == 1, "A player without a position must remain in the list.");
        var selected = map.Model.Groups().Single(group => group.Players.Count == 5).Players[0];
        table.SelectedItem = selected;
        Check(canvas.SelectedId == selected.Id, "Selecting a table row must select its point on the map.");

        var view = CollectionViewSource.GetDefaultView(table.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new(nameof(MapPlayer.Dex), ListSortDirection.Descending));
        table.Columns.Single(column => column.SortMemberPath == nameof(MapPlayer.Dex)).SortDirection = ListSortDirection.Descending;
        table.ScrollIntoView(table.Items[table.Items.Count - 1]);
        await PumpAsync(test.Window);
        var scroll = Descendants<ScrollViewer>(table).FirstOrDefault();
        double offset = scroll?.VerticalOffset ?? 0;
        Check(scroll is not null && offset > 0, "The scroll-preservation scenario must actually move down a long list.");
        await Bounded(test.Window.RefreshAsync(), "map refresh preserving the user's list state");
        await PumpAsync(test.Window);
        Check(table.SelectedItem is MapPlayer after && after.Id == selected.Id && canvas.SelectedId == selected.Id, "Polling must preserve the selected player and map point.");
        Check(view.SortDescriptions.Any(sort => sort.PropertyName == nameof(MapPlayer.Dex) && sort.Direction == ListSortDirection.Descending), "Polling must preserve the chosen column and direction.");
        var dexOrder = table.Items.OfType<MapPlayer>().Select(player => player.Dex ?? -1).ToArray();
        Check(dexOrder.SequenceEqual(dexOrder.OrderDescending()), "The visible table must keep the actual descending Pokédex order.");
        if (scroll is not null && offset > 0) Check(Math.Abs(scroll.VerticalOffset - offset) <= 1, "Polling must preserve the list scroll position.");

        Find<TextBox>(map, "SearchBox").Text = "eleonore";
        Check(table.Items.Count == 1 && table.Items[0] is MapPlayer match && match.Name.Contains("Éléonore"), "Search must match an accented long name without requiring accents.");
        await Bounded(test.Window.RefreshAsync(), "map refresh with active search");
        Check(table.Items.Count == 1 && Find<TextBox>(map, "SearchBox").Text == "eleonore", "Polling must preserve search and its filtered result.");
        foreach (var mode in new[] { (Radio: "CardsMode", List: "PlayerCards"), (Radio: "PlacesMode", List: "PlayerPlaces"), (Radio: "TableMode", List: "PlayerTable") })
        {
            Find<RadioButton>(map, mode.Radio).IsChecked = true;
            var list = Find<ItemsControl>(map, mode.List);
            Check(list.Visibility == Visibility.Visible && list.Items.Count == 1, "All three display modes must share the active filter.");
        }
        Find<TextBox>(map, "SearchBox").Text = "";
        Find<ComboBox>(map, "RoomFilter").SelectedValue = selected.RoomId;
        Find<ComboBox>(map, "PlaceFilter").SelectedValue = selected.SectionId;
        Check(table.Items.Count == 5, "Room and section filters must combine.");
        await Bounded(test.Window.RefreshAsync(), "map refresh with room and place filters");
        Check(table.Items.Count == 5 && canvas.SelectedId == selected.Id, "Polling must preserve both filters and the selected map point.");
    }

    private static async Task GroupSelectionAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Map");
        var map = Find<PlayerMapView>(test.Window, "MapPage");
        var table = Find<DataGrid>(map, "PlayerTable");
        var canvas = Find<PlayerMapCanvas>(map, "MapCanvas");
        var five = map.Model.Groups().Single(group => group.Players.Count == 5);
        canvas.Select(five.Players[2].Id, true);
        await PumpAsync(test.Window);
        Check(canvas.ExpandedSection == five.Section.Id && table.SelectedItem is MapPlayer selected && selected.Id == five.Players[2].Id, "Opening a small map group must keep list and point selection in sync.");
        var twelve = map.Model.Groups().Single(group => group.Players.Count == 12);
        canvas.Select(twelve.Players[0].Id, true);
        await PumpAsync(test.Window);
        var crowd = Find<Border>(map, "CrowdPanel");
        var rows = Find<ListBox>(map, "CrowdRows");
        Check(crowd.Visibility == Visibility.Visible && rows.Items.Count == 12, "A group larger than eight must expose every player in its list bubble.");
        rows.SelectedItem = twelve.Players[7];
        Check(canvas.SelectedId == twelve.Players[7].Id && table.SelectedItem is MapPlayer row && row.Id == twelve.Players[7].Id, "Selecting a person in a large group must select the same list row and map point.");
        map.CloseOverlay();
        Check(crowd.Visibility == Visibility.Collapsed && canvas.ExpandedSection is null, "Closing the group must close the bubble and its expanded state.");
    }

    private static async Task ConfirmationsAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Commands");
        var admin = Find<AdminPanelView>(test.Window, "CommandsPage");
        var account = Find<ComboBox>(admin, "AccountChoice").Items.OfType<AdminChoice>().First();
        var ban = AdminContract.Plan("account.ban", JsonSerializer.SerializeToElement(new { accountHash = account.Id, reason = "Essai de confirmation", expiresAt = (string?)null }), account.Name);
        var cancelled = test.Window.ExecuteAdminAsync(ban, test.Settings);
        await DialogAsync(test.Window);
        Click(test.Window, "DialogCancel");
        await Bounded(cancelled, "cancelled account ban");
        Check(test.Transport.Count("admin.command") == 0, "Cancelling a ban must not contact the server.");
        await DialogClosedAsync(test.Window);
        await ConfirmTypedAsync(test, ban, account.Name);
        Check(test.Transport.Count("admin.command") == 1 && test.Transport.Commands.Last() == "account.ban", "Typing the account name must execute exactly one ban.");

        var room = Find<ComboBox>(admin, "RoomChoice").Items.OfType<AdminChoice>().First();
        var close = AdminContract.Plan("room.close", JsonSerializer.SerializeToElement(new { roomId = room.Id }), room.Name);
        await ConfirmTypedAsync(test, close, room.Name);
        Check(test.Transport.Count("admin.command") == 2 && test.Transport.Commands.Last() == "room.close", "Closing a room must require its actual name and execute once.");
        var money = AdminContract.Plan("eclats.adjust", JsonSerializer.SerializeToElement(new { accountHash = account.Id, amount = 37, reason = "Correction de test" }), account.Name);
        await ConfirmTypedAsync(test, money, "37");
        Check(test.Transport.Count("admin.command") == 3 && test.Transport.Commands.Last() == "eclats.adjust", "An economy adjustment must require the exact signed amount and execute once.");
    }

    private static async Task RoomEditAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Commands");
        var admin = Find<AdminPanelView>(test.Window, "CommandsPage");
        Find<ComboBox>(admin, "CommandChoice").SelectedValue = "room.update";
        var rooms = Find<ComboBox>(admin, "RoomChoice");
        rooms.SelectedIndex = 1; // The synthetic second room is private, with ten members.
        Check(Find<ComboBox>(admin, "RoomVisibility").SelectedValue as string == "private", "A private room must populate its actual visibility before editing.");
        rooms.SelectedIndex = 2; // Two members, so reducing capacity is valid.
        Find<TextBox>(admin, "RoomCapacity").Text = "7";
        await Bounded(test.Window.LoadAdministrationAsync(), "refresh while room capacity is being edited");
        Check(Find<TextBox>(admin, "RoomCapacity").Text == "7", "Polling must preserve a room draft.");
        ClickByContent(admin, "Exécuter");
        await Bounded(test.Window.WaitForIdleAsync(), "room capacity change");
        var capacity = test.Transport.CommandArguments.Last();
        Check(capacity.GetProperty("capacity").GetInt32() == 7 && !capacity.TryGetProperty("visibility", out _), "Changing capacity must not overwrite visibility.");
        rooms.SelectedIndex = 1;
        Find<ComboBox>(admin, "RoomVisibility").SelectedValue = "public";
        ClickByContent(admin, "Exécuter");
        await Bounded(test.Window.WaitForIdleAsync(), "room visibility change");
        var visibility = test.Transport.CommandArguments.Last();
        Check(visibility.GetProperty("visibility").GetString() == "public" && !visibility.TryGetProperty("capacity", out _), "Changing visibility must not overwrite capacity.");
    }

    private static async Task QueuedAndUncertainCommandAsync()
    {
        using var test = await TestWindow.CreateAsync();
        var plan = AdminContract.Plan("maintenance.set", JsonSerializer.SerializeToElement(new { enabled = true }), "Maintenance");
        test.Transport.BlockNextStatus = true;
        var refresh = test.Window.RefreshAsync();
        await Bounded(test.Transport.StatusEntered.Task, "blocked refresh entered");
        var pending = test.Window.ExecuteAdminAsync(plan, test.Settings);
        Check(test.Transport.Count("admin.command") == 0 && Find<Grid>(test.Window, "DialogLayer").Visibility != Visibility.Visible, "A queued command must wait for the current operation before confirming.");
        test.Transport.StatusRelease.TrySetResult(true);
        await Bounded(refresh, "release status before command");
        await DialogAsync(test.Window);
        Click(test.Window, "DialogConfirm");
        await Bounded(pending, "queued maintenance command");
        Check(test.Transport.Count("admin.command") == 1 && test.Transport.MaximumConcurrent == 1, "Queued mutations must execute once and remain serialized.");
        await DialogClosedAsync(test.Window);

        test.Transport.Failures["admin.command"] = new TimeoutException("Synthetic command result unknown");
        test.Transport.Failures["admin.overview"] = new InvalidOperationException("Synthetic follow-up read failed");
        var uncertain = test.Window.ExecuteAdminAsync(plan, test.Settings);
        await DialogAsync(test.Window);
        Click(test.Window, "DialogConfirm");
        await Bounded(uncertain, "uncertain command without retry");
        Check(test.Transport.Count("admin.command") == 2, "An uncertain mutation must not be retried.");
        Check(Text(test.Window, "ServerStatus") == "État non confirmé", "An unavailable follow-up read must leave the service state unknown.");
        Check(Text(Find<AdminPanelView>(test.Window, "CommandsPage"), "AdminState").Contains("incertaine"), "The interface must explain that an uncertain command must not be repeated blindly.");
    }

    private static async Task StaleOriginAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Commands");
        var plan = AdminContract.Plan("maintenance.set", JsonSerializer.SerializeToElement(new { enabled = true }), "Maintenance");
        int before = test.Transport.Calls.Count;
        test.Window.ApplyConnection(test.Settings with { Host = "different.invalid" });
        await Bounded(test.Window.ExecuteAdminAsync(plan, test.Settings), "stale target refusal");
        Check(test.Transport.Calls.Count == before, "A target loaded from the previous host must be refused before any request.");
        Check(Find<Grid>(test.Window, "DialogLayer").Visibility == Visibility.Collapsed, "A stale target must not offer a confirmation that could imply it is current.");
    }

    private static async Task ConfigurationAndStatisticsAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await test.Transport.SeedHistoryAsync(test.Settings);
        await NavigateAsync(test, "Commands");
        var admin = Find<AdminPanelView>(test.Window, "CommandsPage");
        Find<RadioButton>(admin, "ConfigTab").IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), "configuration tab reads");
        Check(test.Transport.Count("config.get") == 1 && test.Transport.Count("config.history") == 1, "Opening the configuration tab must load the actual editor and history through its controller.");
        Check(Find<ComboBox>(admin, "ConfigVersions").Items.Count == 20, "The real editor must expose all twenty retained versions.");
        Check(Find<DataGrid>(admin, "EventsGrid").Items.Count > 0 && Find<DataGrid>(admin, "BannersGrid").Items.Count > 0, "Configuration controls must load real event and banner rows.");
        Find<TextBox>(admin, "SinglePrice").Text = "31";
        Find<TextBox>(admin, "ConfigReason").Text = "Prix changé pendant le test hors ligne";
        ClickByContent(admin, "Enregistrer et contrôler");
        await Bounded(test.Window.WaitForIdleAsync(), "configuration save");
        Check(test.Transport.Count("config.save") == 1 && Find<TextBox>(admin, "SinglePrice").Text == "31", "Saving the price must call the transport and display its reloaded value.");
        Check(Text(admin, "ConfigState").Contains("acceptée"), "A confirmed reload must display the game acknowledgement.");

        var versions = Find<ComboBox>(admin, "ConfigVersions");
        versions.SelectedIndex = versions.Items.Count - 1;
        Find<TextBox>(admin, "ConfigReason").Text = "Restaurer le prix de démonstration";
        ClickByContent(admin, "Restaurer cette version");
        await DialogAsync(test.Window);
        Click(test.Window, "DialogCancel");
        await Bounded(test.Window.WaitForIdleAsync(), "cancelled configuration restore");
        Check(test.Transport.Count("config.restore") == 0, "Cancelling configuration restoration must not send it.");
        await DialogClosedAsync(test.Window);
        ClickByContent(admin, "Restaurer cette version");
        await DialogAsync(test.Window);
        Click(test.Window, "DialogConfirm");
        await Bounded(test.Window.WaitForIdleAsync(), "configuration restore");
        Check(test.Transport.Count("config.restore") == 1 && Find<TextBox>(admin, "SinglePrice").Text == "30", "Restoration must use the selected historic version and reload the editor.");

        Find<RadioButton>(admin, "StatsTab").IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), "statistics tab");
        Check(test.Transport.Count("statistics") == 1 && Find<ItemsControl>(admin, "StatsTables").Items.Count >= 4, "The statistics tab must render each requested result table.");
        Check(Text(admin, "StatsScope").Contains("UTC"), "Daily activity must identify its timezone.");
    }

    private static async Task JournalAndConnectionResetAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Map");
        await NavigateAsync(test, "Commands");
        var admin = Find<AdminPanelView>(test.Window, "CommandsPage");
        var hashes = Find<ComboBox>(admin, "AccountChoice").Items.OfType<AdminChoice>().Select(item => item.Id).ToArray();
        Check(hashes.Length > 0 && Find<ComboBox>(admin, "AccountChoice").Items.OfType<AdminChoice>().All(item => !item.Label.Contains(item.Id)), "Account choices must retain the full target internally and mask it in their visible labels.");
        await NavigateAsync(test, "Logs");
        Find<RadioButton>(test.Window, "AdminLogTab").IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), "administration journal tab");
        var journal = Find<AdminAuditView>(test.Window, "AdminJournal");
        var rows = Find<ListBox>(journal, "AuditRows");
        Check(test.Transport.Count("admin.audit") == 1 && rows.Items.Count > 0, "The administration journal must request and display the actual audit response.");
        await PumpAsync(test.Window);
        var visible = string.Join("\n", Descendants<TextBlock>(journal).Select(block => block.Text));
        Check(hashes.All(hash => !visible.Contains(hash)), "Full account fingerprints must not appear in visible audit text.");
        Check(hashes.Any(hash => visible.Contains(hash[..8])), "The audit should retain a usable eight-character account reference.");
        journal.Apply(JsonSerializer.SerializeToElement(new { items = Array.Empty<object>(), serverAuditUnavailable = "Synthetic game socket unavailable" }));
        Check(Text(journal, "AuditState").Contains("Journal partiel"), "A configuration-only journal must not be presented as the complete server audit.");
        int before = test.Transport.Calls.Count;
        test.Window.ApplyConnection(test.Settings with { Host = "next.invalid" });
        await PumpAsync(test.Window);
        Check(test.Transport.Calls.Count == before, "A connection change must only invalidate local views.");
        Check(Find<PlayerMapView>(test.Window, "MapPage").Model.Players.Count == 0, "A connection change must clear the previous player's map positions.");
        Check(Find<ComboBox>(admin, "AccountChoice").Items.Count == 0 && Find<ComboBox>(admin, "ConfigVersions").Items.Count == 0, "A connection change must clear administration targets and configuration history.");
        Check(rows.Items.Count == 0, "A connection change must clear the previous host's administration journal.");
    }

    private static async Task FailedResponsesAsync()
    {
        using var test = await TestWindow.CreateAsync();
        await NavigateAsync(test, "Map");
        test.Transport.Failures["status"] = new InvalidOperationException("Synthetic status unavailable");
        await Bounded(test.Window.RefreshAsync(), "failed map refresh");
        Check(Find<PlayerMapView>(test.Window, "MapPage").Model.Players.Count == 0 && Text(test.Window, "ServerStatus") == "État non confirmé", "A failed status must not keep old positions visible as live data.");
        test.Transport.Failures.Clear();
        await NavigateAsync(test, "Commands");
        var admin = Find<AdminPanelView>(test.Window, "CommandsPage");
        Find<RadioButton>(admin, "ConfigTab").IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), "successful initial configuration");
        test.Transport.Failures["config.get"] = new InvalidOperationException("Synthetic configuration unavailable");
        ClickByContent(admin, "Relire la configuration");
        await Bounded(test.Window.WaitForIdleAsync(), "failed configuration reload");
        Check(!Text(admin, "ConfigState").Contains("acceptée"), "A failed reload must not present the previous configuration as currently confirmed.");
        Check(Text(admin, "ConfigState").Contains("échec") && Find<TextBlock>(admin, "ConfigState").Foreground is SolidColorBrush tone
            && tone.Color == ((SolidColorBrush)admin.FindResource("Warning")).Color, "The failed configuration read must remain visible with its warning state.");
        test.Transport.Failures.Clear();
        await NavigateAsync(test, "Logs");
        Find<RadioButton>(test.Window, "AdminLogTab").IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), "initial administration journal");
        test.Transport.Failures["admin.audit"] = new InvalidOperationException("Synthetic audit unavailable");
        await Bounded(test.Window.LoadAdminAuditAsync(), "failed audit reload");
        var journal = Find<AdminAuditView>(test.Window, "AdminJournal");
        Check(Find<ListBox>(journal, "AuditRows").Items.Count == 0 && Text(journal, "AuditState").Contains("n’a pas pu"), "A failed journal read must clear old rows and label its unavailable state.");
    }

    private static async Task ConfirmTypedAsync(TestWindow test, PlannedAdminCommand plan, string expected)
    {
        var operation = test.Window.ExecuteAdminAsync(plan, test.Settings);
        await DialogAsync(test.Window);
        var input = Find<TextBox>(test.Window, "DialogInput");
        var confirm = Find<Button>(test.Window, "DialogConfirm");
        Check(!confirm.IsEnabled, "A typed confirmation must begin disabled.");
        input.Text = expected + " incorrect";
        Check(!confirm.IsEnabled, "An approximate confirmation must remain disabled.");
        input.Text = expected;
        Check(confirm.IsEnabled, "The exact confirmation must enable the action.");
        Click(test.Window, "DialogConfirm");
        await Bounded(operation, plan.Command);
        await DialogClosedAsync(test.Window);
    }

    private static async Task NavigateAsync(TestWindow test, string page)
    {
        Find<RadioButton>(test.Window, "Nav" + page).IsChecked = true;
        await Bounded(test.Window.WaitForIdleAsync(), page + " navigation");
        await PumpAsync(test.Window);
    }
    private static async Task PumpAsync(MainWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(40);
    }
    private static T Find<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? throw new InvalidOperationException("Administration UI test control missing: " + name);
    private static string Text(FrameworkElement root, string name) => Find<TextBlock>(root, name).Text;
    private static void Click(FrameworkElement root, string name) => Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickByContent(FrameworkElement root, string startsWith)
    {
        root.UpdateLayout();
        Descendants<Button>(root).First(button => button.Content is string text && text.StartsWith(startsWith, StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static Task DialogAsync(MainWindow window) => UntilAsync(() => Find<Grid>(window, "DialogLayer").Visibility == Visibility.Visible, "confirmation open");
    private static Task DialogClosedAsync(MainWindow window) => UntilAsync(() => Find<Grid>(window, "DialogLayer").Visibility == Visibility.Collapsed, "confirmation close");
    private static async Task UntilAsync(Func<bool> condition, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("Administration UI test timed out: " + operation); await Task.Delay(20); }
    }
    private static async Task Bounded(Task task, string operation)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8))) != task) throw new TimeoutException("Administration UI test timed out: " + operation);
        await task;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Administration UI test failed: " + message); }

    private sealed class TestWindow : IDisposable
    {
        internal FakeTransport Transport { get; } = new();
        internal ConnectionSettings Settings { get; } = new() { Host = "tests.invalid", User = "eclat-panel", KeyPath = "synthetic-key" };
        internal MainWindow Window { get; }
        private TestWindow() => Window = new MainWindow(false, Transport, Settings, automaticRefresh: false)
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        internal static async Task<TestWindow> CreateAsync()
        {
            var test = new TestWindow();
            test.Window.Show();
            await UntilAsync(() => test.Window.IsLoaded, "synthetic offscreen window");
            await PumpAsync(test.Window);
            return test;
        }
        public void Dispose() { Transport.StatusRelease.TrySetResult(true); Window.Close(); }
    }

    private sealed class FakeTransport : IPanelTransport
    {
        private readonly DemoTransport demo = new();
        internal List<string> Calls { get; } = [];
        internal List<string> Commands { get; } = [];
        internal List<JsonElement> CommandArguments { get; } = [];
        internal Dictionary<string, Exception> Failures { get; } = [];
        internal bool BlockNextStatus;
        internal TaskCompletionSource<bool> StatusEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> StatusRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int concurrent;
        internal int MaximumConcurrent { get; private set; }
        internal int Count(string action) => Calls.Count(value => value == action);
        public async Task<JsonElement> SendAsync(ConnectionSettings settings, string action, string? name = null)
        {
            Calls.Add(action); Enter();
            try
            {
                if (action == "status" && BlockNextStatus) { BlockNextStatus = false; StatusEntered.TrySetResult(true); await StatusRelease.Task; }
                if (Failures.TryGetValue(action, out var failure)) throw failure;
                return await demo.SendAsync(settings, action, name);
            }
            finally { concurrent--; }
        }
        public async Task<JsonElement> SendAdminAsync(ConnectionSettings settings, string action, JsonElement fields)
        {
            Calls.Add(action); Enter();
            try
            {
                if (action == "admin.command") { Commands.Add(fields.GetProperty("command").GetString()!); CommandArguments.Add(fields.GetProperty("arguments").Clone()); }
                if (Failures.TryGetValue(action, out var failure)) throw failure;
                return await demo.SendAdminAsync(settings, action, fields);
            }
            finally { concurrent--; }
        }
        private void Enter() { concurrent++; MaximumConcurrent = Math.Max(MaximumConcurrent, concurrent); }
        internal async Task SeedHistoryAsync(ConnectionSettings settings)
        {
            var empty = JsonSerializer.SerializeToElement(new { });
            for (int index = 0; index < 22; index++)
            {
                var current = await demo.SendAdminAsync(settings, "config.get", empty);
                var fields = JsonSerializer.SerializeToElement(new { requestId = Guid.NewGuid().ToString(), config = current.GetProperty("config"), expectedSha256 = current.GetProperty("sha256").GetString(), reason = "Historique de test " + index });
                await demo.SendAdminAsync(settings, "config.save", fields);
            }
        }
    }
}
