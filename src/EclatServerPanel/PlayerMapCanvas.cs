using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EclatServerPanel;

/// <summary>Room colours shared by the map, the legend and the player lists.</summary>
internal static class MapPalette
{
    private static readonly Color[] Colors = [Rgb(0x5E, 0xE6, 0xFF), Rgb(0x8B, 0x7B, 0xFF), Rgb(0x3D, 0xDC, 0x97), Rgb(0xFF, 0xB8, 0x4D), Rgb(0xFF, 0x7A, 0xC8), Rgb(0xFF, 0x8A, 0x5C)];
    private static readonly SolidColorBrush[] Brushes = Colors.Select(color => Frozen(new SolidColorBrush(color))).ToArray();
    private static readonly SolidColorBrush[] Soft = Colors.Select(color => Frozen(new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)))).ToArray();
    internal static readonly SolidColorBrush Neutral = Frozen(new SolidColorBrush(Rgb(0x8A, 0x96, 0xC0)));
    internal static int Tone(string roomId)
    {
        if (roomId.Length == 0) return -1;
        int hash = 17;
        foreach (char c in roomId) hash = unchecked(hash * 31 + c);
        return (int)((uint)hash % (uint)Colors.Length);
    }
    internal static SolidColorBrush Brush(int tone) => tone < 0 ? Neutral : Brushes[tone % Brushes.Length];
    internal static SolidColorBrush SoftBrush(int tone) => tone < 0 ? Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x8A, 0x96, 0xC0))) : Soft[tone % Soft.Length];
    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    internal static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}

/// <summary>
/// Schematic region drawn from the server's section grid: land masses around
/// every section, routes as roads, towns as tiles, players as animated pins.
/// Groups open as a fan (up to eight players) or in the side list.
/// </summary>
internal sealed class PlayerMapCanvas : FrameworkElement
{
    internal PlayerMapModel Model { get; set; } = new();
    internal string? SelectedId { get; private set; }
    internal string? ExpandedSection { get; private set; }
    internal event Action<string>? Selected;
    internal event Action<MapGroup>? CrowdRequested;
    internal event Action? CrowdClosed;

    private readonly List<(Point Point, double Radius, MapGroup Group, MapPlayer? Player)> hits = [];
    private readonly DispatcherTimer frames = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DateTime started = DateTime.UtcNow;
    private DateTime fanStarted = DateTime.MinValue;
    private MapSection? hovered;
    private Point hoverPoint;

    // Geometry depends only on the atlas and the size: cache it between frames.
    private MapAtlas? cachedAtlas;
    private Size cachedSize;
    private Geometry? land, roads;
    private double cell;
    private Point origin;

