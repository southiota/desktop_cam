using System.Windows;
using System.Windows.Interop;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 녹화 영역 바깥 오른쪽 아래의 사용법 버튼.
/// 1) 영역 바로 아래, 오른쪽 끝에 맞춤  2) 아래 자리가 없으면 패널 밑 빈 곳의 오른쪽 아래  3) 그래도 없으면 패널 왼쪽 아래.
/// </summary>
public partial class HelpButtonWindow : Window
{
    public event Action? Clicked;

    public HelpButtonWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };
    }

    void OnClick(object sender, RoutedEventArgs e) => Clicked?.Invoke();

    /// <summary>모든 값은 실제 픽셀(화면 좌표).</summary>
    public void Place(Native.RECT region, Rect panelPx, Native.RECT work)
    {
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        UpdateLayout();
        double w = ActualWidth * s, h = ActualHeight * s;
        const double gap = 16;   // 테두리(6px) 바깥으로 여유

        double x, y;
        if (region.Bottom + gap + h <= work.Bottom)
        {
            x = region.Right - w;
            y = region.Bottom + gap;
        }
        else if (panelPx.Bottom + 8 + gap + h <= work.Bottom)
        {
            x = panelPx.Right - w;
            y = work.Bottom - gap - h;
        }
        else
        {
            x = panelPx.Left - gap - w;
            y = work.Bottom - gap - h;
        }
        Left = x / s;
        Top = y / s;
    }
}
