using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

public enum InkTool { Cursor, Pen, Highlighter }

/// <summary>
/// 녹화 영역 위의 투명한 그림 창 (펜 · 형광펜). 녹화에서 빼지 않으므로 그린 것이 영상에 찍힌다.
/// 커서 모드에서는 클릭이 아래 창으로 통과하고 그림은 그대로 남는다 (5 로 지운다).
/// 펜 · 형광펜 모드에서는 이 창이 포커스를 받으므로 숫자 키만 눌러도 KeyPressed 로 알린다.
/// </summary>
public partial class InkWindow : Window
{
    // 펜 · 형광펜 모드에서 마우스를 받으려면 바탕이 완전히 투명하면 안 된다 (투명 픽셀은 클릭이 통과). 1/255 은 눈에 안 보인다
    static readonly Brush HitBrush = Frozen(Color.FromArgb(1, 0, 0, 0));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    IntPtr _hwnd;
    public InkTool Tool { get; private set; } = InkTool.Cursor;

    /// <summary>펜 · 형광펜 모드에서 눌린 키 (숫자 · Esc). Ctrl+Alt 조합은 전역 단축키가 먼저 받는다.</summary>
    public event Action<Key>? KeyPressed;

    public InkWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE)
                | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        PreviewKeyDown += OnKey;
    }

    public IntPtr Handle => _hwnd;
    public bool HasInk => Ink.Strokes.Count > 0;

    /// <summary>녹화 영역(실제 픽셀)을 꼭 덮는다.</summary>
    public void Place(RECT region)
    {
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        Left = region.Left / s;
        Top = region.Top / s;
        Width = region.Width / s;
        Height = region.Height / s;
    }

    public void SetTool(InkTool tool, Color color)
    {
        Tool = tool;
        bool draw = tool != InkTool.Cursor;
        if (_hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex = draw ? ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) : ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        }
        Ink.Background = draw ? HitBrush : Brushes.Transparent;
        Ink.EditingMode = draw ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        Ink.DefaultDrawingAttributes = tool == InkTool.Highlighter
            ? new DrawingAttributes
            {
                Color = color, IsHighlighter = true, StylusTip = StylusTip.Rectangle,
                Width = 10, Height = 26, IgnorePressure = true, FitToCurve = false,
            }
            : new DrawingAttributes
            {
                Color = color, StylusTip = StylusTip.Ellipse,
                Width = 5, Height = 5, IgnorePressure = true, FitToCurve = true,
            };
        if (draw)
        {
            Activate();
            Ink.Focus();
        }
    }

    public void Clear() => Ink.Strokes.Clear();

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Tool == InkTool.Cursor) return;
        // 되돌리기: 마지막 한 획
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Ink.Strokes.Count > 0) Ink.Strokes.RemoveAt(Ink.Strokes.Count - 1);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var key = e.Key switch
        {
            >= Key.NumPad0 and <= Key.NumPad9 => Key.D0 + (e.Key - Key.NumPad0),
            _ => e.Key,
        };
        if (key is (>= Key.D0 and <= Key.D9) or Key.Escape)
        {
            KeyPressed?.Invoke(key);
            e.Handled = true;
        }
    }
}