    private static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Brush Sea = MapPalette.Frozen(new RadialGradientBrush(Color.FromRgb(0x10, 0x1A, 0x3C), Color.FromRgb(0x0A, 0x10, 0x26)) { Center = new Point(0.45, 0.4), GradientOrigin = new Point(0.45, 0.4), RadiusX = 0.8, RadiusY = 0.9 });
    private static readonly Brush LandFill = MapPalette.Frozen(new LinearGradientBrush(Color.FromRgb(0x1B, 0x29, 0x55), Color.FromRgb(0x15, 0x20, 0x45), 90));
    private static readonly Pen Coast = MapPalette.Frozen(new Pen(MapPalette.Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x5E, 0xE6, 0xFF))), 1.2));
    private static readonly Pen Shelf = MapPalette.Frozen(new Pen(MapPalette.Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0x5E, 0xE6, 0xFF))), 7) { LineJoin = PenLineJoin.Round });
    private static readonly Brush GridDot = MapPalette.Frozen(new SolidColorBrush(Color.FromArgb(0x1C, 0x9F, 0xB4, 0xFF)));
    private static readonly Brush RoadBrush = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x3C, 0x7A)));
    private static readonly Brush RoadLine = MapPalette.Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x9F, 0xB4, 0xFF)));
    private static readonly Brush TownFill = MapPalette.Frozen(new LinearGradientBrush(Color.FromRgb(0x2A, 0x3A, 0x78), Color.FromRgb(0x1E, 0x2B, 0x5E), 90));
    private static readonly Brush Cyan = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0x5E, 0xE6, 0xFF)));
    private static readonly Brush Violet = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0xFF)));
    private static readonly Brush Gold = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)));
    private static readonly Brush Ink = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x1F)));
    private static readonly Brush TextBrush = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xFF)));
    private static readonly Brush MutedBrush = MapPalette.Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xD0)));
    private static readonly Brush PillBrush = MapPalette.Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x1C, 0x3A)));
    private static readonly Brush CrowdFill = MapPalette.Frozen(new LinearGradientBrush(Color.FromRgb(0x5E, 0xE6, 0xFF), Color.FromRgb(0x8B, 0x7B, 0xFF), 45));
    private static readonly Pen PillPen = MapPalette.Frozen(new Pen(MapPalette.Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x5E, 0xE6, 0xFF))), 1));
    private static readonly Pen InkPen = MapPalette.Frozen(new Pen(Ink, 2));

    public PlayerMapCanvas()
    {
        MinHeight = 250; ClipToBounds = true; Focusable = true;
        frames.Tick += (_, _) => { if (Window.GetWindow(this)?.WindowState != WindowState.Minimized) InvalidateVisual(); };
        IsVisibleChanged += (_, _) => UpdateClock();
        Unloaded += (_, _) => frames.Stop();
    }

    // Pins breathe only while the map is on screen; a hidden page costs nothing.
    internal void UpdateClock() { if (IsVisible && Model.Players.Count > 0) frames.Start(); else frames.Stop(); }

    private double Phase => (DateTime.UtcNow - started).TotalSeconds;
    private double FanProgress
    {
        get
        {
            double t = Math.Clamp((DateTime.UtcNow - fanStarted).TotalMilliseconds / 280, 0, 1);
            return 1 - Math.Pow(1 - t, 3);
        }
    }

    private void Layout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (ReferenceEquals(cachedAtlas, Model.Atlas) && cachedSize == size && land is not null) return;
        cachedAtlas = Model.Atlas; cachedSize = size;
        // The headline and legend sit in a left column when the map is wide,
        // above and below the region otherwise.
        bool column = size.Width >= 640;
        double left = column ? 230 : 24, right = 24, top = column ? 22 : 62, bottom = column ? 22 : 50;
        cell = Math.Max(4, Math.Min((size.Width - left - right) / Model.Atlas.Width, (size.Height - top - bottom) / Model.Atlas.Height));
        origin = new Point(left + (size.Width - left - right - cell * Model.Atlas.Width) / 2, top + (size.Height - top - bottom - cell * Model.Atlas.Height) / 2);
        var landGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
        var roadGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var section in Model.Atlas.Sections)
        {
            var rect = Cells(section);
            double grow = cell * 0.5;
            landGroup.Children.Add(new RectangleGeometry(new Rect(rect.X - grow, rect.Y - grow, rect.Width + 2 * grow, rect.Height + 2 * grow), cell * 0.9, cell * 0.9));
            if (section.Kind == "route")
            {
                double thick = Math.Max(4, cell * 0.26);
                var line = section.Width >= section.Height
                    ? new Rect(rect.X + cell * 0.2, rect.Y + (rect.Height - thick) / 2, rect.Width - cell * 0.4, thick)
                    : new Rect(rect.X + (rect.Width - thick) / 2, rect.Y + cell * 0.2, thick, rect.Height - cell * 0.4);
                roadGroup.Children.Add(new RectangleGeometry(line, thick / 2, thick / 2));
            }
        }
        land = landGroup.GetOutlinedPathGeometry(); land.Freeze();
        roads = roadGroup.GetOutlinedPathGeometry(); roads.Freeze();
    }

    private Rect Cells(MapSection section) => new(origin.X + section.X * cell, origin.Y + section.Y * cell, section.Width * cell, section.Height * cell);
    private Point Center(MapSection section) { var r = Cells(section); return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawRoundedRectangle(Sea, null, bounds, 14, 14);
        hits.Clear();
        if (Model.Atlas.Sections.Count == 0)
        {
            Text(drawing, "La carte s’affichera avec un serveur compatible.", new Point(24, 24), 13, MutedBrush, false);
            return;
        }
        Layout();
        for (int x = 0; x <= Model.Atlas.Width; x++)
            for (int y = 0; y <= Model.Atlas.Height; y++)
                drawing.DrawEllipse(GridDot, null, new Point(origin.X + x * cell, origin.Y + y * cell), 1, 1);
        drawing.DrawGeometry(null, Shelf, land);
        drawing.DrawGeometry(LandFill, Coast, land);
        drawing.DrawGeometry(RoadBrush, null, roads);
        foreach (var section in Model.Atlas.Sections.Where(section => section.Kind == "route"))
        {
            var r = Cells(section); var c = Center(section);
            var dash = new Pen(RoadLine, 1) { DashStyle = new DashStyle([3, 4], 0) };
            if (section.Width >= section.Height) drawing.DrawLine(dash, new Point(r.Left + cell * 0.35, c.Y), new Point(r.Right - cell * 0.35, c.Y));
            else drawing.DrawLine(dash, new Point(c.X, r.Top + cell * 0.35), new Point(c.X, r.Bottom - cell * 0.35));
        }
        var occupied = Model.Groups().Select(group => group.Section.Id).ToHashSet();
        foreach (var section in Model.Atlas.Sections.Where(section => section.Kind == "area"))
        {
            var c = Center(section); double s = Math.Max(4, cell * 0.17);
            var corner = new Point(c.X + cell * 0.3, c.Y - cell * 0.3);
            var diamond = new StreamGeometry();
            using (var g = diamond.Open()) { g.BeginFigure(new Point(corner.X, corner.Y - s), true, true); g.PolyLineTo([new Point(corner.X + s, corner.Y), new Point(corner.X, corner.Y + s), new Point(corner.X - s, corner.Y)], true, true); }
            diamond.Freeze();
            drawing.DrawGeometry(occupied.Contains(section.Id) ? Violet : MapPalette.SoftBrush(1), new Pen(Violet, 1), diamond);
        }
        foreach (var section in Model.Atlas.Sections.Where(section => section.Kind == "city"))
        {
            var r = Cells(section); double inset = cell * 0.16;
            var tile = new Rect(r.X + inset, r.Y + inset, r.Width - 2 * inset, r.Height - 2 * inset);
            bool lit = occupied.Contains(section.Id);
            if (lit) drawing.DrawRoundedRectangle(null, new Pen(MapPalette.SoftBrush(0), 6), tile, cell * 0.2, cell * 0.2);
            drawing.DrawRoundedRectangle(TownFill, new Pen(lit ? Cyan : RoadLine, lit ? 1.6 : 1), tile, cell * 0.2, cell * 0.2);
            var label = Format(section.Name, Math.Clamp(cell * 0.3, 9, 11.5), lit ? TextBrush : MutedBrush, lit);
            label.MaxTextWidth = Math.Max(40, cell * 3.2); label.MaxLineCount = 2; label.TextAlignment = TextAlignment.Center; label.Trimming = TextTrimming.CharacterEllipsis;
            // An occupied town carries a pin: write its name under the pin.
            drawing.DrawText(label, new Point(r.X + r.Width / 2 - label.MaxTextWidth / 2, r.Bottom + (lit ? Math.Max(2, 25 - cell / 2) : 1)));
        }
        if (hovered is { } focus)
        {
            var r = Cells(focus);
            drawing.DrawRoundedRectangle(MapPalette.SoftBrush(0), new Pen(Cyan, 1.4), Rect.Inflate(r, 2, 2), 6, 6);
        }

        double phase = Phase;
        var anchors = new List<Point>();
        foreach (var group in Model.Groups())
        {
            Point anchor = Center(group.Section);
            // Sections can share a cell (a cave in its town): nudge the later pin.
            while (anchors.Any(other => (other - anchor).Length < 18)) anchor.Offset(14, -12);
            anchors.Add(anchor);
            bool open = group.Players.Count > 1 && group.Section.Id == ExpandedSection;
            if (open && group.Players.Count <= 8) Fan(drawing, group, anchor, phase);
            else if (group.Players.Count == 1) Pin(drawing, anchor, group.Players[0], group, phase, true);
            else Crowd(drawing, anchor, group, phase, open);
        }
        if (hovered is { } place && Model.Groups().All(group => group.Section.Id != place.Id || group.Players.Count == 0)) Pill(drawing, place.Name, new Point(hoverPoint.X + 14, hoverPoint.Y - 30), MutedBrush);
    }

    private void Pin(DrawingContext drawing, Point point, MapPlayer player, MapGroup group, double phase, bool label)
    {
        bool selected = player.Id == SelectedId;
        var tone = MapPalette.Brush(player.RoomTone);
        drawing.PushOpacity(player.IsStale ? 0.42 : 1);
        if (!player.IsStale)
        {
            double wave = (phase * 0.7 + player.Id.Length * 0.13) % 1;
            drawing.PushOpacity(0.5 * (1 - wave));
            drawing.DrawEllipse(null, new Pen(tone, 2), point, 11 + wave * 12, 11 + wave * 12);
            drawing.Pop();
        }
        if (selected) drawing.DrawEllipse(null, new Pen(Gold, 2.5), point, 16, 16);
        drawing.DrawEllipse(tone, InkPen, point, 11, 11);
        if (player.IsStale) drawing.DrawEllipse(null, new Pen(TextBrush, 1) { DashStyle = DashStyles.Dot }, point, 13.5, 13.5);
        var initial = Format(player.Initial, 11.5, Ink, true);
        drawing.DrawText(initial, new Point(point.X - initial.Width / 2, point.Y - initial.Height / 2));
        drawing.Pop();
        if (label && selected) Pill(drawing, player.Name, new Point(point.X + 18, point.Y - 11), TextBrush);
        hits.Add((point, 15, group, player));
    }

    private void Crowd(DrawingContext drawing, Point point, MapGroup group, double phase, bool open)
    {
        bool selected = group.Players.Any(player => player.Id == SelectedId);
        bool stale = group.Players.All(player => player.IsStale);
        double radius = 15 + Math.Min(6, group.Players.Count * 0.5);
        drawing.PushOpacity(stale ? 0.45 : 1);
        double wave = phase * 0.55 % 1;
        drawing.PushOpacity(0.45 * (1 - wave));
        drawing.DrawEllipse(null, new Pen(Cyan, 2), point, radius + wave * 16, radius + wave * 16);
        drawing.Pop();
        // Ring split by room, so a mixed group shows who is gathered.
        double start = -Math.PI / 2;
        foreach (var share in group.Players.GroupBy(player => player.RoomTone).OrderBy(share => share.Key))
        {
            double sweep = 2 * Math.PI * share.Count() / group.Players.Count;
            Arc(drawing, point, radius + 4, start + 0.04, start + sweep - 0.04, MapPalette.Brush(share.Key));
            start += sweep;
        }
        if (selected || open) drawing.DrawEllipse(null, new Pen(open ? TextBrush : Gold, 2), point, radius + 8, radius + 8);
        drawing.DrawEllipse(CrowdFill, InkPen, point, radius, radius);
        var count = Format(group.Players.Count.ToString(French), 13, Ink, true);
        drawing.DrawText(count, new Point(point.X - count.Width / 2, point.Y - count.Height / 2));
        drawing.Pop();
        hits.Add((point, radius + 4, group, null));
    }

    private void Fan(DrawingContext drawing, MapGroup group, Point anchor, double phase)
    {
        double progress = FanProgress;
        var fan = PlayerMapModel.Fan(anchor.X, anchor.Y, group.Players.Count, ActualWidth, ActualHeight, 30);
        var center = fan.Count > 0 ? new Point(fan.Average(p => p.X), fan.Average(p => p.Y)) : anchor;
        drawing.DrawEllipse(MapPalette.SoftBrush(0), new Pen(Cyan, 1) { DashStyle = DashStyles.Dash }, anchor, 7, 7);
        for (int index = 0; index < fan.Count; index++)
        {
            var target = new Point(fan[index].X, fan[index].Y);
            var point = new Point(anchor.X + (target.X - anchor.X) * progress, anchor.Y + (target.Y - anchor.Y) * progress);
            var player = group.Players[index];
            drawing.DrawLine(new Pen(MapPalette.Brush(player.RoomTone), 1.2) { DashStyle = new DashStyle([2, 3], 0) }, anchor, point);
            Pin(drawing, point, player, group, phase, false);
            if (progress > 0.85)
            {
                bool right = target.X >= center.X - 1;
                var name = Format(player.Name, 11, player.Id == SelectedId ? Gold : TextBrush, player.Id == SelectedId);
                name.MaxTextWidth = 150; name.MaxLineCount = 1; name.Trimming = TextTrimming.CharacterEllipsis;
                double width = Math.Min(150, name.Width) + 14;
                var at = right ? new Point(point.X + 16, point.Y - 10) : new Point(point.X - 16 - width, point.Y - 10);
                at.X = Math.Clamp(at.X, 4, Math.Max(4, ActualWidth - width - 4));
                drawing.PushOpacity((progress - 0.85) / 0.15);
                drawing.DrawRoundedRectangle(PillBrush, PillPen, new Rect(at.X, at.Y, width, 20), 10, 10);
                drawing.DrawText(name, new Point(at.X + 7, at.Y + 10 - name.Height / 2));
                drawing.Pop();
            }
        }
        if (progress < 1) frames.Start();
    }

    private static void Arc(DrawingContext drawing, Point center, double radius, double from, double to, Brush brush)
    {
        if (to <= from) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + radius * Math.Cos(from), center.Y + radius * Math.Sin(from)), false, false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(to), center.Y + radius * Math.Sin(to)), new Size(radius, radius), 0, to - from > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }

    private void Pill(DrawingContext drawing, string value, Point at, Brush foreground)
    {
        var text = Format(value, 11.5, foreground, true);
        text.MaxTextWidth = 220; text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis;
        double width = Math.Min(220, text.Width) + 16;
        at.X = Math.Clamp(at.X, 4, Math.Max(4, ActualWidth - width - 4)); at.Y = Math.Clamp(at.Y, 4, Math.Max(4, ActualHeight - 26));
        drawing.DrawRoundedRectangle(PillBrush, PillPen, new Rect(at.X, at.Y, width, 22), 11, 11);
        drawing.DrawText(text, new Point(at.X + 8, at.Y + 11 - text.Height / 2));
    }

    private FormattedText Format(string value, double size, Brush brush, bool bold)
        => new(value, French, FlowDirection.LeftToRight, bold ? UiBold : Ui, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void Text(DrawingContext drawing, string value, Point point, double size, Brush brush, bool bold) => drawing.DrawText(Format(value, size, brush, bold), point);

    private (Point Point, double Radius, MapGroup Group, MapPlayer? Player) Hit(Point point) => hits.LastOrDefault(hit => (hit.Point - point).Length <= hit.Radius);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hit = Hit(point);
        var section = cell > 0 ? Model.Atlas.Sections.Where(section => Rect.Inflate(Cells(section), 2, 2).Contains(point)).OrderBy(section => section.Width * section.Height).FirstOrDefault() : null;
        if (!ReferenceEquals(section, hovered) || section is not null) { hovered = section; hoverPoint = point; InvalidateVisual(); }
        if (hit.Group is null) { Cursor = Cursors.Arrow; ToolTip = null; return; }
        Cursor = Cursors.Hand;
        ToolTip = hit.Player?.Detail ?? $"{hit.Group.Section.Name}\n{hit.Group.Players.Count} joueurs — cliquez pour les voir";
        if (hit.Player is null && ExpandedSection != hit.Group.Section.Id) Expand(hit.Group);
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); hovered = null; InvalidateVisual(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = Hit(e.GetPosition(this));
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
        if (ExpandedSection != group.Section.Id) fanStarted = DateTime.UtcNow;
        ExpandedSection = group.Section.Id;
        if (group.Players.Count > 8) CrowdRequested?.Invoke(group);
        else CrowdClosed?.Invoke();
        frames.Start();
        InvalidateVisual();
    }
    internal void Collapse() { ExpandedSection = null; CrowdClosed?.Invoke(); InvalidateVisual(); }
    /// <summary>Finishes the fan animation at once, for captures.</summary>
    internal void Settle() => fanStarted = DateTime.MinValue;
}
