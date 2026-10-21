using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 녹화 영역 안에 띄우는 대본. 바탕 없이 테두리 두른 글자만 보인다.
/// 화면 캡처에서 빠지므로 녹화에는 찍히지 않고, 클릭은 아래 창으로 통과한다.
/// 위치: 녹화 영역의 위 · 아래 · 왼쪽 · 오른쪽. 크기: 배율(Scale).
/// </summary>
public partial class ScriptWindow : Window
{
    public const string PosTop = "Top", PosBottom = "Bottom", PosLeft = "Left", PosRight = "Right";

    Native.RECT _region;
    string _pos = PosTop;
    double _scale = 1;
    bool _showPrev = true, _showSection = true, _showCue = true;
    int _nextCount = 2;

    /// <summary>무엇을 보일지: 이전 줄 · 섹션 이름 · 메모 · 다음 줄 수(0~2).</summary>
    public void SetOptions(bool prev, bool section, bool cue, int nextCount)
    {
        _showPrev = prev; _showSection = section; _showCue = cue; _nextCount = Math.Clamp(nextCount, 0, 2);
    }

    public ScriptWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE)
                | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };

        ApplyColors();
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        PrevLine.Opacity = 0.5;
        NextLine1.Opacity = 0.8;
        NextLine2.Opacity = 0.55;
        CurLine.Bold = true;
        ApplySizes();
    }

    /// <summary>글자 색: 보통 cream, 강조 yellow, 읽은 부분 · 섹션 teal (테마 색을 따른다).</summary>
    void ApplyColors()
    {
        var cream = (Brush)FindResource("Cream");
        var yellow = (Brush)FindResource("Yellow");
        var teal = (Brush)FindResource("Teal");
        foreach (var t in new[] { SectionText, PrevLine, CueText, CurLine, NextLine1, NextLine2 })
        {
            t.Fill = cream;
            t.EmphasisFill = yellow;
            t.ReadFill = teal;
        }
        SectionText.Fill = teal;
        CueText.Fill = yellow;
    }

    void OnThemeChanged()
    {
        ApplyColors();
        foreach (var t in new[] { SectionText, PrevLine, CueText, CurLine, NextLine1, NextLine2 }) t.Refresh();
    }

    /// <summary>녹화 영역(실제 픽셀) 안에 놓는다. 위·아래는 가운데 정렬 폭 70%, 왼쪽·오른쪽은 세로 가운데 폭 32%.</summary>
    public void Place(Native.RECT region, string pos, double scale)
    {
        _region = region;
        _pos = pos;
        if (Math.Abs(scale - _scale) > 0.001) { _scale = scale; ApplySizes(); }

        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        bool side = pos is PosLeft or PosRight;
        double w = side ? Math.Max(360, region.Width * 0.32) : Math.Max(600, region.Width * 0.7);
        var align = pos == PosLeft ? TextAlignment.Left : pos == PosRight ? TextAlignment.Right : TextAlignment.Center;
        foreach (var t in Texts) t.Align = align;

        Width = w / s;
        foreach (var t in Texts) t.Refresh();
        UpdateLayout();
        double h = ActualHeight * s;
        const double margin = 24;

        double x = pos switch
        {
            PosLeft => region.Left + margin,
            PosRight => region.Right - margin - w,
            _ => region.Left + (region.Width - w) / 2,
        };
        double y = pos switch
        {
            PosBottom => region.Bottom - margin - h,
            PosLeft or PosRight => region.Top + (region.Height - h) / 2,
            _ => region.Top + margin,
        };
        Left = x / s;
        Top = y / s;
    }

    IEnumerable<OutlinedText> Texts => new[] { SectionText, PrevLine, CueText, CurLine, NextLine1, NextLine2 };

    /// <summary>기본 글자 크기 × 배율. 지금 줄 34, 다음 줄 22, 나머지 18.</summary>
    void ApplySizes()
    {
        CurLine.Size = 34 * _scale;
        NextLine1.Size = 22 * _scale;
        NextLine2.Size = 20 * _scale;
        PrevLine.Size = 18 * _scale;
        CueText.Size = 18 * _scale;
        SectionText.Size = 16 * _scale;
        CurLine.Margin = new Thickness(0, 2 * _scale, 0, 6 * _scale);
    }

    public void Render(IReadOnlyList<ScriptLine> lines, int index)
    {
        ScriptLine? At(int i) => i >= 0 && i < lines.Count ? lines[i] : null;
        var cur = At(index);

        SectionText.Text = _showSection ? cur?.Section ?? "" : "";
        PrevLine.Text = _showPrev ? At(index - 1)?.Text ?? "" : "";
        CueText.Text = _showCue && cur != null ? cur.Cue : "";
        CurLine.Text = lines.Count == 0 ? "대본을 불러오세요" : cur?.Text ?? "";
        CurLine.Emphasis = cur?.Emphasis ?? Array.Empty<(int, int)>();
        CurLine.ReadRatio = 0;
        NextLine1.Text = _nextCount >= 1 ? At(index + 1)?.Text ?? "" : "";
        NextLine1.Emphasis = At(index + 1)?.Emphasis ?? Array.Empty<(int, int)>();
        NextLine2.Text = _nextCount >= 2 ? At(index + 2)?.Text ?? "" : "";
        NextLine2.Emphasis = At(index + 2)?.Emphasis ?? Array.Empty<(int, int)>();
        if (_region.Width > 0) Place(_region, _pos, _scale);   // 줄 길이에 따라 높이가 바뀌므로 다시 놓는다
        else foreach (var t in Texts) t.Refresh();
    }

    /// <summary>지금 줄을 얼마나 읽었는지 (음성인식). 읽은 만큼 글자가 teal 로 바뀐다.</summary>
    public void SetProgress(double ratio) => CurLine.ReadRatio = ratio;   // 글자 색만 다시 칠함
}
