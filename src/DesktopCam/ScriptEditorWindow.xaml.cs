using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 대본 상세 편집: 직접 고치기(바로 화면에 반영), 줄 목록(눌러서 이동), 화면 표시 설정, 자동스크롤 [AI] 설정과 들린 말 확인.
/// 녹화에는 찍히지 않는다.
/// </summary>
public partial class ScriptEditorWindow : Window
{
    readonly PanelWindow _panel;
    readonly Settings _s;
    readonly DispatcherTimer _parseTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Brush _teal, _yellow, _danger, _muted, _cream, _raised, _panelBg;
    string? _path;
    bool _dirty, _loading, _ready;
    List<ScriptLine> _parsed = new();

    public ScriptEditorWindow(PanelWindow panel, Settings settings)
    {
        _panel = panel;
        _s = settings;
        InitializeComponent();
        _teal = (Brush)FindResource("Teal");
        _yellow = (Brush)FindResource("Yellow");
        _danger = (Brush)FindResource("Danger");
        _muted = (Brush)FindResource("Muted");
        _cream = (Brush)FindResource("Cream");
        _raised = (Brush)FindResource("BgRaised");
        _panelBg = (Brush)FindResource("BgPanel");

        (_s.ScriptPos switch { "Bottom" => PosBottom, "Left" => PosLeft, "Right" => PosRight, _ => PosTop }).IsChecked = true;
        (_s.ScriptNextCount switch { 0 => Next0, 1 => Next1, _ => Next2 }).IsChecked = true;
        (_s.ScriptSensitivity switch { "Fast" => SensFast, "Strict" => SensStrict, _ => SensNormal }).IsChecked = true;
        UpdateDisplayButtons();
        UpdateVoiceButton();

        _parseTimer.Tick += (_, _) => { _parseTimer.Stop(); ApplyEdits(); };
        _panel.ScriptPositionChanged += OnPanelLineChanged;
        _panel.HeardUpdate += OnHeardUpdate;
        PreviewKeyDown += OnKey;
        Closing += OnClosingWindow;
        Closed += (_, _) =>
        {
            _panel.ScriptPositionChanged -= OnPanelLineChanged;
            _panel.HeardUpdate -= OnHeardUpdate;
        };
        SourceInitialized += (_, _) =>
        {
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WDA_EXCLUDEFROMCAPTURE);
        };

