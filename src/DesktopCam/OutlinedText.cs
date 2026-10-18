using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesktopCam;

/// <summary>
/// 바탕 없이 글자만 그리되, 어두운 테두리를 둘러 밝은 화면·어두운 화면 어디서나 읽히게 한다.
/// 강조 구간은 노란색, 이미 읽은 부분(ReadRatio)은 teal 로 칠한다.
///
/// 빠르게 그리기: 테두리는 글자 윤곽을 도형으로 바꿔 두꺼운 선을 긋는 대신(한글은 윤곽이 복잡해 느림),
/// 같은 글자를 어두운 색으로 8방향에 찍어서 만든다. 글자 배치(FormattedText)는 글자·크기·폭이 바뀔 때만 새로 만든다.
/// </summary>
public class OutlinedText : FrameworkElement
{
    static readonly Brush Outline = Frozen(Color.FromRgb(0x14, 0x12, 0x2B));
    static readonly (double X, double Y)[] Dirs =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (0.7, 0.7), (-0.7, 0.7), (0.7, -0.7), (-0.7, -0.7),
    };

    public string Text { get; set; } = "";
    public IReadOnlyList<(int Start, int Length)> Emphasis { get; set; } = Array.Empty<(int, int)>();
    public double Size { get; set; } = 30;
    public bool Bold { get; set; }
    public Brush Fill { get; set; } = Brushes.White;
    public Brush EmphasisFill { get; set; } = Brushes.Yellow;
    public Brush ReadFill { get; set; } = Brushes.Teal;
    public TextAlignment Align { get; set; } = TextAlignment.Center;

    double _readRatio;
    public double ReadRatio
    {
        get => _readRatio;
        set
        {
            if (Math.Abs(_readRatio - value) < 0.01) return;
            _readRatio = value;
            _fill = null;          // 테두리는 그대로, 글자 색만 다시
            InvalidateVisual();
        }
    }

    // 캐시: 테두리용(어두운 색 한 가지)과 글자용(강조·읽은 부분 색)
    FormattedText? _outline, _fill;
    string _key = "";

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    double Thick => Math.Max(1.5, Size * 0.07);   // 테두리 두께
    double Pad => Math.Ceiling(Thick * 2) + 1;

    /// <summary>글자·강조·크기·정렬을 바꾼 뒤 부른다.</summary>
    public void Refresh()
    {
        _key = "";
        Visibility = Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure();
        InvalidateVisual();
    }

    FormattedText Make(double width, Brush brush)
    {
        var face = new Typeface((FontFamily)Application.Current.FindResource("KoFont"),
            FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
        var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(10, width),
            TextAlignment = Align,
        };
        foreach (var (s, l) in Emphasis)
            if (s >= 0 && s + l <= Text.Length) ft.SetFontWeight(FontWeights.ExtraBold, s, l);
        return ft;
    }

    void Ensure(double width)
    {
        string key = $"{Text}|{Size}|{Bold}|{Align}|{width:0}|{Emphasis.Count}";
        if (key != _key)
        {
            _key = key;
            _outline = Make(width, Outline);
            _fill = null;
        }
        if (_fill == null)
        {
            _fill = Make(width, Fill);
            foreach (var (s, l) in Emphasis)
                if (s >= 0 && s + l <= Text.Length) _fill.SetForegroundBrush(EmphasisFill, s, l);
            int read = (int)Math.Round(Text.Length * Math.Clamp(_readRatio, 0, 1));
            if (read > 0) _fill.SetForegroundBrush(ReadFill, 0, read);
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Text.Length == 0) return new Size(0, 0);
        double w = double.IsInfinity(available.Width) ? 1200 : available.Width;
        Ensure(w - Pad * 2);
        return new Size(w, _outline!.Height + Pad * 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Text.Length == 0) return;
        Ensure(ActualWidth - Pad * 2);
        double t = Thick;

        // 아래로 살짝 밀린 그림자 → 8방향 테두리 → 글자
        dc.DrawText(_outline!, new Point(Pad + t * 0.8, Pad + t * 1.6));
        foreach (var (x, y) in Dirs) dc.DrawText(_outline!, new Point(Pad + x * t, Pad + y * t));
        dc.DrawText(_fill!, new Point(Pad, Pad));
    }
}
