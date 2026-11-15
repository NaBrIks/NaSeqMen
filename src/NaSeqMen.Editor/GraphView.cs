// SPDX-License-Identifier: MIT
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NaSeqMen.Editor;

public sealed class NodeView
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Lane { get; set; }
    public DateTime FlashUntil { get; set; }
    public bool Failed { get; set; }
}

public sealed record EdgeView(string From, string To)
{
    public DateTime FlashUntil { get; set; }
}

public sealed class GraphView : FrameworkElement
{
    private readonly Dictionary<string, Rect> _bounds = new();
    private Point _pointer;
    private string? _wireStart;
    private string? _selected;
    private string? _helpNode;
    public List<NodeView> Nodes { get; set; } = new();
    public List<EdgeView> Edges { get; set; } = new();
    public event Action<NodeView>? NodeSelected;
    public event Action<NodeView>? NodeEditRequested;
    public event Action<string, string>? LinkRequested;
    private double _zoom = 1;
    public double Zoom { get => _zoom; set { _zoom = value; InvalidateMeasure(); InvalidateVisual(); } }

    public GraphView()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 240;
        HelpText.Attach(this, HelpText.Graph);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (Nodes.Any(n => n.FlashUntil > DateTime.UtcNow.AddMilliseconds(-150)) || Edges.Any(e => e.FlashUntil > DateTime.UtcNow.AddMilliseconds(-150))) InvalidateVisual(); };
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Nodes.GroupBy(n => n.Lane).Select(g => g.Count()).DefaultIfEmpty(1).Max();
        return new Size(800 * Zoom, Math.Max(240, rows * 70 + 70) * Zoom);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brush("#17232D"), null, new Rect(RenderSize));
        dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        var width = Math.Max(ActualWidth / Zoom, 800);
        var names = new[] { "ИСТОЧНИКИ", "СОБЫТИЯ", "УСЛОВИЯ", "ОБРАБОТКА", "ДЕЙСТВИЯ" };
        var laneWidth = width / names.Length;
        for (var lane = 0; lane < names.Length; lane++)
        {
            var x = lane * laneWidth;
            dc.DrawLine(new Pen(Brush("#30424F"), 1), new Point(x, 0), new Point(x, ActualHeight / Zoom));
            DrawText(dc, names[lane], new Point(x + 10, 9), 11, Brush("#A9BDC9"), laneWidth - 20);
        }
        dc.DrawLine(new Pen(Brush("#30424F"), 1), new Point(0, 33), new Point(width, 33));
        _bounds.Clear();
        var rows = new int[names.Length];
        foreach (var node in Nodes)
        {
            var lane = Math.Clamp(node.Lane, 0, names.Length - 1);
            _bounds[node.Id] = new Rect(lane * laneWidth + 18, 49 + rows[lane]++ * 70, laneWidth - 36, 51);
        }
        foreach (var edge in Edges)
        {
            if (!_bounds.TryGetValue(edge.From, out var from) || !_bounds.TryGetValue(edge.To, out var to)) continue;
            var active = edge.FlashUntil > DateTime.UtcNow;
            // A skipped lane may contain an unrelated node at either endpoint's row.
            // The 19-unit gap between rows provides a clear horizontal corridor;
            // vertical segments stay in the 36-unit gaps between node columns.
            var needsCorridor = to.Left - from.Right > laneWidth || to.Left <= from.Right;
            DrawWire(dc, new Point(from.Right, from.Top + from.Height / 2), new Point(to.Left, to.Top + to.Height / 2),
                active ? Brush("#77D9C0") : Brush("#748F9E"), active ? 2 : 1.3,
                needsCorridor ? from.Bottom + 9.5 : null);
        }
        foreach (var node in Nodes)
        {
            var rect = _bounds[node.Id];
            var active = node.FlashUntil > DateTime.UtcNow;
            var border = node.Id == _selected ? Brush("#7CB6E0") : active ? Brush(node.Failed ? "#F28E82" : "#77D9C0") : Brush("#4A626F");
            dc.DrawRectangle(Brush(active ? "#304B4A" : "#233542"), new Pen(border, node.Id == _selected || active ? 2 : 1), rect);
            DrawText(dc, node.Title, new Point(rect.Left + 9, rect.Top + 6), 13, Brush("#E4EBF0"), rect.Width - 18);
            DrawText(dc, node.Detail, new Point(rect.Left + 9, rect.Top + 27), 11, Brush("#ABC1CE"), rect.Width - 18);
            if (node.Lane != 0) dc.DrawEllipse(border, null, new Point(rect.Left, rect.Top + rect.Height / 2), 4, 4);
            if (node.Lane != 4) dc.DrawEllipse(border, null, new Point(rect.Right, rect.Top + rect.Height / 2), 4, 4);
        }
        if (_wireStart is not null && _bounds.TryGetValue(_wireStart, out var start))
            DrawWire(dc, new Point(start.Right, start.Top + start.Height / 2), _pointer, Brush("#8FD4F3"), 2);
        if (Nodes.Count == 0) DrawText(dc, "Нет узлов. Добавьте источник, условие или действие.", new Point(18, 55), 13, Brush("#A9BDC9"), width - 36);
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = LogicalPoint(e.GetPosition(this));
        var hit = Nodes.FirstOrDefault(n => _bounds.TryGetValue(n.Id, out var rect) && new Rect(rect.Left - 10, rect.Top, rect.Width + 20, rect.Height).Contains(point));
        if (hit is null) { _selected = null; _wireStart = null; InvalidateVisual(); return; }
        _selected = hit.Id;
        NodeSelected?.Invoke(hit);
        if (e.ClickCount == 2) { _wireStart = null; NodeEditRequested?.Invoke(hit); }
        else if (_wireStart is not null && _wireStart != hit.Id && hit.Lane != 0)
        {
            LinkRequested?.Invoke(_wireStart, hit.Id);
            _wireStart = null;
        }
        else if (hit.Lane != 4 && Math.Abs(point.X - _bounds[hit.Id].Right) < 14)
        {
            _wireStart = hit.Id;
            _pointer = point;
            CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_wireStart is not null) { _pointer = LogicalPoint(e.GetPosition(this)); InvalidateVisual(); }
        else
        {
            var point = LogicalPoint(e.GetPosition(this));
            var node = Nodes.FirstOrDefault(n => _bounds.TryGetValue(n.Id, out var bounds) && bounds.Contains(point));
            if (_helpNode != node?.Id)
            {
                _helpNode = node?.Id;
                ToolTip = node is null ? HelpText.Graph : HelpText.Node(node);
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_wireStart is null) return;
        ReleaseMouseCapture();
        var point = LogicalPoint(e.GetPosition(this));
        var hit = Nodes.FirstOrDefault(n => n.Id != _wireStart && n.Lane != 0 && _bounds.TryGetValue(n.Id, out var rect) && new Rect(rect.Left - 10, rect.Top, rect.Width + 20, rect.Height).Contains(point));
        if (hit is not null) { LinkRequested?.Invoke(_wireStart, hit.Id); _wireStart = null; }
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _wireStart = null; ReleaseMouseCapture(); InvalidateVisual(); }
        base.OnKeyDown(e);
    }

    private void DrawText(DrawingContext dc, string text, Point point, double size, Brush brush, double width)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, point);
    }

    private static void DrawWire(DrawingContext dc, Point from, Point to, Brush color, double thickness, double? corridorY = null)
    {
        var geometry = new StreamGeometry();
        var dx = Math.Max(28, Math.Abs(from.X - to.X) / 2);
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, false, false);
            if (corridorY is { } y)
            {
                context.LineTo(new Point(from.X + 10, from.Y), true, false);
                context.LineTo(new Point(from.X + 10, y), true, false);
                context.LineTo(new Point(to.X - 10, y), true, false);
                context.LineTo(new Point(to.X - 10, to.Y), true, false);
                context.LineTo(to, true, false);
            }
            else
                context.BezierTo(new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y), to, true, false);
        }
        dc.DrawGeometry(null, new Pen(color, thickness), geometry);
        dc.DrawLine(new Pen(color, thickness), new Point(to.X - 6, to.Y - 4), to);
        dc.DrawLine(new Pen(color, thickness), new Point(to.X - 6, to.Y + 4), to);
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private Point LogicalPoint(Point point) => new(point.X / Zoom, point.Y / Zoom);
}
