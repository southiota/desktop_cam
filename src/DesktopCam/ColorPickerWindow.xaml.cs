using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 오피스 스타일 색 고르기. 커서 오른쪽 아래에 뜬다. 고르면 Picked, 바깥을 누르거나 Esc 면 그냥 닫힌다.
/// </summary>
public partial class ColorPickerWindow : Window
{
    // 오피스 기본 테마 색 10개 (흰색 · 검정 · 회색 · 남색 · 파랑 · 주황 · 회색 · 금색 · 하늘 · 초록)
    static readonly string[] ThemeColors =
        { "#FFFFFF", "#000000", "#E7E6E6", "#44546A", "#4472C4", "#ED7D31", "#A5A5A5", "#FFC000", "#5B9BD5", "#70AD47" };
    static readonly string[] StandardColors =
        { "#C00000", "#FF0000", "#FFC000", "#FFFF00", "#92D050", "#00B050", "#00B0F0", "#0070C0", "#002060", "#7030A0" };

    public event Action<Color>? Picked;
    readonly Color _current;
    bool _closing;

    public ColorPickerWindow(Color current)
    {
        _current = current;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };

        foreach (var hex in ThemeColors) ThemeTop.Children.Add(Cell(Parse(hex), 0));
        // 밝기 줄: 오피스처럼 흰색은 점점 어둡게, 검정은 점점 밝게, 나머지는 밝게 80 · 60 · 40% 와 어둡게 25 · 50%
        for (int row = 0; row < 5; row++)
            foreach (var hex in ThemeColors)
            {
                var c = Parse(hex);
                double l = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255;
                var v = l > 0.95 ? Shade(c, new[] { 0.05, 0.15, 0.25, 0.35, 0.5 }[row])
                      : l < 0.05 ? Tint(c, new[] { 0.5, 0.35, 0.25, 0.15, 0.05 }[row])
                      : row < 3 ? Tint(c, new[] { 0.8, 0.6, 0.4 }[row]) : Shade(c, new[] { 0.25, 0.5 }[row - 3]);
                ThemeShades.Children.Add(Cell(v, row));
            }
        foreach (var hex in StandardColors) Standard.Children.Add(Cell(Parse(hex), 0));

        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Color Tint(Color c, double t) => Color.FromRgb(Mix(c.R, 255, t), Mix(c.G, 255, t), Mix(c.B, 255, t));
    static Color Shade(Color c, double t) => Color.FromRgb(Mix(c.R, 0, t), Mix(c.G, 0, t), Mix(c.B, 0, t));
    static byte Mix(byte a, int b, double t) => (byte)Math.Round(a + (b - a) * t);

    FrameworkElement Cell(Color c, int row)
    {
        var fill = new SolidColorBrush(c); fill.Freeze();
        bool selected = c == _current;
        var b = new Border
        {
            Width = 18, Height = 18,
            Margin = new Thickness(2, row == 0 ? 0 : 1, 2, 0),
            Background = fill,
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = Cursors.Hand,
            ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
        };
        if (selected) b.SetResourceReference(Border.BorderBrushProperty, "Yellow");
        else b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        b.MouseEnter += (_, _) => { b.SetResourceReference(Border.BorderBrushProperty, "Teal"); b.BorderThickness = new Thickness(2); };
        b.MouseLeave += (_, _) =>
        {
            if (selected) b.SetResourceReference(Border.BorderBrushProperty, "Yellow");
            else b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
            b.BorderThickness = new Thickness(selected ? 2 : 1);
        };
        b.MouseLeftButtonUp += (_, _) => { Picked?.Invoke(c); SafeClose(); };
        return b;
    }

    void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>커서 오른쪽 아래에 띄운다 (화면 밖이면 안쪽으로).</summary>
    public void ShowAtCursor()
    {
        Show();
        UpdateLayout();
        GetCursorPos(out var p);
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        var (_, work) = GetPrimaryMonitor();
        double w = ActualWidth * s, h = ActualHeight * s;
        double x = Math.Min(p.X + 12, work.Right - w - 4);
        double y = Math.Min(p.Y + 12, work.Bottom - h - 4);
        Left = x / s;
        Top = y / s;
        Activate();
    }
}
