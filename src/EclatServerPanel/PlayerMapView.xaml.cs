using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace EclatServerPanel;

public partial class PlayerMapView : UserControl
{
    internal PlayerMapModel Model { get; } = new();
    private readonly ListCollectionView tableView, cardView, placeView;
    private bool updating, selecting;
    private string? selectedId;
    public PlayerMapView()
    {
        InitializeComponent();
        tableView = new(Model.Players); cardView = new(Model.Players); placeView = new(Model.Players);
        foreach (var view in new[] { tableView, cardView, placeView }) { view.Filter = item => Model.Matches((MapPlayer)item); view.SortDescriptions.Add(new(nameof(MapPlayer.Name), ListSortDirection.Ascending)); }
        placeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MapPlayer.Location)));
        PlayerTable.ItemsSource = tableView; PlayerCards.ItemsSource = cardView; PlayerPlaces.ItemsSource = placeView;
        MapCanvas.Model = Model;
        MapCanvas.Selected += id => Select(id, false);
        MapCanvas.CrowdRequested += group => { CrowdTitle.Text = $"{group.Players.Count} joueurs à {group.Section.Name}"; CrowdRows.ItemsSource = group.Players; CrowdPanel.Visibility = Visibility.Visible; CrowdRows.SelectedItem = group.Players.FirstOrDefault(player => player.Id == selectedId); };
        MapCanvas.CrowdClosed += () => CrowdPanel.Visibility = Visibility.Collapsed;
        SearchBox.TextChanged += (_, _) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TableMode.IsChecked = true;
        ResetFilters();
    }
    internal void Apply(JsonElement monitor, bool available)
    {
        var scrolls = new[] { FindScroll(PlayerTable), FindScroll(PlayerCards), FindScroll(PlayerPlaces), FindScroll(CrowdRows) }.Select(scroll => (Scroll: scroll, Offset: scroll?.VerticalOffset ?? 0)).ToArray();
        updating = true;
        // ObservableCollection notifications must reach WPF outside DeferRefresh;
        // its selection code reads CurrentPosition while handling each change.
        Model.Update(monitor, available);
        ResetFilters();
        updating = false;
        RefreshViews();
        Select(selectedId, false);
        if (CrowdPanel.Visibility == Visibility.Visible)
        {
            var crowd = Model.Groups().FirstOrDefault(group => group.Section.Id == MapCanvas.ExpandedSection);
            if (crowd is null || crowd.Players.Count <= 8) CrowdPanel.Visibility = Visibility.Collapsed;
            else { CrowdRows.ItemsSource = crowd.Players; CrowdRows.SelectedItem = crowd.Players.FirstOrDefault(player => player.Id == selectedId); }
        }
        MapSummary.Text = available ? $"{Model.Players.Count} connectés · positions de plus de 10 s estompées · survolez un groupe pour l’ouvrir" : "Activité non confirmée — actualisez avec un serveur compatible.";
        Summarize(available);
        MapCanvas.UpdateClock();
        Dispatcher.BeginInvoke(() => { foreach (var scroll in scrolls) scroll.Scroll?.ScrollToVerticalOffset(scroll.Offset); });
    }
    // Map headline, room legend and busiest places, from the same model as the lists.
    private void Summarize(bool available)
    {
        int placed = Model.Players.Count(player => player.HasPlace);
        MapHeadline.Text = !available ? "En attente du serveur" : placed == 0 ? "Aucun joueur placé" : $"{placed} joueur{(placed > 1 ? "s" : "")} sur la carte";
        RoomLegend.ItemsSource = Model.Players.GroupBy(player => player.RoomId).OrderByDescending(group => group.Count())
            .Select(group => new LegendItem(group.First().RoomBrush, group.First().Room, group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var places = Model.Groups().OrderByDescending(group => group.Players.Count).Take(5).ToList();
        int most = places.Count == 0 ? 1 : places[0].Players.Count;
        BusyPlaces.ItemsSource = places.Select(group => new PlaceItem(group.Section.Name, group.Players.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new GridLength(group.Players.Count, GridUnitType.Star), new GridLength(most - group.Players.Count, GridUnitType.Star))).ToList();
    }
    private void ResetFilters()
    {
        string room = Model.RoomFilter, place = Model.PlaceFilter;
        RoomFilter.ItemsSource = new[] { new Choice("", "Tous les salons") }.Concat(Model.Players.GroupBy(player => player.RoomId).Select(group => new Choice(group.Key, group.First().Room))).ToList();
        PlaceFilter.ItemsSource = new[] { new Choice("", "Tous les lieux") }.Concat(Model.Players.GroupBy(player => player.SectionId.Length == 0 ? "?" : player.SectionId).Select(group => new Choice(group.Key, group.First().Location))).ToList();
        RoomFilter.SelectedValue = room; PlaceFilter.SelectedValue = place;
    }
    private void Filters_Changed(object sender, RoutedEventArgs e)
    {
        if (updating || PlayerTable is null || tableView is null) return;
        Model.Search = SearchBox.Text.Trim(); Model.RoomFilter = RoomFilter.SelectedValue as string ?? ""; Model.PlaceFilter = PlaceFilter.SelectedValue as string ?? "";
        RefreshViews();
    }
    private void RefreshViews() { tableView.Refresh(); cardView.Refresh(); placeView.Refresh(); MapCanvas.InvalidateVisual(); }
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (PlayerTable is null) return;
        string mode = (string)((RadioButton)sender).Tag;
        PlayerTable.Visibility = mode == "table" ? Visibility.Visible : Visibility.Collapsed;
        PlayerCards.Visibility = mode == "cards" ? Visibility.Visible : Visibility.Collapsed;
        PlayerPlaces.Visibility = mode == "places" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Player_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (updating || selecting) return;
        if (((Selector)sender).SelectedItem is MapPlayer player) Select(player.Id, true);
    }
    private void Select(string? id, bool open)
    {
        if (selecting) return;
        selecting = true;
        selectedId = id;
        var player = Model.Players.FirstOrDefault(player => player.Id == id);
        PlayerTable.SelectedItem = player; PlayerCards.SelectedItem = player; PlayerPlaces.SelectedItem = player;
        CrowdRows.SelectedItem = player;
        Inspector.DataContext = player;
        Inspector.Visibility = player is null ? Visibility.Collapsed : Visibility.Visible;
        PlacesSummary.Visibility = player is null ? Visibility.Visible : Visibility.Collapsed;
        MapCanvas.Select(id, open);
        selecting = false;
    }
    internal void Tick() { foreach (var player in Model.Players) player.Tick(); MapCanvas.InvalidateVisual(); }
    internal void CloseOverlay() => MapCanvas.Collapse();
    internal void Clear() { updating = true; selectedId = null; Model.Clear(); SearchBox.Text = ""; ResetFilters(); MapCanvas.Collapse(); updating = false; RefreshViews(); MapSummary.Text = "En attente du serveur."; Summarize(false); Inspector.Visibility = Visibility.Collapsed; PlacesSummary.Visibility = Visibility.Visible; MapCanvas.UpdateClock(); }
    internal void CaptureMode(string mode, int groupSize = 0)
    {
        (mode == "cards" ? CardsMode : mode == "places" ? PlacesMode : TableMode).IsChecked = true;
        if (groupSize > 0 && Model.Groups().FirstOrDefault(group => group.Players.Count == groupSize) is { } group) Select(group.Players[0].Id, true);
        MapCanvas.Settle();
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (FindScroll(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }
    private sealed record Choice(string Id, string Label);
    private sealed record LegendItem(System.Windows.Media.Brush Brush, string Name, string Count);
    private sealed record PlaceItem(string Name, string Count, GridLength Fill, GridLength Rest);
}
