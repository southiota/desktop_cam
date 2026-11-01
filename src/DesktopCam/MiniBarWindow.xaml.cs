using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 패널을 닫았을 때만 뜨는 미니 바 (패널 왼쪽 위 자리). 녹화 · 대본 넘기기 · 펜 · 소리 막대.
/// 녹화에 찍히지 않고 포커스를 가져가지 않는다 (누르는 동안에도 녹화 중인 창이나 그림 창이 계속 포커스를 가짐).
/// 버튼이 하는 일과 십자 아이콘 끌기(녹화 영역 옮기기)는 패널이 받아서 한다 (이 창은 버튼과 표시만).
/// </summary>
public partial class MiniBarWindow : Window
{
    public event Action? RecClicked, PauseClicked, PrevClicked, NextClicked, ExpandClicked;
    /// <summary>1 커서 · 2 펜 · 3 형광펜 · 4 색 · 5 지우기.</summary>
    public event Action<int>? ToolClicked;
    /// <summary>십자 아이콘 끌기: 시작 → 움직임 → 끝 (패널 머리 칸 끌기와 같은 동작).</summary>
    public event Action? DragStarted, DragMoved, DragEnded;
    /// <summary>끌기를 시작해도 되는지 (녹화 중이면 영역 고정).</summary>
    public Func<bool>? CanDrag;

    public MiniBarWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };
        WheelImage.Source = Wheel;
    }

    /// <summary>색 고르기 아이콘: 360도를 256칸으로 나눈 색상환, 가운데로 갈수록 흰색. 한 번 만들어 같이 쓴다.</summary>
    static readonly ImageSource Wheel = MakeWheel();

    static ImageSource MakeWheel()
    {
        const int n = 256;
        const double r = 10;
        var c = new Point(r, r);
        Point At(double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        var g = new DrawingGroup();
        for (int i = 0; i < n; i++)
        {
            double a0 = 2 * Math.PI * i / n - Math.PI / 2, a1 = 2 * Math.PI * (i + 1) / n - Math.PI / 2 + 0.01;   // 칸 사이 틈 없게 살짝 겹침
            var fig = new PathFigure(c, new PathSegment[]
            {
                new LineSegment(At(a0), false),
                new ArcSegment(At(a1), new Size(r, r), 0, false, SweepDirection.Clockwise, false),
            }, true);
            var brush = new SolidColorBrush(Hue(360.0 * i / n));
            brush.Freeze();
            g.Children.Add(new GeometryDrawing(brush, null, new PathGeometry(new[] { fig })));
        }
        var fade = new RadialGradientBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF));
        fade.Freeze();
        g.Children.Add(new GeometryDrawing(fade, null, new EllipseGeometry(c, r, r)));
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    /// <summary>색상(0~360), 채도 · 명도 100% → RGB.</summary>
    static Color Hue(double h)
    {
        double x = 1 - Math.Abs(h / 60 % 2 - 1);
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    void OnRec(object sender, RoutedEventArgs e) => RecClicked?.Invoke();
    void OnPause(object sender, RoutedEventArgs e) => PauseClicked?.Invoke();
    void OnPrev(object sender, RoutedEventArgs e) => PrevClicked?.Invoke();
    void OnNext(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    void OnExpand(object sender, RoutedEventArgs e) => ExpandClicked?.Invoke();
    void OnTool(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && int.TryParse(t, out int n)) ToolClicked?.Invoke(n);
    }

    // ── 표시 ──

    public void SetRecording(bool on, bool paused, bool canClick)
    {
        RecBtn.Content = on ? "STOP" : "REC";   // 픽셀 글꼴에 ● ■ 글자가 없음
        RecBtn.IsEnabled = canClick;
        PauseBtn.IsEnabled = on;
        PauseBtn.Content = paused ? ">" : "II";
        StateText.Text = !on ? "" : paused ? "PAUSE" : "REC";   // 대기 중에는 비워 둔다
        StateText.SetResourceReference(TextBlock.ForegroundProperty, paused ? "Yellow" : "Danger");
        Grip.Opacity = on ? 0.4 : 1;   // 녹화 중에는 영역을 옮길 수 없음
        if (!on) TimerText.Text = "00:00:00";
    }

    public void SetTime(string text) { if (TimerText.Text != text) TimerText.Text = text; }

    public void SetTool(InkTool tool, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        ColorSwatch.Background = brush;
        foreach (var (b, t) in new[] { (CursorBtn, InkTool.Cursor), (PenBtn, InkTool.Pen), (HiBtn, InkTool.Highlighter) })
        {
            b.SetResourceReference(Control.ForegroundProperty, t == tool ? "Teal" : "Cream");
            b.Opacity = t == tool ? 1 : 0.75;
        }
    }

    public void PushLevels(float sys, bool sysOff, float mic, bool micOff)
    {
        SysMeter.Push(sys, sysOff);
        MicMeter.Push(mic, micOff);
    }

    public void ThemeChanged()
    {
        SysMeter.InvalidateVisual();
        MicMeter.InvalidateVisual();
    }

    /// <summary>왼쪽 위를 (실제 픽셀) 자리에 둔다.</summary>
    public void Place(double xPx, double yPx)
    {
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        Left = xPx / s;
        Top = yPx / s;
    }

    // ── 십자 아이콘 끌기 → 패널이 녹화 영역을 옮긴다 ──

    bool _dragging;

    void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (CanDrag?.Invoke() == false) return;
        _dragging = true;
        Grip.CaptureMouse();
        DragStarted?.Invoke();
    }

    void OnGripMove(object sender, MouseEventArgs e) { if (_dragging) DragMoved?.Invoke(); }

    void OnGripUp(object sender, MouseButtonEventArgs e) => Grip.ReleaseMouseCapture();

    void OnGripLost(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DragEnded?.Invoke();
    }
}
