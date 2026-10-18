using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 주 모니터 전체를 덮는 투명 창. 녹화 영역 바깥만 어둡게 칠하고, 마우스 클릭은 아래 창으로 그대로 통과시킨다.
/// </summary>
public partial class OverlayWindow : Window
{
    const int EdgeWidth = 6;
    RECT _monitor;
    double _scale = 1;   // 실제 픽셀 → WPF 단위

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // 클릭 통과 + Alt+Tab 목록에서 숨김 + 포커스를 가져가지 않음
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE)
                | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    /// <summary>모니터를 다시 읽고 창을 모니터 크기에 맞춘다.</summary>
    void FitToMonitor()
    {
        _monitor = GetPrimaryMonitor().monitor;
        _scale = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        Left = _monitor.Left / _scale;
        Top = _monitor.Top / _scale;
        Width = _monitor.Width / _scale;
        Height = _monitor.Height / _scale;
    }

    /// <summary>녹화 영역(실제 픽셀, 모니터 기준 좌표)과 어둡기(0~1)를 적용한다.</summary>
    public void SetRegion(RECT region, double dim, bool recording = false)
    {
        FitToMonitor();
        double s = _scale;
        var full = new Rect(0, 0, _monitor.Width / s, _monitor.Height / s);
        var hole = new Rect((region.Left - _monitor.Left) / s, (region.Top - _monitor.Top) / s, region.Width / s, region.Height / s);

        Shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(hole));
        ShadeBrush.Opacity = dim;

        // Rectangle 의 선은 안쪽으로 그려지므로, 영역보다 선 두께만큼 크게 잡으면 선이 영역 바로 바깥에 놓인다
        double e = EdgeWidth / s;
        Canvas.SetLeft(Edge, hole.Left - e);
        Canvas.SetTop(Edge, hole.Top - e);
        Edge.Width = hole.Width + e * 2;
        Edge.Height = hole.Height + e * 2;
        Edge.StrokeThickness = e;
        Edge.Stroke = (Brush)FindResource(recording ? "Danger" : "Teal");

        PlaceCredit(hole, s);
    }

    /// <summary>패널 위치(실제 픽셀, 화면 좌표)를 받아 그 뒤에 8px 밀린 그림자를 그린다.</summary>
    public void SetPanelShadow(double leftPx, double topPx, double widthPx, double heightPx)
    {
        const int Offset = 8;
        double s = _scale;
        Canvas.SetLeft(PanelShadow, (leftPx - _monitor.Left + Offset) / s);
        Canvas.SetTop(PanelShadow, (topPx - _monitor.Top + Offset) / s);
        PanelShadow.Width = widthPx / s;
        PanelShadow.Height = heightPx / s;
        PanelShadow.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 크레딧은 녹화에 찍히지 않도록 항상 영역 바깥에 둔다.
    /// 1) 영역 아래, 왼쪽 맞춤  2) 영역 왼쪽, 아래 맞춤
    /// 3) QHD 처럼 영역이 화면 높이를 다 쓰면 오른쪽 패널(높이 1080) 아래 빈 곳의 바닥
    /// 4) 그래도 자리가 없으면 숨김
    /// </summary>
    void PlaceCredit(Rect hole, double s)
    {
        const int PanelGap = 24, PanelHeight = 1080;   // PanelWindow 와 같은 값
        var work = GetPrimaryMonitor().work;
        double workBottom = (work.Bottom - _monitor.Top) / s;   // 작업표시줄에 가리지 않게
        double gap = (EdgeWidth + 10) / s;

        Credit.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Credit.DesiredSize;

        double x, y;
        if (hole.Bottom + gap + size.Height <= workBottom)
        {
            x = hole.Left;
            y = hole.Bottom + gap;
        }
        else if (hole.Left - gap - size.Width >= 0)
        {
            x = hole.Left - gap - size.Width;
            y = Math.Min(hole.Bottom, workBottom) - size.Height;
        }
        else if (hole.Top + (PanelHeight + 8) / s + gap + size.Height <= workBottom
                 && hole.Right + PanelGap / s + size.Width <= _monitor.Width / s)
        {
            x = hole.Right + PanelGap / s;
            y = workBottom - gap - size.Height;
        }
        else
        {
            Credit.Visibility = Visibility.Collapsed;
            return;
        }
        Credit.Visibility = Visibility.Visible;
        Canvas.SetLeft(Credit, x);
        Canvas.SetTop(Credit, y);
    }
}
