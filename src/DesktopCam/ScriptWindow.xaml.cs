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
    /// <summary>이전 줄 · 다음 줄을 각각 몇 줄까지 보일 수 있는지.</summary>
    public const int MaxContext = 20;

    Native.RECT _region;
    string _pos = PosTop;
    double _scale = 1;
    bool _showSection = true, _showCue = true;
    int _prevCount = 1, _nextCount = 2;

    // Prev[0] = 바로 앞 줄, Next[0] = 바로 다음 줄. 멀수록 흐리고 작다.
    readonly OutlinedText[] _prev = new OutlinedText[MaxContext];
    readonly OutlinedText[] _next = new OutlinedText[MaxContext];

    /// <summary>무엇을 보일지: 이전 줄 수 · 섹션 이름 · 메모 · 다음 줄 수(각 0~MaxContext).</summary>
    public void SetOptions(int prevCount, bool section, bool cue, int nextCount)
    {
        _prevCount = Math.Clamp(prevCount, 0, MaxContext);
        _showSection = section; _showCue = cue;
        _nextCount = Math.Clamp(nextCount, 0, MaxContext);
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

        for (int i = 0; i < MaxContext; i++)
        {
            _prev[i] = new OutlinedText { Opacity = Math.Max(0.25, 0.5 - 0.06 * i) };
            _next[i] = new OutlinedText { Opacity = Math.Max(0.3, 0.8 - 0.15 * i) };
            NextPanel.Children.Add(_next[i]);
        }
        for (int i = MaxContext - 1; i >= 0; i--) PrevPanel.Children.Add(_prev[i]);   // 먼 줄이 위

        ApplyColors();
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        CurLine.Bold = true;
        ApplySizes();
    }

    IEnumerable<OutlinedText> Texts => new[] { SectionText, CueText, CurLine }.Concat(_prev).Concat(_next);

    /// <summary>글자 색: 보통 cream, 강조 yellow, 읽은 부분 · 섹션 teal (테마 색을 따른다).</summary>
    void ApplyColors()
    {
        var cream = (Brush)FindResource("Cream");
        var yellow = (Brush)FindResource("Yellow");
        var teal = (Brush)FindResource("Teal");
        foreach (var t in Texts)
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
        foreach (var t in Texts) t.Refresh();
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
        const double margin = 24;

        double x = pos switch
        {
            PosLeft => region.Left + margin,
            PosRight => region.Right - margin - w,
            _ => region.Left + (region.Width - w) / 2,
        };

        // 지금 읽을 줄(CurLine)의 윗변을 고정 자리에 둔다. 이전 · 다음 줄 수나 줄 길이가 바뀌어도 읽는 자리는 그대로이고,
        // 늘어난 줄은 위 · 아래로 뻗는다 (영역이나 화면 밖으로 나가도 둔다).
        //  위: 영역 위 + 섹션 · 메모 · 이전 줄(설정한 수) 한 줄씩 들어갈 자리
        //  아래: 영역 아래에서 지금 줄 한 줄 + 다음 줄 2줄 들어갈 자리만큼 위 (다음 줄을 더 늘리면 아래로 넘친다)
        //  왼쪽 · 오른쪽: 영역 세로 가운데
        double curTop = pos switch
        {
            PosBottom => region.Bottom - margin - (OneLine(CurLine) + OneLine(_next[0]) + OneLine(_next[1])) * s,
            PosLeft or PosRight => region.Top + region.Height / 2 - OneLine(CurLine) * s / 2,
            _ => region.Top + margin + AboveReserve() * s,
        };
        double curOffset = CurLine.IsVisible ? CurLine.TranslatePoint(new Point(0, 0), this).Y * s : 0;
        Left = x / s;
        Top = (curTop - curOffset) / s;
    }

    /// <summary>글자 크기 size 로 한 줄짜리일 때의 높이 (DIP, 위아래 여백 포함).</summary>
    static double OneLine(OutlinedText like)
    {
        var t = new OutlinedText { Text = "가", Size = like.Size, Bold = like.Bold };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return t.DesiredSize.Height + like.Margin.Top + like.Margin.Bottom;
    }

    /// <summary>위쪽 배치에서 지금 줄 위에 비워 둘 높이: 섹션 · 이전 줄(설정한 수) · 메모를 한 줄씩.</summary>
    double AboveReserve()
    {
        double h = 0;
        if (_showSection) h += OneLine(SectionText);
        for (int i = 0; i < _prevCount; i++) h += OneLine(_prev[i]);
        if (_showCue) h += OneLine(CueText);
        return h;
    }

    /// <summary>기본 글자 크기 × 배율. 지금 줄 34, 다음 줄 22부터 한 줄마다 2씩 작게(최소 16), 이전 줄 18부터(최소 14).</summary>
    void ApplySizes()
    {
        CurLine.Size = 34 * _scale;
        for (int i = 0; i < MaxContext; i++)
        {
            _next[i].Size = Math.Max(16, 22 - 2 * i) * _scale;
            _prev[i].Size = Math.Max(14, 18 - 1 * i) * _scale;
        }
        CueText.Size = 18 * _scale;
        SectionText.Size = 16 * _scale;
        CurLine.Margin = new Thickness(0, 2 * _scale, 0, 6 * _scale);
    }

    public void Render(IReadOnlyList<ScriptLine> lines, int index)
    {
        ScriptLine? At(int i) => i >= 0 && i < lines.Count ? lines[i] : null;
        var cur = At(index);

        SectionText.Text = _showSection ? cur?.Section ?? "" : "";
        CueText.Text = _showCue && cur != null ? cur.Cue : "";
        CurLine.Text = lines.Count == 0 ? "대본을 불러오세요" : cur?.Text ?? "";
        CurLine.Emphasis = cur?.Emphasis ?? Array.Empty<(int, int)>();
        CurLine.ReadRatio = 0;
        for (int i = 0; i < MaxContext; i++)
        {
            var p = At(index - 1 - i);
            _prev[i].Text = i < _prevCount ? p?.Text ?? "" : "";
            _prev[i].Emphasis = p?.Emphasis ?? Array.Empty<(int, int)>();
            var n = At(index + 1 + i);
            _next[i].Text = i < _nextCount ? n?.Text ?? "" : "";
            _next[i].Emphasis = n?.Emphasis ?? Array.Empty<(int, int)>();
        }
        if (_region.Width > 0) Place(_region, _pos, _scale);   // 줄 길이에 따라 높이가 바뀌므로 다시 놓는다
        else foreach (var t in Texts) t.Refresh();
    }

    /// <summary>지금 줄을 얼마나 읽었는지 (음성인식). 읽은 만큼 글자가 teal 로 바뀐다.</summary>
    public void SetProgress(double ratio) => CurLine.ReadRatio = ratio;   // 글자 색만 다시 칠함
}
