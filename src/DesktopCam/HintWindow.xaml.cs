using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>커서 옆에 1.5초 뜨는 짧은 안내. 녹화에서 빠지고, 클릭은 아래로 통과한다.</summary>
public partial class HintWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    public HintWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE)
                | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
    }

    /// <summary>text 를 커서 오른쪽 아래에 띄운다. color 가 있으면 앞 글자(기호) 색.</summary>
    public void Flash(string text, string keys, Brush? color = null)
    {
        MainText.Text = text;
        MainText.Foreground = color ?? (Brush)FindResource("Cream");
        KeysText.Text = keys;
        KeysText.Visibility = keys.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!IsVisible) Show();
        UpdateLayout();

        GetCursorPos(out var p);
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        var (mon, _) = GetPrimaryMonitor();
        double w = ActualWidth * s, h = ActualHeight * s;
        double x = Math.Min(p.X + 24, mon.Right - w - 8);
        double y = Math.Min(p.Y + 28, mon.Bottom - h - 8);
        Left = x / s;
        Top = y / s;

        _timer.Stop();
        _timer.Start();
    }
}
