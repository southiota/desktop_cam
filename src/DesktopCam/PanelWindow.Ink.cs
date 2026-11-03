using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 펜 · 형광펜(그림 창), 패널 닫기 → 미니 바, 숫자 단축키.
/// 단축키: Ctrl+Alt+1 커서 · 2 펜 · 3 형광펜 · 4 색 고르기 · 5 지우기 · 9 대본 이전 줄 · 0 다음 줄 (언제나).
/// 마우스가 녹화 영역 안이면 Ctrl+L ; ' . / · Ctrl+← → 와 Shift+우클릭 메뉴도 (InputHook).
/// 펜 · 형광펜을 쓰는 중에는 그림 창이 포커스를 가지므로 숫자만 눌러도 된다 (다른 프로그램의 숫자 입력은 막지 않음).
/// </summary>
public partial class PanelWindow
{
    const int HotkeyTool1 = 21;                 // 21~25 = Ctrl+Alt+1~5
    const int HotkeyScript9 = 29, HotkeyScript0 = 30;

    InkWindow? _ink;
    MiniBarWindow? _mini;
    ColorPickerWindow? _picker;
    IntPtr _beforeDraw;   // 펜을 켜기 전에 쓰던 창 (커서로 돌아가면 포커스를 돌려준다)

    /// <summary>패널 위에 뜨는 작은 창들의 주인. 그림 창보다 위에 있어야 하는 창(대본 · 후원 안내 · 미니 바)은 이 창을 주인으로 둔다.</summary>
    Window TopOwner => (Window?)_ink ?? _overlay;

    Color PenColor
    {
        get
        {
            try { return (Color)ColorConverter.ConvertFromString(_settings.PenColor); }
            catch { return Colors.Red; }
        }
    }

