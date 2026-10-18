using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 사용법 설명 하나: 가리킬 곳(실제 픽셀, 화면 좌표, 매번 새로 잼), 제목, 짧은 설명, 단축키.
/// Inside 이면 가리킬 곳 안쪽 왼쪽 위에 상자를 둔다 (녹화 영역처럼 아주 큰 곳).
/// Beside 이면 줄에 넣지 않고 가리킬 곳 바로 왼쪽에 둔다 (줄과 겹치는 아래쪽 작은 버튼).
/// </summary>
public record TourItem(Func<Rect?> Target, string Title, string Body, string Keys = "", bool Inside = false, bool Beside = false);

/// <summary>
/// 떠 있는 창 위에 사용법을 한꺼번에 보여준다. 모든 칸을 밝게 뚫어 노란 테두리를 두르고,
/// 패널 옆 한 줄로 늘어선 설명 상자에서 각 칸을 화살표로 가리킨다. 아무 곳이나 누르거나 Esc 로 닫는다.
/// 녹화에는 찍히지 않는다.
/// </summary>
public partial class TourWindow : Window
{
    const double NoteWidth = 300, Gap = 64, Spacing = 10, Edge = 16, Pad = 5;

    readonly IReadOnlyList<TourItem> _items;
    readonly Func<Rect?> _panel;
    Native.RECT _monitor;
    double _scale = 1;
    string _lastKey = "";
    readonly System.Windows.Threading.DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public TourWindow(IReadOnlyList<TourItem> items, Func<Rect?> panel)
    {
        _items = items;
        _panel = panel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) => { FitToMonitor(); Build(); Focus(); _follow.Start(); ClipCheck.Run(this, "사용법"); };
        Closed += (_, _) => _follow.Stop();
        // 알림이 생기는 등으로 패널 안 칸이 움직이면 다시 그린다
        _follow.Tick += (_, _) => { if (StateKey() != _lastKey) Build(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    void OnClick(object sender, MouseButtonEventArgs e) => Close();

    void FitToMonitor()
    {
        _monitor = GetPrimaryMonitor().monitor;
        _scale = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        Left = _monitor.Left / _scale;
        Top = _monitor.Top / _scale;
        Width = _monitor.Width / _scale;
        Height = _monitor.Height / _scale;
    }

    Rect ToLocal(Rect px) => new((px.X - _monitor.Left) / _scale, (px.Y - _monitor.Top) / _scale, px.Width / _scale, px.Height / _scale);

    string StateKey() => string.Join("|", _items.Select(i => i.Target()?.ToString() ?? "-")) + _panel();

    /// <summary>뚫을 곳 · 테두리 · 설명 상자 · 화살표를 한 번에 그린다.</summary>
    void Build()
    {
        _lastKey = StateKey();
        Marks.Children.Clear();
        Notes.Children.Clear();
        var full = new Rect(0, 0, Width, Height);

        // 닫는 법: 맨 위 가운데
        Hint.UpdateLayout();
        Canvas.SetLeft(Hint, (full.Width - Hint.ActualWidth) / 2);
        Canvas.SetTop(Hint, Edge);

        var targets = _items.Select(i => (item: i, rect: i.Target() is Rect r ? ToLocal(r) : (Rect?)null))
                            .Where(t => t.rect != null).Select(t => (t.item, rect: t.rect!.Value)).ToList();

        // 어둡게 덮고, 영역 안쪽에 설명하는 큰 곳을 빼고는 모두 뚫는다
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(full));
        foreach (var (_, r) in targets.Where(t => !t.item.Inside))
            dim.Children.Add(new RectangleGeometry(Rect.Inflate(r, Pad, Pad)));
        Dim.Data = dim;

        // 설명 상자 줄: 패널이 화면 오른쪽 쪽에 있으면 패널 왼쪽, 아니면 오른쪽
        var panel = _panel() is Rect p ? ToLocal(p) : full;
        bool left = panel.X > full.Width / 3;
        double colX = left ? panel.X - Gap - NoteWidth : panel.Right + Gap;

        var notes = targets.Where(t => !t.item.Inside && !t.item.Beside).OrderBy(t => t.rect.Y + t.rect.Height / 2)
                           .Select(t => (t.item, t.rect, note: MakeNote(t.item))).ToList();
        // 세로 자리: 가리킬 곳 높이에 맞추되 겹치면 아래로 밀고, 화면 밖으로 나가면 전체를 위로
        double y = Edge + 44;
        var ys = new List<double>();
        foreach (var (_, r, note) in notes)
        {
            double at = Math.Max(r.Y + r.Height / 2 - note.DesiredSize.Height / 2, y);
            ys.Add(at);
            y = at + note.DesiredSize.Height + Spacing;
        }
        double overflow = y - Spacing - (full.Height - Edge);
        if (overflow > 0) for (int i = 0; i < ys.Count; i++) ys[i] = Math.Max(Edge + 44, ys[i] - overflow);

        for (int i = 0; i < notes.Count; i++)
        {
            var (_, r, note) = notes[i];
            Place(note, colX, ys[i]);
            Mark(r);
            Arrow(new Rect(colX, ys[i], note.DesiredSize.Width, note.DesiredSize.Height), Rect.Inflate(r, Pad, Pad));
        }

        // 줄과 겹치는 작은 곳(사용법 버튼): 바로 왼쪽에, 세로 가운데 맞춤
        foreach (var (item, r) in targets.Where(t => t.item.Beside))
        {
            var note = MakeNote(item);
            double nx = Math.Max(Edge, r.X - Gap - note.DesiredSize.Width);
            double ny = Math.Clamp(r.Y + r.Height / 2 - note.DesiredSize.Height / 2, Edge, full.Height - note.DesiredSize.Height - Edge);
            Place(note, nx, ny);
            Mark(r);
            Arrow(new Rect(nx, ny, note.DesiredSize.Width, note.DesiredSize.Height), Rect.Inflate(r, Pad, Pad));
        }

        // 아주 큰 곳(녹화 영역): 안쪽 왼쪽 위에 상자, 둘레에 노란 점선
        foreach (var (item, r) in targets.Where(t => t.item.Inside))
        {
            Place(MakeNote(item), r.X + 28, r.Y + 28);
            var box = new Rectangle
            {
                Width = r.Width + 12, Height = r.Height + 12, Stroke = (Brush)FindResource("Yellow"),
                StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 3, 2 },
            };
            Canvas.SetLeft(box, r.X - 6);
            Canvas.SetTop(box, r.Y - 6);
            Marks.Children.Add(box);
        }
    }