        LoadFile(_s.ScriptPath);
        _ready = true;
        Loaded += (_, _) => ClipCheck.Run(this, "대본 편집");
    }

    // ── 파일 ──

    void LoadFile(string? path)
    {
        _loading = true;
        _path = path != null && File.Exists(path) ? path : null;
        Editor.Text = _path != null ? File.ReadAllText(_path) : "";
        _loading = false;
        SetDirty(false);
        ApplyEdits(push: false);
    }

    void SetDirty(bool dirty)
    {
        _dirty = dirty;
        DirtyMark.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        FileText.Text = _path == null ? "(새 대본)" : _path;
    }

    bool IsMarkdown => _path == null || !Path.GetExtension(_path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _path = null;
        _loading = true;
        Editor.Text = ScriptDoc.Sample;
        _loading = false;
        SetDirty(true);
        ApplyEdits();
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog
        {
            Title = "대본 불러오기",
            Filter = "대본 (*.md;*.txt)|*.md;*.markdown;*.txt|모든 파일|*.*",
            InitialDirectory = Path.GetDirectoryName(_path ?? _s.ScriptPath ?? PanelWindow.DefaultScriptPath),
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
        _panel.OpenScriptFile(dlg.FileName);
    }

    void OnSave(object sender, RoutedEventArgs e) => Save();
    void OnSaveAs(object sender, RoutedEventArgs e) => SaveAs();

    bool Save()
    {
        if (_path == null) return SaveAs();
        try
        {
            File.WriteAllText(_path, Editor.Text, new UTF8Encoding(false));
            SetDirty(false);
            _panel.OpenScriptFile(_path, keepLine: true);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"저장하지 못했습니다.\n{ex.Message}", "대본 편집");
            return false;
        }
    }

    bool SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Title = "대본 저장",
            Filter = "마크다운 대본 (*.md)|*.md|텍스트 (*.txt)|*.txt",
            FileName = _path != null ? Path.GetFileName(_path) : "대본.md",
            InitialDirectory = Path.GetDirectoryName(_path ?? PanelWindow.DefaultScriptPath),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(PanelWindow.DefaultScriptPath)!);
        if (dlg.ShowDialog(this) != true) return false;
        _path = dlg.FileName;
        return Save();
    }

    /// <summary>저장 안 된 내용이 있으면 저장할지 묻는다. 계속해도 되면 true.</summary>
    bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "저장하지 않은 내용이 있습니다. 저장할까요?", "대본 편집",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch { MessageBoxResult.Yes => Save(), MessageBoxResult.No => true, _ => false };
    }

    void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        if (_dirty || _path == null) _panel.OpenScriptFile(_s.ScriptPath, keepLine: true);   // 저장 안 한 미리보기는 되돌림
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    void OnHeaderDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
    }

    // ── 편집 → 바로 화면에 반영 ──

    void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!_dirty) SetDirty(true);
        _parseTimer.Stop();
        _parseTimer.Start();   // 0.3초 동안 더 안 고치면 반영
    }

    void ApplyEdits(bool push = true)
    {
        _parsed = ScriptDoc.Parse(Editor.Text, IsMarkdown);
        if (push) _panel.SetScriptLinesLive(_parsed);
        RenderLineList();
        RenderStats();
    }

    void RenderStats()
    {
        int sections = _parsed.Select(l => l.Section).Where(s => s != "").Distinct().Count();
        int longCount = _parsed.Count(l => l.Text.Length > ScriptDoc.LongLine);
        var t = ScriptDoc.ReadingTime(_parsed);
        StatsText.Text = $"읽을 줄 {_parsed.Count} · 섹션 {sections} · 읽는 시간 약 {(int)t.TotalMinutes}분 {t.Seconds:00}초"
                       + (longCount > 0 ? $" · {ScriptDoc.LongLine}자 넘는 줄 {longCount}개 (긴 줄 나누기)" : "");
        StatsText.Foreground = longCount > 0 ? _yellow : _muted;
    }

    /// <summary>오른쪽 줄 목록: 섹션 머리 + 번호 붙은 줄. 지금 줄은 teal 테두리, 긴 줄은 노란 번호.</summary>
    void RenderLineList()
    {
        LineList.Children.Clear();
        string section = "\0";
        int cur = _panel.ScriptIndex;
        FrameworkElement? curRow = null;
        for (int i = 0; i < _parsed.Count; i++)
        {
            var l = _parsed[i];
            if (l.Section != section)
            {
                section = l.Section;
                if (section != "")
                    LineList.Children.Add(new TextBlock
                    {
                        Text = "## " + section, FontSize = 11, Foreground = _teal, FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, LineList.Children.Count == 0 ? 0 : 8, 0, 4),
                    });
            }
            bool isLong = l.Text.Length > ScriptDoc.LongLine;
            var row = new Border
            {
                Background = i == cur ? _panelBg : _raised,
                BorderBrush = i == cur ? _teal : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = Cursors.Hand,
                Tag = i,
                ToolTip = l.Cue != "" ? "> " + l.Cue : null,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var num = new TextBlock
            {
                Text = (i + 1).ToString(), FontFamily = (FontFamily)FindResource("CodeFont"), FontSize = 11,
                Foreground = isLong ? _yellow : _muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            };
            var text = new TextBlock { Text = (l.Cue != "" ? "› " : "") + l.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = _cream };
            Grid.SetColumn(text, 1);
            grid.Children.Add(num);
            grid.Children.Add(text);
            row.Child = grid;
            row.MouseLeftButtonUp += (s, _) => _panel.JumpToLine((int)((Border)s).Tag);
            LineList.Children.Add(row);
            if (i == cur) curRow = row;
        }
        curRow?.BringIntoView();
    }

    void OnPanelLineChanged()
    {
        // 줄만 바뀐 경우: 테두리만 옮긴다 (목록 전체를 다시 그리지 않음)
        int cur = _panel.ScriptIndex;
        foreach (var child in LineList.Children)
            if (child is Border { Tag: int i } b)
            {
                bool on = i == cur;
                b.Background = on ? _panelBg : _raised;
                b.BorderBrush = on ? _teal : Brushes.Transparent;
                if (on) b.BringIntoView();
            }
        HeardText.Text = "-";
        HeardBar.Width = 0;
    }

    // ── 넣기 도우미 ──

    int LineStart(int caret) => caret <= 0 ? 0 : Editor.Text.LastIndexOf('\n', caret - 1) + 1;

    void InsertLineAbove(string prefix, string placeholder)
    {
        int start = LineStart(Editor.CaretIndex);
        Editor.Text = Editor.Text.Insert(start, prefix + placeholder + "\n");
        Editor.Select(start + prefix.Length, placeholder.Length);
        Editor.Focus();
    }

    void OnInsertSection(object sender, RoutedEventArgs e) => InsertLineAbove("## ", "섹션 이름");
    void OnInsertCue(object sender, RoutedEventArgs e) => InsertLineAbove("> ", "메모");

    void OnInsertEmphasis(object sender, RoutedEventArgs e)
    {
        int at = Editor.SelectionStart, len = Editor.SelectionLength;
        string sel = Editor.SelectedText;
        Editor.SelectedText = "**" + (len > 0 ? sel : "강조") + "**";
        Editor.Select(at + 2, len > 0 ? len : 2);
        Editor.Focus();
    }

    void OnSplitLong(object sender, RoutedEventArgs e)
    {
        var (text, n) = ScriptDoc.SplitLongLines(Editor.Text);
        if (n == 0) { StatsText.Text = "나눌 긴 줄이 없습니다."; return; }
        int caret = Editor.CaretIndex;
        Editor.Text = text;
        Editor.CaretIndex = Math.Min(caret, text.Length);
    }

    void OnCopyPrompt(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ScriptDoc.OptimizePrompt);
            StatsText.Text = "프롬프트를 복사했습니다. AI 채팅에 붙여 넣고 끝에 원고를 넣으세요. 결과는 '붙여넣어 바꾸기'로 가져옵니다.";
            StatsText.Foreground = _teal;
        }
        catch (Exception ex) { StatsText.Text = $"복사하지 못했습니다. {ex.Message}"; }
    }

    /// <summary>AI 결과를 클립보드에서 가져와 바꾼다. ``` 코드 블록 표시가 있으면 안쪽만.</summary>
    void OnPasteReplace(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrWhiteSpace(text)) { StatsText.Text = "클립보드가 비어 있습니다."; return; }
        var lines = text.Replace("\r", "").Split('\n').ToList();
        int open = lines.FindIndex(l => l.TrimStart().StartsWith("```"));
        if (open >= 0)
        {
            int close = lines.FindIndex(open + 1, l => l.TrimStart().StartsWith("```"));
            lines = lines.Skip(open + 1).Take((close > open ? close : lines.Count) - open - 1).ToList();
        }
        if (_dirty && MessageBox.Show(this, "지금 내용을 클립보드 내용으로 바꿀까요?", "대본 편집",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        Editor.Text = string.Join("\n", lines).Trim() + "\n";
    }

    // ── 화면 표시 ──

    void OnPos(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string pos }) return;
        _s.ScriptPos = pos;
        _panel.ApplyScriptDisplay();
    }

    void OnSmaller(object sender, RoutedEventArgs e) => SetScale(_s.ScriptScale - 0.1);
    void OnBigger(object sender, RoutedEventArgs e) => SetScale(_s.ScriptScale + 0.1);

    void SetScale(double v)
    {
        _s.ScriptScale = Math.Round(Math.Clamp(v, 0.6, 2.0), 1);
        UpdateDisplayButtons();
        _panel.ApplyScriptDisplay();
    }

    void OnNextCount(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string n }) return;
        _s.ScriptNextCount = int.Parse(n);
        _panel.ApplyScriptDisplay();
    }

    void OnTogglePrev(object sender, RoutedEventArgs e) { _s.ScriptShowPrev = !_s.ScriptShowPrev; UpdateDisplayButtons(); _panel.ApplyScriptDisplay(); }
    void OnToggleSection(object sender, RoutedEventArgs e) { _s.ScriptShowSection = !_s.ScriptShowSection; UpdateDisplayButtons(); _panel.ApplyScriptDisplay(); }
    void OnToggleCue(object sender, RoutedEventArgs e) { _s.ScriptShowCue = !_s.ScriptShowCue; UpdateDisplayButtons(); _panel.ApplyScriptDisplay(); }

    void UpdateDisplayButtons()
    {
        ScaleText.Text = $"{_s.ScriptScale * 100:0}%";
        Toggle(ShowPrevBtn, "이전 줄", _s.ScriptShowPrev);
        Toggle(ShowSectionBtn, "섹션", _s.ScriptShowSection);
        Toggle(ShowCueBtn, "메모", _s.ScriptShowCue);
    }

    void Toggle(Button b, string name, bool on)
    {
        b.Content = $"{name} {(on ? "ON" : "OFF")}";
        b.Foreground = on ? _teal : _muted;
    }

    // ── 자동스크롤 [AI] ──

    void OnVoiceToggle(object sender, RoutedEventArgs e)
    {
        _panel.ToggleVoice();
        UpdateVoiceButton();
    }

    void UpdateVoiceButton()
    {
        VoiceBtn.Content = _s.ScriptVoice ? "ON" : "OFF";
        VoiceBtn.Foreground = _s.ScriptVoice ? _teal : _danger;
    }

    void OnSensitivity(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string sens }) return;
        _s.ScriptSensitivity = sens;
        _s.Save();
    }

    void OnHeardUpdate(string heard, double score)
    {
        HeardText.Text = heard.Length == 0 ? "-" : heard;
        HeardBar.Width = Math.Clamp(score, 0, 1) * Math.Max(0, ((FrameworkElement)HeardBar.Parent).ActualWidth);
        HeardBar.Background = score >= 0.85 ? _teal : score >= 0.5 ? _yellow : _muted;
        UpdateVoiceButton();
    }
}
