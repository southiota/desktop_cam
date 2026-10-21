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
/// Inside 이면 녹화 영역 자체를 설명한다: 뚫지 않고 둘레에 점선만 두르며, 상자가 영역 안에 있으면 화살표가 없다.
/// </summary>
public record TourItem(Func<Rect?> Target, string Title, string Body, string Keys = "", bool Inside = false);

/// <summary>
/// 떠 있는 창 위에 사용법을 한꺼번에 보여준다. 설명할 칸마다 짝 색(번호 ①②…)의 굵은 네모를 두르고,
/// 녹화 영역 안 빈 곳에 놓은 같은 색 설명 상자에서 그 네모까지 화살표를 긋는다.
/// 상자는 서로 · 화살표 · 다른 칸과 겹치지 않는 자리를 찾아 놓고, 영역 안에 자리가 없을 때만 영역 밖에 둔다.
/// 크기는 패널과 같은 기준(실제 픽셀 × 패널 크기 %)이라 Windows 배율과 상관없이 같은 모습. 아무 곳이나 누르거나 Esc 로 닫는다.
/// 녹화에는 찍히지 않는다.
/// </summary>
public partial class TourWindow : Window
{
    // 아래 값은 모두 디자인 픽셀 (패널 100% 기준). 화면에는 × _k (DIP) 로 그린다.
    const double NoteW = 560;      // 설명 상자 폭
    const double TitleSize = 28, BodySize = 24, BodyLine = 38, KeySize = 20;   // 예전(14 · 12 · 18 · 11)의 두 배
    const double NoteGap = 36;     // 상자 사이
    const double AreaPad = 40;     // 녹화 영역 가장자리에서 안쪽으로
    const double ScreenPad = 16;   // 화면 가장자리
    const double Pad = 5;          // 칸 둘레 여유 (뚫는 곳 · 네모)
    const double MarkW = 4;        // 네모 테두리 굵기
    const double LineW = 4;        // 화살표 굵기
    const double Step = 8;         // 자리 찾기 간격

