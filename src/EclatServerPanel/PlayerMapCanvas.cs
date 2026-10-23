using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EclatServerPanel;

internal sealed class PlayerMapCanvas : FrameworkElement
{
    internal PlayerMapModel Model { get; set; } = new();
    internal string? SelectedId { get; private set; }
    internal string? ExpandedSection { get; private set; }
    internal event Action<string>? Selected;
    internal event Action<MapGroup>? CrowdRequested;
    internal event Action? CrowdClosed;
    private readonly List<(Point Point, double Radius, MapGroup Group, MapPlayer? Player)> hits = [];
    public PlayerMapCanvas() { MinHeight = 250; ClipToBounds = true; Focusable = true; }
    private Brush Brush(string name) => (Brush)FindResource(name);
    private Point Center(MapSection section) => new(26 + (section.X + section.Width / 2) / Model.Atlas.Width * Math.Max(1, ActualWidth - 52), 22 + (section.Y + section.Height / 2) / Model.Atlas.Height * Math.Max(1, ActualHeight - 44));
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRoundedRectangle(Brush("Bg1"), new Pen(Brush("Line"), 1), new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        hits.Clear();
        foreach (var section in Model.Atlas.Sections.OrderBy(section => section.Kind == "city" ? 1 : 0))
        {
            var center = Center(section);
            double w = section.Width / Model.Atlas.Width * Math.Max(1, ActualWidth - 52), h = section.Height / Model.Atlas.Height * Math.Max(1, ActualHeight - 44);
            drawing.DrawRoundedRectangle(Brush(section.Kind == "route" ? "Line" : "CardHigh"), new Pen(Brush(section.Kind == "city" ? "Cyan" : "Faint"), 1), new Rect(center.X - w / 2, center.Y - h / 2, w, h), section.Kind == "city" ? 5 : 2, section.Kind == "city" ? 5 : 2);
        }
        foreach (var group in Model.Groups())
        {
            Point anchor = Center(group.Section);
            bool selected = group.Players.Any(player => player.Id == SelectedId);
            if (group.Players.Count > 1 && group.Section.Id == ExpandedSection && group.Players.Count <= 8)
            {
                var fan = PlayerMapModel.Fan(anchor.X, anchor.Y, group.Players.Count, ActualWidth, ActualHeight);
                for (int index = 0; index < fan.Count; index++)
                {
                    Point point = new(fan[index].X, fan[index].Y);
                    drawing.DrawLine(new Pen(Brush("Muted"), 1), anchor, point);
                    Dot(drawing, point, group.Players[index], group, group.Players[index].Name.Length == 0 ? "?" : group.Players[index].Name[..1], group.Players[index].Id == SelectedId);
                }
                drawing.DrawEllipse(Brush("Cyan"), null, anchor, 4, 4);
            }
            else Dot(drawing, anchor, group.Players.Count == 1 ? group.Players[0] : null, group, group.Players.Count == 1 ? group.Players[0].Name[..1] : group.Players.Count.ToString(CultureInfo.InvariantCulture), selected);
        }
        if (Model.Atlas.Sections.Count == 0) Label(drawing, "La carte sera fournie par un serveur compatible.", new Point(22, 28), false);
    }
    private void Dot(DrawingContext drawing, Point point, MapPlayer? player, MapGroup group, string text, bool selected)
    {
        bool stale = player?.IsStale ?? group.Players.All(member => member.IsStale);
        drawing.PushOpacity(stale ? 0.4 : 1);
        drawing.DrawEllipse(Brush("CardHigh"), new Pen(Brush(selected ? "Warning" : "Cyan"), selected ? 3 : 2), point, 14, 14);
        var label = new FormattedText(text, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, Brush("Text"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(label, new Point(point.X - label.Width / 2, point.Y - label.Height / 2));
        drawing.Pop();
        hits.Add((point, 17, group, player));
    }
    private void Label(DrawingContext drawing, string value, Point point, bool bold)
    {
        var text = new FormattedText(value, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(20, ActualWidth - 44) };
        drawing.DrawText(text, point);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hit = hits.LastOrDefault(hit => (hit.Point - point).Length <= hit.Radius);
        if (hit.Group is null) { Cursor = Cursors.Arrow; ToolTip = null; return; }
        Cursor = Cursors.Hand;
        ToolTip = new TextBlock { Text = hit.Player?.Detail ?? $"{hit.Group.Section.Name}\n{hit.Group.Players.Count} joueurs — cliquer pour choisir", MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
        if (hit.Player is null && ExpandedSection != hit.Group.Section.Id) Expand(hit.Group);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        var hit = hits.LastOrDefault(hit => (hit.Point - point).Length <= hit.Radius);
        if (hit.Group is null) { Collapse(); return; }
        if (hit.Player is { } player) Select(player.Id, false);
        else Expand(hit.Group);
        e.Handled = true;
    }
    internal void Select(string? id, bool openGroup = true)
    {
        SelectedId = id;
        if (openGroup && Model.Groups().FirstOrDefault(group => group.Players.Any(player => player.Id == id)) is { } group && group.Players.Count > 1) Expand(group);
        InvalidateVisual();
        if (id is not null) Selected?.Invoke(id);
    }
    private void Expand(MapGroup group)
    {
        ExpandedSection = group.Section.Id;
        if (group.Players.Count > 8)
        {
            CrowdRequested?.Invoke(group);
        }
        else CrowdClosed?.Invoke();
        InvalidateVisual();
    }
    internal void Collapse() { ExpandedSection = null; CrowdClosed?.Invoke(); InvalidateVisual(); }
}