    FrameworkElement MakeNote(TourItem item)
    {
        double width = item.Beside ? 200 : NoteWidth;
        var body = new StackPanel();
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = item.Title, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Yellow") });
        if (item.Keys != "")
            head.Children.Add(new TextBlock
            {
                Text = item.Keys, FontFamily = (FontFamily)FindResource("CodeFont"), FontSize = 11,
                Foreground = (Brush)FindResource("Teal"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            });
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = item.Body, FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        var note = new Grid { Width = width };
        note.Children.Add(new Border { Background = (Brush)FindResource("TealDeep"), Margin = new Thickness(4, 4, -4, -4) });
        note.Children.Add(new Border
        {
            Background = (Brush)FindResource("BgPanel"), BorderBrush = (Brush)FindResource("Yellow"),
            BorderThickness = new Thickness(2), Padding = new Thickness(12, 8, 12, 9), Child = body,
        });
        note.Measure(new Size(width, double.PositiveInfinity));
        return note;
    }

    void Place(FrameworkElement note, double x, double y)
    {
        Canvas.SetLeft(note, x);
        Canvas.SetTop(note, y);
        Notes.Children.Add(note);
    }

    void Mark(Rect r)
    {
        var hole = Rect.Inflate(r, Pad, Pad);
        var box = new Rectangle { Width = hole.Width, Height = hole.Height, Stroke = (Brush)FindResource("Yellow"), StrokeThickness = 2 };
        Canvas.SetLeft(box, hole.X);
        Canvas.SetTop(box, hole.Y);
        Marks.Children.Add(box);
    }

    /// <summary>설명 상자 옆면 → 가리킬 곳 가장자리. 어두운 굵은 선 위에 노란 선, 끝에 삼각형.</summary>
    void Arrow(Rect from, Rect to)
    {
        if (from.IntersectsWith(to)) return;
        Point tc = new(to.X + to.Width / 2, to.Y + to.Height / 2);
        double sy = Math.Clamp(tc.Y, from.Y + 12, from.Bottom - 12);
        Point a = new(tc.X > from.X + from.Width / 2 ? from.Right : from.X, sy);
        Point b = EdgePoint(to, a);
        var d = b - a;
        if (d.Length < 14) return;
        d.Normalize();
        b -= d * 3;
        var n = new Vector(-d.Y, d.X);
        Point baseP = b - d * 12;
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, baseP));
        var tri = new StreamGeometry();
        using (var c = tri.Open())
        {
            c.BeginFigure(b, true, true);
            c.LineTo(baseP + n * 7, true, false);
            c.LineTo(baseP - n * 7, true, false);
        }
        g.Children.Add(tri);
        g.Freeze();
        Marks.Children.Add(new Path { Data = g, Stroke = (Brush)FindResource("Bg"), StrokeThickness = 6, Fill = (Brush)FindResource("Bg") });
        Marks.Children.Add(new Path { Data = g, Stroke = (Brush)FindResource("Yellow"), StrokeThickness = 2.5, Fill = (Brush)FindResource("Yellow") });
    }

    static Point EdgePoint(Rect r, Point toward)
    {
        Point c = new(r.X + r.Width / 2, r.Y + r.Height / 2);
        var d = toward - c;
        if (Math.Abs(d.X) < 0.001 && Math.Abs(d.Y) < 0.001) return c;
        double sx = Math.Abs(d.X) > 0.001 ? (r.Width / 2) / Math.Abs(d.X) : double.MaxValue;
        double sy = Math.Abs(d.Y) > 0.001 ? (r.Height / 2) / Math.Abs(d.Y) : double.MaxValue;
        return c + d * Math.Min(sx, sy);
    }
}