    /// <summary>짝 색: 어두운 바탕에서 서로 잘 구분되는 색. 테마와 상관없이 같다.</summary>
    static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0xD2, 0x3F),   // 노랑
        Color.FromRgb(0x3B, 0xCE, 0xAC),   // 청록
        Color.FromRgb(0xFF, 0x84, 0x60),   // 주황
        Color.FromRgb(0x7D, 0xAE, 0xE8),   // 파랑
        Color.FromRgb(0xC7, 0x92, 0xEA),   // 보라
        Color.FromRgb(0x9C, 0xDB, 0x5A),   // 연두
        Color.FromRgb(0xFF, 0x7E, 0xB9),   // 분홍
        Color.FromRgb(0x5F, 0xD7, 0xFF),   // 하늘
        Color.FromRgb(0xF2, 0xA5, 0x41),   // 귤
    };

    readonly IReadOnlyList<TourItem> _items;
    readonly Func<Rect?> _panel;
    readonly Func<double> _zoom;   // 패널 크기 (1 = 100%)
    Native.RECT _monitor;
    double _scale = 1;   // DIP → 실제 픽셀
    double _k = 1;       // 디자인 픽셀 → DIP
    bool _panelRight = true;   // 패널이 녹화 영역 오른쪽에 있음
    string _lastKey = "";
    readonly System.Windows.Threading.DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public TourWindow(IReadOnlyList<TourItem> items, Func<Rect?> panel, Func<double> zoom)
    {
        _items = items;
        _panel = panel;
        _zoom = zoom;
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

    string StateKey() => string.Join("|", _items.Select(i => i.Target()?.ToString() ?? "-")) + _panel() + _zoom();

    double U(double v) => v * _k;

    /// <summary>뚫을 곳 · 네모 · 설명 상자 · 화살표를 한 번에 그린다.</summary>
    void Build()
    {
        _lastKey = StateKey();
        _k = Math.Clamp(_zoom(), 0.5, 2) / _scale;
        Marks.Children.Clear();
        Notes.Children.Clear();
        var full = new Rect(0, 0, Width, Height);


        // 짝 번호 · 색은 목록 순서대로 (보이는 칸만 세므로 번호가 건너뛰지 않음)
        var targets = new List<(TourItem item, Rect rect, Rect hole, int no, Brush color)>();
        foreach (var i in _items)
        {
            if (i.Target() is not Rect r) continue;
            var local = ToLocal(r);
            var c = new SolidColorBrush(Palette[targets.Count % Palette.Length]);
            c.Freeze();
            targets.Add((i, local, i.Inside ? Rect.Inflate(local, U(6), U(6)) : Rect.Inflate(local, U(Pad), U(Pad)), targets.Count + 1, c));
        }

        // 어둡게 덮고, 영역 안쪽에 설명하는 큰 곳을 빼고는 모두 뚫는다
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(full));
        foreach (var t in targets.Where(t => !t.item.Inside))
            dim.Children.Add(new RectangleGeometry(t.hole));
        Dim.Data = dim;

        // 상자를 놓을 곳: 녹화 영역 안쪽. 상자가 가리면 안 되는 것: 뚫은 칸 · 패널 · 닫는 법 안내.
        // 화살표가 지나가면 안 되는 것: 다른 칸 (패널 바탕은 지나가도 됨)
        var region = targets.FirstOrDefault(t => t.item.Inside).rect;
        var inner = region.IsEmpty ? Rect.Empty : Rect.Inflate(region, -U(AreaPad), -U(AreaPad));
        var screen = new Rect(U(ScreenPad), U(ScreenPad), Math.Max(0, full.Width - 2 * U(ScreenPad)), Math.Max(0, full.Height - 2 * U(ScreenPad)));
        var holes = targets.Where(t => !t.item.Inside).Select(t => t.hole).ToList();
        var panelRect = _panel() is Rect p ? Rect.Inflate(ToLocal(p), U(Pad), U(Pad)) : Rect.Empty;
        _panelRight = panelRect.IsEmpty || region.IsEmpty || panelRect.X + panelRect.Width / 2 >= region.X + region.Width / 2;

        // 닫는 법: 맨 위 가운데 (설명 상자와 같은 비율로 조금 크게). 패널 · 칸을 가리면 맨 아래 가운데로
        Hint.LayoutTransform = new ScaleTransform(1.5 * _k, 1.5 * _k);
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var hs = Hint.DesiredSize;
        var hintRect = new Rect((full.Width - hs.Width) / 2, U(ScreenPad), hs.Width, hs.Height);
        bool Covers(Rect r) => (!panelRect.IsEmpty && panelRect.IntersectsWith(r)) || holes.Any(h => h.IntersectsWith(r));
        if (Covers(hintRect))
        {
            var low = new Rect(hintRect.X, full.Height - U(ScreenPad) - hs.Height, hs.Width, hs.Height);
            if (!Covers(low)) hintRect = low;
        }
        Canvas.SetLeft(Hint, hintRect.X);
        Canvas.SetTop(Hint, hintRect.Y);

        var blocked = new List<Rect>(holes) { hintRect };
        var lineBlocks = new List<Rect>(holes) { hintRect };   // 화살표가 지나가면 안 되는 것
        if (!panelRect.IsEmpty) blocked.Add(panelRect);

        var placed = new List<Rect>();
        var lines = new List<(Point a, Point b)>();

        // 패널 칸은 위에서부터, 녹화 영역 설명은 마지막에 (남는 자리 아무 데나)
        foreach (var t in targets.OrderBy(t => t.item.Inside ? 1 : 0).ThenBy(t => t.rect.Y + t.rect.Height / 2))
        {
            if (t.item.Inside) MarkRegion(t.rect, t.color);
            else Mark(t.hole, t.color);

            // 빈 곳이 없으면 상자를 조금씩 줄여 다시 찾는다. 그래도 없으면 상자는 빼고 네모만 (상자끼리 겹쳐 글을 가리지 않게)
            FrameworkElement? note = null;
            (Rect spot, (Point a, Point b)? link)? found = null;
            foreach (double f in new[] { 1.0, 0.8, 0.65 })
            {
                note = MakeNote(t.item, t.no, t.color, f);
                found = FindSpot(note.DesiredSize, t.hole, t.item.Inside, inner, region, screen, blocked, lineBlocks, placed, lines);
                if (found != null) break;
            }
            if (found is not { } got || note == null) continue;
            var (spot, link) = got;
            placed.Add(spot);
            Place(note, spot.X, spot.Y);
            if (link is (Point a, Point b))
            {
                lines.Add((a, b));
                Arrow(a, b, t.color);
            }
        }
    }

    /// <summary>
    /// 상자 자리 찾기. 차례로 느슨하게: ① 녹화 영역 안 (상자 · 화살표 · 칸과 안 겹침) ② 화면 어디든 같은 조건
    /// ③ 화살표가 다른 상자를 지나가도 됨 ④ 패널 · 안내 줄 위도 됨 (칸 · 다른 상자와는 끝까지 안 겹침). 없으면 null.
    /// 같은 단계 안에서는 가리킬 칸과 높이가 가깝고 거리가 가까운 자리를 고른다 (화살표가 짧고 눕게).
    /// </summary>
    (Rect spot, (Point a, Point b)? link)? FindSpot(Size size, Rect hole, bool inside, Rect inner, Rect region, Rect screen,
                  List<Rect> blocked, List<Rect> holes, List<Rect> placed, List<(Point a, Point b)> lines)
    {
        double gap = U(NoteGap);
        (Rect, (Point a, Point b)?)? best = null;
        // 화살표가 다른 상자 · 다른 칸을 몇 개 지나가는지
        int Hits(Point a, Point b) => placed.Count(q => Crosses(a, b, Rect.Inflate(q, U(14), U(14))))
                                    + holes.Count(q => q != hole && Crosses(a, b, Rect.Inflate(q, U(4), U(4))));   // 붙은 칸 사이 좁은 틈은 막힌 것으로

        for (int pass = 0; pass < 4 && best == null; pass++)
        {
            var area = pass == 0 ? inner : screen;
            if (area.IsEmpty || area.Width < size.Width || area.Height < size.Height) continue;
            double bestCost = double.MaxValue;
            double step = U(Step);

            // 영역 안: 가리킬 칸 쪽 가장자리에 붙인 세로 줄(열)들 위에서만 찾는다 (가지런하게). 화면: 촘촘히.
            var xs = new List<double>();
            if (pass == 0)
            {
                // 열은 패널 쪽 가장자리부터 (모든 상자가 같은 열을 써서 가지런하게)
                bool fromRight = _panelRight;
                for (double off = 0; off + size.Width <= area.Width + 0.5; off += size.Width + gap)
                    xs.Add(fromRight ? area.Right - size.Width - off : area.X + off);
            }
            else
                for (double x = area.X; x + size.Width <= area.Right + 0.5; x += step * 4) xs.Add(x);

            foreach (double x in xs)
                for (double y = area.Y; y + size.Height <= area.Bottom + 0.5; y += pass == 0 ? step : step * 2)
                {
                    var r = new Rect(x, y, size.Width, size.Height);
                    var roomy = Rect.Inflate(r, gap / 2, gap / 2);
                    if (placed.Any(q => Rect.Inflate(q, gap / 2, gap / 2).IntersectsWith(roomy))) continue;
                    if ((pass < 3 ? blocked : holes).Any(q => q.IntersectsWith(r)) || (!inside && r.IntersectsWith(hole))) continue;

                    // 싼 것부터: 높이 차이 · 영역 밖 벌점만으로도 지금 최선보다 나쁘면 화살표는 따져 보지 않는다
                    double cost;
                    if (inside) cost = (y - area.Y) + (x - area.X) * 0.25;   // 녹화 영역 설명: 왼쪽 위부터
                    else
                    {
                        cost = Math.Abs(y + size.Height / 2 - (hole.Y + hole.Height / 2));
                        if (!region.IsEmpty && !region.Contains(r)) cost += U(4000);   // 영역 밖은 마지막 수단
                    }
                    if (cost >= bestCost) continue;

                    // 이미 그은 화살표가 이 상자를 지나가거나, 이 상자의 화살표가 다른 상자 · 칸을 지나가면 안 됨 (③ 부터는 적을수록 좋음)
                    var pad = Rect.Inflate(r, U(14), U(14));
                    int crossed = lines.Count(l => Crosses(l.a, l.b, pad));
                    if (pass < 2 && crossed > 0) continue;
                    var link = Link(r, hole, (a, b) => Hits(a, b) == 0);
                    int hits = link is (Point la, Point lb) ? Hits(la, lb) : 0;
                    if (pass < 2 && hits > 0) continue;
                    if (link is (Point a2, Point b2)) cost += (b2 - a2).Length * 0.5;
                    cost += (crossed + hits) * U(3000);
                    if (cost < bestCost) { bestCost = cost; best = (r, link); }
                }
        }
        return best;
    }

    /// <summary>
    /// 상자 가장자리 → 칸 가장자리. 칸이 옆에 있으면 옆면끼리 (높이를 맞춰 눕게), 위 · 아래면 윗면 · 아랫면끼리.
    /// 먼저 칸 가운데를 겨누고, ok 를 못 넘으면 상자와 칸이 겹치는 높이(또는 폭) 안에서 곧은 줄을 차례로 찾아본다.
    /// </summary>
    (Point a, Point b)? Link(Rect note, Rect hole, Func<Point, Point, bool>? ok = null)
    {
        var first = LinkAt(note, hole, null);
        if (first is not (Point fa, Point fb) || ok == null || ok(fa, fb)) return first;
        bool side = hole.Left >= note.Right + U(12) || hole.Right <= note.Left - U(12);
        double lo = side ? Math.Max(note.Top, hole.Top) + U(22) : Math.Max(note.Left, hole.Left) + U(22);
        double hi = side ? Math.Min(note.Bottom, hole.Bottom) - U(22) : Math.Min(note.Right, hole.Right) - U(22);
        for (double v = lo; v <= hi; v += U(Step) * 2)
            if (LinkAt(note, hole, v) is (Point a, Point b) && ok(a, b)) return (a, b);
        return first;
    }

    (Point a, Point b)? LinkAt(Rect note, Rect hole, double? at)
    {
        double inN = U(22), inH = Math.Min(U(8), hole.Height / 2), inW = Math.Min(U(8), hole.Width / 2);
        Point hc = new(hole.X + hole.Width / 2, hole.Y + hole.Height / 2);
        if (hole.Left >= note.Right + U(12) || hole.Right <= note.Left - U(12))
        {
            bool right = hole.Left >= note.Right;
            double ay = Math.Clamp(at ?? hc.Y, note.Top + inN, note.Bottom - inN);
            var a = new Point(right ? note.Right : note.Left, ay);
            var b = new Point(right ? hole.Left : hole.Right, Math.Clamp(ay, hole.Top + inH, hole.Bottom - inH));
            return (a, b);
        }
        if (hole.Top >= note.Bottom + U(12) || hole.Bottom <= note.Top - U(12))
        {
            bool down = hole.Top >= note.Bottom;
            double ax = Math.Clamp(at ?? hc.X, note.Left + inN, note.Right - inN);
            var a = new Point(ax, down ? note.Bottom : note.Top);
            var b = new Point(Math.Clamp(ax, hole.Left + inW, hole.Right - inW), down ? hole.Top : hole.Bottom);
            return (a, b);
        }
        return null;   // 겹치거나 맞닿음 (녹화 영역 안의 상자 등)
    }

    /// <summary>선분 a-b 가 네모 r 을 지나는지.</summary>
    static bool Crosses(Point a, Point b, Rect r)
    {
        if (r.IsEmpty) return false;
        if (r.Contains(a) || r.Contains(b)) return true;
        var c1 = r.TopLeft; var c2 = r.TopRight; var c3 = r.BottomRight; var c4 = r.BottomLeft;
        return SegHit(a, b, c1, c2) || SegHit(a, b, c2, c3) || SegHit(a, b, c3, c4) || SegHit(a, b, c4, c1);
    }

    static bool SegHit(Point p1, Point p2, Point q1, Point q2)
    {
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>설명 상자. f 는 자리가 모자랄 때 줄이는 비율 (1 = 원래 크기).</summary>
    FrameworkElement MakeNote(TourItem item, int no, Brush color, double f = 1)
    {
        var onAccent = (Brush)FindResource("OnAccent");
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 번호 딱지: 짝 색 면 + 어두운 숫자
        var badge = new Border
        {
            Background = color, Width = U(40), Height = U(40), Margin = new Thickness(0, 0, U(14), 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = no.ToString(), FontSize = U(24), FontWeight = FontWeights.Bold, Foreground = onAccent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        head.Children.Add(badge);
        var title = new TextBlock
        {
            Text = item.Title, FontSize = U(TitleSize), FontWeight = FontWeights.Bold, Foreground = color,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        if (item.Keys != "")
        {
            var keys = new TextBlock
            {
                Text = item.Keys, FontFamily = (FontFamily)FindResource("CodeFont"), FontSize = U(KeySize),
                Foreground = (Brush)FindResource("Cream"), Opacity = 0.8, Margin = new Thickness(U(12), 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(keys, 2);
            head.Children.Add(keys);
        }

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = item.Body, FontSize = U(BodySize), LineHeight = U(BodyLine), TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(0, U(14), 0, 0),
        });

        double w = U(NoteW);
        var note = new Grid { Width = w };
        note.Children.Add(new Border { Background = (Brush)FindResource("Bg"), Opacity = 0.7, Margin = new Thickness(U(6), U(6), -U(6), -U(6)) });
        note.Children.Add(new Border
        {
            Background = (Brush)FindResource("BgPanel"), BorderBrush = color,
            BorderThickness = new Thickness(U(3)), Padding = new Thickness(U(28), U(22), U(28), U(26)), Child = body,
        });
        if (f < 1) note.LayoutTransform = new ScaleTransform(f, f);
        note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return note;
    }

    void Place(FrameworkElement note, double x, double y)
    {
        Canvas.SetLeft(note, x);
        Canvas.SetTop(note, y);
        Notes.Children.Add(note);
    }

    /// <summary>칸 둘레 굵은 네모 (짝 색). 테두리는 뚫은 곳 안쪽 여유에 그려 칸 내용을 가리지 않는다.</summary>
    void Mark(Rect hole, Brush color)
    {
        var box = new Rectangle { Width = hole.Width, Height = hole.Height, Stroke = color, StrokeThickness = U(MarkW) };
        Canvas.SetLeft(box, hole.X);
        Canvas.SetTop(box, hole.Y);
        Marks.Children.Add(box);
    }

    /// <summary>녹화 영역 둘레 점선 (짝 색).</summary>
    void MarkRegion(Rect r, Brush color)
    {
        var box = new Rectangle
        {
            Width = r.Width + 12, Height = r.Height + 12, Stroke = color,
            StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 3, 2 },
        };
        Canvas.SetLeft(box, r.X - 6);
        Canvas.SetTop(box, r.Y - 6);
        Marks.Children.Add(box);
    }

    /// <summary>a → b 화살표: 어두운 굵은 선 위에 짝 색 선, 끝에 삼각형 화살촉, 시작에 작은 네모.</summary>
    void Arrow(Point a, Point b, Brush color)
    {
        var d = b - a;
        if (d.Length < U(20)) return;
        d.Normalize();
        var n = new Vector(-d.Y, d.X);
        double head = U(20), half = U(11);
        Point baseP = b - d * head;
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, baseP));
        var tri = new StreamGeometry();
        using (var c = tri.Open())
        {
            c.BeginFigure(b, true, true);
            c.LineTo(baseP + n * half, true, false);
            c.LineTo(baseP - n * half, true, false);
        }
        tri.Freeze();
        g.Children.Add(tri);
        g.Children.Add(new RectangleGeometry(new Rect(a.X - U(5), a.Y - U(5), U(10), U(10))));
        g.Freeze();
        var bg = (Brush)FindResource("Bg");
        Marks.Children.Add(new Path { Data = g, Stroke = bg, StrokeThickness = U(LineW + 5), Fill = bg, StrokeLineJoin = PenLineJoin.Round });
        Marks.Children.Add(new Path { Data = g, Stroke = color, StrokeThickness = U(LineW), Fill = color, StrokeLineJoin = PenLineJoin.Round });
    }
}