    /// <summary>OnSourceInitialized 에서 ApplyRegion 전에 부른다.</summary>
    void CreateInk(List<string> failed)
    {
        _ink = new InkWindow { Owner = _overlay };
        _ink.KeyPressed += OnInkKey;
        _ink.Show();
        _ink.SetTool(InkTool.Cursor, PenColor);

        for (int i = 0; i < 5; i++)
            if (!RegisterHotKey(_hwnd, HotkeyTool1 + i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)('1' + i))) failed.Add($"Ctrl+Alt+{i + 1}");
        if (!RegisterHotKey(_hwnd, HotkeyScript9, MOD_CONTROL | MOD_ALT, (uint)'9')) failed.Add("Ctrl+Alt+9");
        if (!RegisterHotKey(_hwnd, HotkeyScript0, MOD_CONTROL | MOD_ALT, (uint)'0')) failed.Add("Ctrl+Alt+0");
    }

    void UnregisterInkHotkeys()
    {
        for (int i = 0; i < 5; i++) UnregisterHotKey(_hwnd, HotkeyTool1 + i);
        UnregisterHotKey(_hwnd, HotkeyScript9);
        UnregisterHotKey(_hwnd, HotkeyScript0);
    }

    /// <summary>WndProc 에서: 처리했으면 true.</summary>
    bool OnInkHotkey(int id)
    {
        if (id >= HotkeyTool1 && id < HotkeyTool1 + 5) { DoTool(id - HotkeyTool1 + 1); return true; }
        if (id == HotkeyScript9) { SetScriptLine(_settings.ScriptLine - 1); return true; }
        if (id == HotkeyScript0) { SetScriptLine(_settings.ScriptLine + 1); return true; }
        return false;
    }

    /// <summary>펜 · 형광펜 중 그림 창에서 숫자만 눌렀을 때.</summary>
    void OnInkKey(Key key)
    {
        if (key == Key.Escape) { DoTool(1); return; }
        int n = key - Key.D0;
        if (n is >= 1 and <= 5) DoTool(n);
        else if (n == 9) SetScriptLine(_settings.ScriptLine - 1);
        else if (n == 0) SetScriptLine(_settings.ScriptLine + 1);
    }

    /// <summary>1 커서 · 2 펜 · 3 형광펜 · 4 색 고르기 · 5 전부 지우기.</summary>
    void DoTool(int n)
    {
        if (_ink == null) return;
        switch (n)
        {
            case 1: SetTool(InkTool.Cursor); break;
            case 2: SetTool(InkTool.Pen); break;
            case 3: SetTool(InkTool.Highlighter); break;
            case 4: ShowColorPicker(); break;
            case 5: _ink.Clear(); break;
        }
        _mini?.SetTool(_ink.Tool, PenColor);
    }

    void SetTool(InkTool tool)
    {
        if (_ink == null) return;
        var was = _ink.Tool;
        if (was == InkTool.Cursor && tool != InkTool.Cursor)
        {
            var fg = GetForegroundWindow();
            _beforeDraw = fg == _ink.Handle ? IntPtr.Zero : fg;
        }
        _ink.SetTool(tool, PenColor);
        if (tool == InkTool.Cursor && was != InkTool.Cursor) RestoreFocus();
    }

    void RestoreFocus()
    {
        if (_beforeDraw != IntPtr.Zero && IsWindow(_beforeDraw)) ForceForeground(_beforeDraw);
    }

    /// <summary>오피스 스타일 색 고르기를 커서 오른쪽 아래에 띄운다. 고르면 다음 획부터 그 색.</summary>
    void ShowColorPicker()
    {
        if (_ink == null) return;
        if (_picker != null) { _picker.Activate(); return; }
        var fg = GetForegroundWindow();
        if (_ink.Tool == InkTool.Cursor && fg != _ink.Handle) _beforeDraw = fg;
        _picker = new ColorPickerWindow(PenColor) { Owner = TopOwner };
        _picker.Picked += c =>
        {
            _settings.PenColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _settings.Save();
            _mini?.SetTool(_ink.Tool, PenColor);
        };
        _picker.Closed += (_, _) =>
        {
            _picker = null;
            // 포커스 돌려주기: 그리는 중이면 그림 창(숫자 키가 계속 되게), 아니면 쓰던 창
            if (_ink.Tool != InkTool.Cursor) _ink.SetTool(_ink.Tool, PenColor);
            else RestoreFocus();
        };
        _picker.ShowAtCursor();
    }


    // ── 패널 닫기 ↔ 미니 바 ──

    bool _collapsed;

    void OnCollapseClick(object sender, RoutedEventArgs e) => Collapse();

    void Collapse()
    {
        if (_collapsed) return;
        _collapsed = true;
        _settings.PanelCollapsed = true;
        _settings.Save();
        EnsureMini();
        _mini!.Show();
        _mini.Place(_panelPx.Left, _panelPx.Top);   // 처음 자리 = 패널 왼쪽 위
        SyncMini();
        Hide();
        _overlay.HidePanelShadow();
    }

    void Expand()
    {
        if (!_collapsed) return;
        _collapsed = false;
        _settings.PanelCollapsed = false;
        _settings.Save();
        _mini?.Hide();
        Show();
        ApplyRegion();
    }

    void EnsureMini()
    {
        if (_mini != null) return;
        _mini = new MiniBarWindow { Owner = TopOwner };
        _mini.RecClicked += ToggleRecording;
        _mini.PauseClicked += TogglePause;
        _mini.PrevClicked += () => SetScriptLine(_settings.ScriptLine - 1);
        _mini.NextClicked += () => SetScriptLine(_settings.ScriptLine + 1);
        _mini.ToolClicked += DoTool;
        _mini.ExpandClicked += Expand;
        // 십자 아이콘: 패널 머리 칸처럼 녹화 영역을 옮긴다 (패널 자리 · 미니 바도 ApplyRegion 에서 같이)
        _mini.CanDrag = () => !_recordingUi;
        _mini.DragStarted += BeginRegionDrag;
        _mini.DragMoved += RegionDragMove;
        _mini.DragEnded += EndRegionDrag;
    }

    /// <summary>미니 바 표시를 지금 상태에 맞춘다 (녹화 · 펜).</summary>
    void SyncMini()
    {
        if (_mini == null) return;
        bool paused = _recorder.Status == ScreenRecorderLib.RecorderStatus.Paused;
        _mini.SetRecording(_recordingUi, paused, RecBtn.IsEnabled);
        _mini.SetTool(_ink?.Tool ?? InkTool.Cursor, PenColor);
        if (_recordingUi) _mini.SetTime(TimerText.Text);
    }

    /// <summary>MeterTick(50ms) 에서: 미니 바가 보일 때만 막대와 녹화 표시를 갱신.</summary>
    void MiniTick(float sp, bool sysOff, float mp, bool micOff)
    {
        if (_mini is not { IsVisible: true }) return;
        _mini.PushLevels(sp, sysOff, mp, micOff);
        if (_statusTick % 4 == 0) SyncMini();
    }

    // ── 녹화 영역 안 Ctrl 단축키 · Shift+우클릭 메뉴 ──
    // 마우스가 녹화 영역 안에 있을 때만 키를 가져가고, 밖이면 원래 프로그램으로 보낸다.
    // Ctrl+← 이전 줄 · Ctrl+→ 다음 줄 · Ctrl+L 커서 · Ctrl+; 펜 · Ctrl+' 형광펜 · Ctrl+. 색 · Ctrl+/ 지우기

    InputHook? _input;
    ToolMenuWindow? _menu;

    void CreateInputHook(List<string> failed)
    {
        _input = new InputHook();
        if (!_input.Installed) { failed.Add("영역 안 Ctrl 단축키 · Shift+우클릭"); return; }
        _input.CtrlKey = (vk, p) =>
        {
            int a = KeyAction(vk);
            if (a == 0 || !InRegion(p) || OwnWindowInFront()) return false;
            Dispatcher.BeginInvoke(() => RunAction(a));
            return true;
        };
        _input.ShiftRightDown = p => InRegion(p) && !OwnWindowInFront();
        _input.ShiftRightClicked += p => Dispatcher.BeginInvoke(() => ShowToolMenu(p));
    }

    /// <summary>가상 키 → 동작 번호 (1~5 = 도구, 9 = 이전 줄, 10 = 다음 줄, 0 = 안 씀).</summary>
    static int KeyAction(int vk) => vk switch
    {
        0x25 => 9,    // ←
        0x27 => 10,   // →
        0x4C => 1,    // L
        0xBA => 2,    // ;
        0xDE => 3,    // '
        0xBE => 4,    // .
        0xBF => 5,    // /
        _ => 0,
    };

    void RunAction(int a)
    {
        if (a is >= 1 and <= 5) DoTool(a);
        else if (a == 9) SetScriptLine(_settings.ScriptLine - 1);
        else if (a == 10) SetScriptLine(_settings.ScriptLine + 1);
    }

    bool InRegion(POINT p) => p.X >= _region.Left && p.X < _region.Right && p.Y >= _region.Top && p.Y < _region.Bottom;

    /// <summary>이 프로그램의 다른 창(대본 편집기 · 색 고르기 · 메뉴 등)이 앞에 있으면 키를 가져가지 않는다 (글자 입력 중 Ctrl+← 등). 그림 창은 예외.</summary>
    bool OwnWindowInFront()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || (_ink != null && fg == _ink.Handle)) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    void ShowToolMenu(POINT p)
    {
        if (_ink == null || _menu != null) return;
        var fg = GetForegroundWindow();
        var tool = _ink.Tool;
        _menu = new ToolMenuWindow(new ToolMenuWindow.Item[]
        {
            new("◀ 이전 줄", "Ctrl+←", () => RunAction(9)),
            new("▶ 다음 줄", "Ctrl+→", () => RunAction(10)),
            new("커서", "Ctrl+L", () => RunAction(1), tool == InkTool.Cursor, GapBefore: true),
            new("펜", "Ctrl+;", () => RunAction(2), tool == InkTool.Pen),
            new("형광펜", "Ctrl+'", () => RunAction(3), tool == InkTool.Highlighter),
            new("색 고르기", "Ctrl+.", () => RunAction(4)),
            new("전부 지우기", "Ctrl+/", () => RunAction(5)),
        }) { Owner = TopOwner };
        _menu.Closed += (_, _) =>
        {
            var chosen = _menu?.Chosen;
            _menu = null;
            // 메뉴를 띄우기 전 창으로 포커스를 먼저 돌려 둔다 (펜을 켜면 그 창을 기억했다가 커서로 돌아올 때 돌려줌)
            if (fg != IntPtr.Zero && IsWindow(fg)) ForceForeground(fg);
            chosen?.Run();
            if (_ink.Tool != InkTool.Cursor && _picker == null) _ink.SetTool(_ink.Tool, PenColor);   // 그리는 중이면 그림 창이 다시 키를 받게
        };
        _menu.ShowAt(p);
    }
}
