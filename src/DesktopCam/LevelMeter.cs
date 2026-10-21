using System.Windows;
using System.Windows.Media;

namespace DesktopCam;

/// <summary>
/// 칸으로 나뉜 음량 막대. -60dB~0dB 를 0~1 로 그린다.
/// teal(보통) → yellow(큰 소리, -10dB 위) → danger(찢어질 위험, -3dB 위), 흰 선은 최근 최고치.
/// </summary>
public class LevelMeter : FrameworkElement
{
    const double MinDb = -60, YellowAt = 0.70, DangerAt = 0.88, Cell = 10, Gap = 2, Border = 2;

    // 매 프레임 리소스를 찾지 않도록 한 번만 가져온다 (얼린 브러시는 공유해도 안전)
    static Brush? _bg, _teal, _yellow, _danger, _cream;
    static Pen? _edge;
    static Brush? _gaps;

    /// <summary>테마가 바뀌면 다음 그리기 때 색을 다시 가져오게 한다 (UI 스레드).</summary>
    public static void ResetBrushes() => _bg = null;

    static void EnsureBrushes()
    {
        if (_bg != null) return;
        Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        _bg = Res("Bg"); _teal = Res("Teal"); _yellow = Res("Yellow"); _danger = Res("Danger"); _cream = Res("Cream");
        _edge = new Pen(Res("TealDeep"), Border);
        _edge.Freeze();
        var tile = new DrawingBrush(new GeometryDrawing(_bg, null, new RectangleGeometry(new Rect(Cell - Gap, 0, Gap, 1))))
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None,
            Viewport = new Rect(Border, 0, Cell, 1), ViewportUnits = BrushMappingMode.Absolute,   // 예전과 같은 자리(테두리 안쪽 기준)
            Viewbox = new Rect(0, 0, Cell, 1), ViewboxUnits = BrushMappingMode.Absolute,
        };
        tile.Freeze();
        _gaps = tile;
    }

    double _level, _peak, _drawnLevel = -1, _drawnPeak = -1;
    bool _off;

    public LevelMeter() { Height = 18; SnapsToDevicePixels = true; }

    /// <summary>선형 피크(0~1)를 dB 눈금 위치(0~1)로 바꾼다.</summary>
    public static double ToPosition(float peak) =>
        peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) - MinDb) / -MinDb, 0, 1);

    /// <summary>새 값을 넣는다. 최고치 표시는 천천히 떨어진다.</summary>
    public void Push(float peak, bool off)
    {
        double pos = ToPosition(peak);
        _level = pos > _level ? pos : _level + (pos - _level) * 0.35;   // 올라갈 땐 바로, 내려갈 땐 부드럽게
        _peak = Math.Max(pos, _peak - 0.012);
        bool changed = Math.Abs(_level - _drawnLevel) > 0.002 || Math.Abs(_peak - _drawnPeak) > 0.002 || off != _off;
        _off = off;
        if (!changed) return;
        _drawnLevel = _level; _drawnPeak = _peak;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        EnsureBrushes();
        dc.DrawRectangle(_bg, null, new Rect(0, 0, w, h));

        double inner = w - Border * 2, ih = h - Border * 2;
        double fill = inner * _level;
        if (fill > 0)
        {
            dc.PushOpacity(_off ? 0.15 : 1);
            DrawBand(dc, 0, Math.Min(fill, inner * YellowAt), _teal!, ih);
            DrawBand(dc, inner * YellowAt, Math.Min(fill, inner * DangerAt), _yellow!, ih);
            DrawBand(dc, inner * DangerAt, fill, _danger!, ih);
            dc.Pop();
        }

        // 칸 사이 틈: 10px 마다 2px 틈을 그린 타일 하나로 한 번에 (사각형 25개를 따로 그리지 않음)
        if (fill > 0) dc.DrawRectangle(_gaps, null, new Rect(Border, Border, Math.Min(fill + Gap, inner), ih));

        if (_peak > 0.01 && !_off)
            dc.DrawRectangle(_cream, null, new Rect(Border + Math.Max(0, inner * _peak - 3), Border, 3, ih));

        dc.DrawRectangle(null, _edge, new Rect(Border / 2, Border / 2, w - Border, h - Border));
    }

    static void DrawBand(DrawingContext dc, double from, double to, Brush brush, double ih)
    {
        if (to > from) dc.DrawRectangle(brush, null, new Rect(Border + from, Border, to - from, ih));
    }
}
