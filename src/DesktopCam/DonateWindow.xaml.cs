using System.Windows;
using System.Windows.Interop;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>녹화 영역 가운데의 후원 계좌 안내. 켤 때마다 뜨고, X 로 닫으면 이번 실행 동안은 다시 안 뜬다.</summary>
public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>녹화 영역 가운데에 둔다. region 은 실제 픽셀(화면 좌표).</summary>
    public void Place(Native.RECT region)
    {
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        UpdateLayout();
        double w = ActualWidth * s, h = ActualHeight * s;
        Left = ((region.Left + region.Right) / 2.0 - w / 2) / s;
        Top = ((region.Top + region.Bottom) / 2.0 - h / 2) / s;
    }
}
