using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using static DesktopCam.Native;

namespace DesktopCam;

public record Alert(string Tag, string Message, Brush Color);

/// <summary>녹화 영역 오른쪽에 붙는 설정 패널.</summary>
public partial class PanelWindow : Window
{
    const int PanelGap = 24;          // 녹화 영역 오른쪽 끝과 패널 사이 (실제 픽셀)
    const int PanelHeightPx = 1080;   // 패널 높이는 HD 세로 크기로 고정
    const int PanelWidthPx = 440;
    const int HotkeyFit = 1, HotkeyMin = 2, HotkeyRec = 3, HotkeyPause = 4;
    const long MinFreeBytes = 2L * 1024 * 1024 * 1024;   // 이보다 여유 공간이 적으면 녹화를 시작하지 않음

    readonly OverlayWindow _overlay;
    readonly Settings _settings;
    readonly WindowFitter _fitter = new();
    readonly ObservableCollection<WinItem> _items = new();
    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    readonly AudioMonitor _audio = new();
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly ObservableCollection<Alert> _alerts = new();
    static readonly TimeSpan MicSilenceLimit = TimeSpan.FromSeconds(10);
    const float SilencePeak = 0.002f;   // 약 -54dB 아래는 소리 없음으로 본다
    const float ClipPeak = 0.99f;

    // 자주 쓰는 색은 한 번만 찾아 둔다 (50ms 마다 FindResource 하지 않게)
    Brush _teal = Brushes.Transparent, _yellow = Brushes.Transparent, _danger = Brushes.Transparent,
          _bgPanel = Brushes.Transparent, _onAccent = Brushes.Transparent, _cream = Brushes.Transparent;
    long _freeCache = -1;
    DateTime _freeCheckedAt, _muteCheckedAt;
    bool _micMutedCache;

    RECT _region;
    IntPtr _hwnd, _lastMinimized;
    DateTime _micLastSound = DateTime.Now, _sysClipUntil, _micClipUntil;
    bool _ready, _fillingCombos, _sysMissing, _micMissing;
    string _alertKey = "", _hotkeyFail = "";
    int _statusTick;
    bool _taskbarInside;

    readonly RegionRecorder _recorder = new();
    readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DateTime _recStarted, _pauseStarted;
    TimeSpan _pausedTotal;
    string? _recPath, _lastFile;
    bool _recordingUi, _closeAfterStop;

    public PanelWindow(OverlayWindow overlay, Settings settings)
    {
        _overlay = overlay;
        _settings = settings;
        StartupTrace.Mark("panel ctor");
        InitializeComponent();
        StartupTrace.Mark("panel InitializeComponent");
        _teal = (Brush)FindResource("Teal");
        _yellow = (Brush)FindResource("Yellow");
        _danger = (Brush)FindResource("Danger");
        _bgPanel = (Brush)FindResource("BgPanel");
        _onAccent = (Brush)FindResource("OnAccent");
        _cream = (Brush)FindResource("Cream");
        WinList.ItemsSource = _items;

        (_settings.Preset switch { "720" => Res720Btn, "HD" => HdBtn, _ => QhdBtn }).IsChecked = true;
        (_settings.Fps == 30 ? Fps30 : Fps60).IsChecked = true;
        DimSlider.Value = _settings.Dim;
        DimText.Text = $"{_settings.Dim}%";

        _refreshTimer.Tick += (_, _) => RefreshList();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        Alerts.ItemsSource = _alerts;
        _audio.DevicesChanged += () => Dispatcher.BeginInvoke(ReloadDevices);
        _meterTimer.Tick += (_, _) => MeterTick();
        UpdateAudioToggles();

        _recorder.StatusChanged += s => Dispatcher.BeginInvoke(() => OnRecorderStatus(s));
        _recorder.Completed += p => Dispatcher.BeginInvoke(() => OnRecordingDone(p, null));
        _recorder.Failed += (err, p) => Dispatcher.BeginInvoke(() => OnRecordingDone(p, err));
        _recTimer.Tick += (_, _) => UpdateRecTimer();
        Closing += OnClosingWindow;
        FolderText.Text = _settings.OutputFolder;
        FolderText.ToolTip = _settings.OutputFolder;
        _ready = true;
        InitScriptAndMonitor();
        UpdateFxLabel();
        StartupTrace.Mark("panel ctor done (script loaded)");
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        int square = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));

        var failed = new List<string>();
        if (!RegisterHotKey(_hwnd, HotkeyFit, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'F')) failed.Add("Ctrl+Alt+F");
        if (!RegisterHotKey(_hwnd, HotkeyMin, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'M')) failed.Add("Ctrl+Alt+M");
        if (!RegisterHotKey(_hwnd, HotkeyRec, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'R')) failed.Add("Ctrl+Alt+R");
        if (!RegisterHotKey(_hwnd, HotkeyPause, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'P')) failed.Add("Ctrl+Alt+P");
        if (!RegisterHotKey(_hwnd, HotkeyScript, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)'S')) failed.Add("Ctrl+Alt+S");
        if (!RegisterHotKey(_hwnd, HotkeyNext, MOD_CONTROL | MOD_ALT, (uint)'N')) failed.Add("Ctrl+Alt+N");
        if (!RegisterHotKey(_hwnd, HotkeyPrev, MOD_CONTROL | MOD_ALT, (uint)'B')) failed.Add("Ctrl+Alt+B");
        if (failed.Count > 0) _hotkeyFail = $"{string.Join(", ", failed)} 단축키를 다른 프로그램이 쓰고 있어 쓸 수 없습니다.";

        StartupTrace.Mark("hotkeys");
        ApplyRegion();
        StartupTrace.Mark("ApplyRegion");
        UpdateHideMinButton();
        RefreshList();
        StartupTrace.Mark("RefreshList");
        _refreshTimer.Start();
        HookWindowEvents();
        ReloadDevices();
        StartupTrace.Mark("ReloadDevices started (background)");
        _meterTimer.Start();
        ApplyScriptVisibility();
        StartupTrace.Mark("ApplyScriptVisibility");
        CreateHelpButton();
        RepairInterruptedRecording();
        ClipCheck.Run(this, "패널");
        if (Environment.GetEnvironmentVariable("DESKTOPCAM_OPEN_EDITOR") == "1") OnScriptEditor(this, new RoutedEventArgs());   // 확인용
        if (Environment.GetEnvironmentVariable("DESKTOPCAM_OPEN_AUDIO") == "1") OnAudioSettings(this, new RoutedEventArgs());     // 확인용
        if (Environment.GetEnvironmentVariable("DESKTOPCAM_TOUR") == "1") Dispatcher.BeginInvoke(StartTour, System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // 확인용
        if (Environment.GetEnvironmentVariable("DESKTOPCAM_GET_MODEL") == "1") _ = DownloadModelAsync();   // 확인용: 묻지 않고 모델 받기
    }

    // ── 비정상 종료된 녹화 복구 ──
    // 녹화를 시작하면 표시 파일을 남기고, 정상으로 끝나면 지운다.
    // 다음에 켰을 때 표시가 남아 있으면 그 녹화는 PC 가 꺼지거나 강제로 끝난 것이므로 파일을 고친다.

    static string LockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopCAM", "recording.lock");

    static void DeleteRecordingLock()
    {
        try { File.Delete(LockPath); } catch { }
    }


    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(_hwnd, HotkeyFit);
        UnregisterHotKey(_hwnd, HotkeyMin);
        UnregisterHotKey(_hwnd, HotkeyRec);
        UnregisterHotKey(_hwnd, HotkeyPause);
        UnregisterHotKey(_hwnd, HotkeyScript);
        UnregisterHotKey(_hwnd, HotkeyNext);
        UnregisterHotKey(_hwnd, HotkeyPrev);
        DisposeScriptAndMonitor();
        _recorder.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _meterTimer.Stop();
        _audio.Dispose();
        _settings.Save();
        _overlay.Close();
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == HotkeyFit) OnHotkeyFit();
            else if (wParam.ToInt32() == HotkeyMin) OnHotkeyMin();
            else if (wParam.ToInt32() == HotkeyRec) ToggleRecording();
            else if (wParam.ToInt32() == HotkeyPause) TogglePause();
            else if (wParam.ToInt32() == HotkeyScript) ToggleScript();
            else if (wParam.ToInt32() == HotkeyNext) SetScriptLine(_settings.ScriptLine + 1);
            else if (wParam.ToInt32() == HotkeyPrev) SetScriptLine(_settings.ScriptLine - 1);
            handled = true;
        }
        return IntPtr.Zero;
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyRegion);

    // ── 녹화 영역 ──

    /// <summary>설정에 맞춰 영역을 계산하고 오버레이와 패널 위치를 갱신한다.</summary>
    void ApplyRegion()
    {
        var (mon, work) = GetPrimaryMonitor();

        // 모니터보다 큰 프리셋은 고를 수 없게 (녹화 중이면 잠금이 우선)
        if (!_recordingUi)
        {
            QhdBtn.IsEnabled = mon.Width >= 2560 && mon.Height >= 1440;
            HdBtn.IsEnabled = mon.Width >= 1920 && mon.Height >= 1080;
        }
        if (_settings.Width > mon.Width || _settings.Height > mon.Height)
        {
            _settings.Preset = mon.Width >= 1920 && mon.Height >= 1080 ? "HD" : "720";
            _ready = false; (_settings.Preset == "HD" ? HdBtn : Res720Btn).IsChecked = true; _ready = true;
        }

        int w = Math.Min(_settings.Width, mon.Width), h = Math.Min(_settings.Height, mon.Height);
        // 영역이 모니터 밖으로 나가지 않게
        _settings.OffsetX = Math.Clamp(_settings.OffsetX, 0, mon.Width - w);
        _settings.OffsetY = Math.Clamp(_settings.OffsetY, 0, mon.Height - h);
        int x = mon.Left + _settings.OffsetX, y = mon.Top + _settings.OffsetY;
        _region = new RECT(x, y, x + w, y + h);
        _overlay.SetRegion(_region, _settings.Dim / 100.0, _recordingUi);

        PosText.Text = $"{_settings.OffsetX},{_settings.OffsetY}";
        if (!_recordingUi) UpdateRecMeta();
        _taskbarInside = work.Left > _region.Left || work.Top > _region.Top
                      || work.Right < _region.Right || work.Bottom < _region.Bottom;

        // 패널: 녹화 영역 오른쪽에서 PanelGap 만큼 떨어진 곳. 자리가 없으면 왼쪽, 그래도 없으면 화면 오른쪽 끝.
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        const int ShadowPx = 8;   // 오버레이가 패널 뒤에 그리는 그림자
        double widthPx = PanelWidthPx;
        double needPx = widthPx + ShadowPx;
        double leftPx = _region.Right + PanelGap + needPx <= mon.Right ? _region.Right + PanelGap
                      : _region.Left - PanelGap - needPx >= mon.Left ? _region.Left - PanelGap - needPx
                      : mon.Right - needPx;
        // 높이는 HD 세로 1080 고정. 영역 위쪽에 맞추되 작업표시줄 위로 넘치지 않게 (그림자 자리 포함).
        double heightPx = Math.Min(PanelHeightPx, work.Height - ShadowPx);
        double topPx = Math.Clamp(_region.Top, work.Top, work.Bottom - heightPx - ShadowPx);
        Left = leftPx / s;
        Top = topPx / s;
        Width = widthPx / s;
        Height = heightPx / s;
        _overlay.SetPanelShadow(leftPx, topPx, widthPx, heightPx);
        _panelPx = new Rect(leftPx, topPx, widthPx, heightPx);
        _workArea = work;
        PlaceScript();
        PlaceHelpButton();
    }

    // ── 패널 머리를 끌어서 녹화 영역 옮기기 ──

    const int SnapPx = 16;   // 화면 가장자리에 이만큼 가까우면 달라붙음
    bool _dragging;
    POINT _dragCursor;
    int _dragX, _dragY;
    List<IntPtr> _dragFitted = new();

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (_recordingUi) { e.Handled = true; return; }   // 녹화 중에는 영역 고정
        GetCursorPos(out _dragCursor);
        _dragX = _settings.OffsetX;
        _dragY = _settings.OffsetY;
        // 맞춰 둔 창은 놓았을 때 새 위치로 따라오게
        _dragFitted = _items.Where(i => _fitter.IsFitted(i.Handle, _region)).Select(i => i.Handle).ToList();
        _dragging = true;
        Header.CaptureMouse();
        e.Handled = true;
    }

    void OnHeaderMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        GetCursorPos(out var p);
        var (mon, _) = GetPrimaryMonitor();
        int maxX = mon.Width - _region.Width, maxY = mon.Height - _region.Height;
        int x = Snap(_dragX + p.X - _dragCursor.X, maxX);
        int y = Snap(_dragY + p.Y - _dragCursor.Y, maxY);
        if (x == _settings.OffsetX && y == _settings.OffsetY) return;
        _settings.OffsetX = x;
        _settings.OffsetY = y;
        if (_dragPending) return;
        _dragPending = true;
        CompositionTarget.Rendering += OnDragFrame;
    }

    bool _dragPending;

    void OnDragFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnDragFrame;
        _dragPending = false;
        ApplyRegion();
    }

    static int Snap(int v, int max) => v <= SnapPx ? 0 : v >= max - SnapPx ? max : v;

    void OnHeaderUp(object sender, MouseButtonEventArgs e) => Header.ReleaseMouseCapture();

    void OnHeaderLost(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_dragPending) OnDragFrame(null, EventArgs.Empty);   // 마지막 위치 반영
        _settings.Save();
        foreach (var h in _dragFitted) _fitter.Fit(h, _region);
        if (_dragFitted.Count > 0) SetStatus("OK", $"맞춰 둔 창 {_dragFitted.Count}개를 새 위치로 옮겼습니다.");
        RefreshList();
    }

    void OnResetPosition(object sender, RoutedEventArgs e)
    {
        var fitted = _items.Where(i => _fitter.IsFitted(i.Handle, _region)).Select(i => i.Handle).ToList();
        _settings.OffsetX = _settings.OffsetY = 0;
        ApplyRegion();
        _settings.Save();
        foreach (var h in fitted) _fitter.Fit(h, _region);
        RefreshList();
    }

    void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var oldRegion = _region;
        // 이미 맞춰 둔 창은 새 크기로 다시 맞춘다
        var fitted = _items.Where(i => _fitter.IsFitted(i.Handle, oldRegion)).Select(i => i.Handle).ToList();

        _settings.Preset = (sender as RadioButton)?.Tag as string ?? "HD";
        _settings.Save();
        ApplyRegion();

        foreach (var h in fitted) _fitter.Fit(h, _region);
        if (fitted.Count > 0) SetStatus("OK", $"맞춰 둔 창 {fitted.Count}개를 {_region.Width}×{_region.Height}로 다시 맞췄습니다.");
        RefreshList();
    }

    void OnFpsChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.Fps = sender == Fps30 ? 30 : 60;
        _settings.Save();
        ApplyRegion();
    }

    void OnDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Dim = (int)DimSlider.Value;
        DimText.Text = $"{_settings.Dim}%";
        _overlay.SetRegion(_region, _settings.Dim / 100.0, _recordingUi);
        _settings.Save();
    }

    // ── 창 목록 ──

    /// <summary>
    /// 목록을 다시 읽는다. 순서: 지금 쓰는 창(맨 앞) → 최근에 쓴 창 순 → 최소화된 창.
    /// 기존 줄은 지우지 않고 자리만 옮겨 값만 고친다 (버튼을 누르는 도중 줄이 새로 생기지 않게).
    /// </summary>
    void RefreshList()
    {
        // EnumWindows 는 앞에 있는 창부터 준다
        var wins = WindowFitter.ListWindows();
        wins = wins.Where(w => !w.Minimized).Concat(_settings.HideMinimized ? [] : wins.Where(w => w.Minimized)).ToList();
        var alive = wins.Select(w => w.Handle).ToHashSet();
        for (int i = _items.Count - 1; i >= 0; i--)
            if (!alive.Contains(_items[i].Handle)) _items.RemoveAt(i);

        for (int at = 0; at < wins.Count; at++)
        {
            var w = wins[at];
            var item = _items.FirstOrDefault(x => x.Handle == w.Handle);
            if (item == null) { item = new WinItem { Handle = w.Handle }; _items.Insert(at, item); }
            else if (_items.IndexOf(item) is int cur && cur != at) _items.Move(cur, at);
            item.IsActive = at == 0 && !w.Minimized;
            item.Title = w.Title;
            item.IsMinimized = w.Minimized;
            item.IsFitted = _fitter.IsFitted(w.Handle, _region);
            if (w.Minimized) item.Sub = $"{w.Process} · 최소화됨";
            else
            {
                var r = GetFrameBounds(w.Handle);
                item.Sub = $"{w.Process} · {r.Width}×{r.Height}";
            }
        }
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 다른 창이 앞으로 오거나 최소화 · 복원되면 바로 다시 정렬 (3초 타이머만 기다리지 않게)
    WinEventProc? _winEvent;
    readonly List<IntPtr> _winHooks = new();
    bool _refreshQueued;

    void HookWindowEvents()
    {
        _winEvent = (_, _, h, idObject, _, _, _) =>
        {
            if (idObject != 0 || _refreshQueued) return;   // OBJID_WINDOW 만
            _refreshQueued = true;
            Dispatcher.BeginInvoke(() => { _refreshQueued = false; RefreshList(); }, DispatcherPriority.Background);
        };
        _winHooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEvent, 0, 0, WINEVENT_OUTOFCONTEXT));
        _winHooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _winEvent, 0, 0, WINEVENT_OUTOFCONTEXT));
        Closed += (_, _) => { foreach (var k in _winHooks) if (k != IntPtr.Zero) UnhookWinEvent(k); };
    }

    void OnHideMinClick(object sender, RoutedEventArgs e)
    {
        _settings.HideMinimized = !_settings.HideMinimized;
        _settings.Save();
        UpdateHideMinButton();
        RefreshList();
    }

    void UpdateHideMinButton() =>
        HideMinBtn.Content = _settings.HideMinimized ? "비활성 창 보이기" : "비활성 창 숨기기";

    /// <summary>위로: 창을 맨 앞으로. 최소화돼 있으면 먼저 연다.</summary>
    void OnTopClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WinItem item || !IsWindow(item.Handle)) return;
        if (IsIconic(item.Handle)) ShowWindow(item.Handle, SW_RESTORE);
        BringWindowToTop(item.Handle);
        SetForegroundWindow(item.Handle);
        RefreshList();
    }

    void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshList();

    void OnFitClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is WinItem item) FitOrRestore(item.Handle);
    }

    void OnMinClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WinItem item) return;
        if (IsIconic(item.Handle))
        {
            ShowWindow(item.Handle, SW_RESTORE);
            SetForegroundWindow(item.Handle);
        }
        else
        {
            ShowWindow(item.Handle, SW_MINIMIZE);
            _lastMinimized = item.Handle;
        }
        RefreshList();
    }

    /// <summary>맞춰져 있으면 되돌리고, 아니면 녹화 영역에 맞춘다.</summary>
    void FitOrRestore(IntPtr h)
    {
        string title = Shorten(GetTitle(h));
        if (_fitter.IsFitted(h, _region))
        {
            _fitter.Restore(h);
            SetStatus("OK", $"\"{title}\" 창을 원래 위치로 되돌렸습니다.");
        }
        else Fit(h, title);
        RefreshList();
    }

    void Fit(IntPtr h, string title)
    {
        var (ok, actual) = _fitter.Fit(h, _region);
        if (!ok)
            SetStatus("FAIL", $"\"{title}\" 창을 옮기지 못했습니다. 관리자 권한으로 실행된 창일 수 있습니다.");
        else if (!WindowFitter.Matches(actual, _region))
            SetStatus("WAIT", $"\"{title}\" 창은 크기 제한이 있어 {actual.Width}×{actual.Height}까지만 맞춰졌습니다.");
        else
            SetStatus("OK", $"\"{title}\" 창을 {_region.Width}×{_region.Height}로 맞췄습니다.");
    }

    // ── 단축키 ──

    void OnHotkeyFit()
    {
        var h = GetForegroundWindow();
        if (!WindowFitter.IsCandidate(h)) { SetStatus("WAIT", "맞출 창을 먼저 클릭한 뒤 누르세요."); return; }
        FitOrRestore(h);
    }

    void OnHotkeyMin()
    {
        var h = GetForegroundWindow();
        if (WindowFitter.IsCandidate(h) && !IsIconic(h))
        {
            ShowWindow(h, SW_MINIMIZE);
            _lastMinimized = h;
        }
        else if (_lastMinimized != IntPtr.Zero && IsWindow(_lastMinimized) && IsIconic(_lastMinimized))
        {
            ShowWindow(_lastMinimized, SW_RESTORE);
            SetForegroundWindow(_lastMinimized);
        }
        RefreshList();
    }

    // ── 소리 ──

    int _deviceReload;

    /// <summary>
    /// 장치 목록을 다시 읽고 저장된 장치를 고른다. 장치가 꽂히거나 빠질 때도 불린다.
    /// 목록은 뒤에서 한 번만 읽어 시스템·모니터 칸이 같이 쓴다 (화면은 그동안 멈추지 않음).
    /// </summary>
    async void ReloadDevices()
    {
        int version = ++_deviceReload;
        var (sys, mic) = await AudioMonitor.ListAllAsync();
        if (version != _deviceReload) return;   // 그사이 또 바뀌었으면 최신 것만
        _fillingCombos = true;

        _sysMissing = !_audio.SelectSystem(_settings.SystemDeviceId);
        SysCombo.ItemsSource = sys;
        SysCombo.SelectedItem = (_sysMissing ? null : sys.FirstOrDefault(d => d.Id == _settings.SystemDeviceId)) ?? sys[0];

        _captureDevices = mic;
        _micMissing = !_audio.SelectMic(_settings.MicDeviceId);
        MicCombo.ItemsSource = mic;
        MicCombo.SelectedItem = (_micMissing ? null : mic.FirstOrDefault(d => d.Id == _settings.MicDeviceId)) ?? mic[0];
        _micLastSound = DateTime.Now;

        _fillingCombos = false;
        ReloadMonitorDevices(sys);   // 헤드셋이 연결되거나 빠질 때도 여기로 온다
        StartupTrace.Mark("devices filled");
    }

    void OnSysDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCombos || SysCombo.SelectedItem is not AudioDevice d) return;
        _settings.SystemDeviceId = d.Id;
        _settings.SystemDeviceName = d.Id == null ? null : d.Name;
        _sysMissing = !_audio.SelectSystem(d.Id);
        ApplyMonitor();   // 모니터 장치와 같아졌는지 다시 본다
        _settings.Save();
    }

    void OnMicDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCombos || MicCombo.SelectedItem is not AudioDevice d) return;
        _settings.MicDeviceId = d.Id;
        _settings.MicDeviceName = d.Id == null ? null : d.Name;
        _micMissing = !_audio.SelectMic(d.Id);
        _micLastSound = DateTime.Now;
        ApplyMonitor(restart: true);   // 마이크 형식이 바뀌었을 수 있음
        _settings.Save();
    }

    void OnSysToggle(object sender, RoutedEventArgs e)
    {
        _settings.SystemEnabled = !_settings.SystemEnabled;
        UpdateAudioToggles();
        ApplyLiveAudio();
        ApplyMonitor();
        _settings.Save();
    }

    void OnMicToggle(object sender, RoutedEventArgs e)
    {
        _settings.MicEnabled = !_settings.MicEnabled;
        _micLastSound = DateTime.Now;
        UpdateAudioToggles();
        ApplyLiveAudio();
        _settings.Save();
    }

    void UpdateAudioToggles()
    {
        Brush teal = _teal, danger = _danger;
        SysToggle.Content = _settings.SystemEnabled ? "ON" : "OFF";
        SysToggle.Foreground = _settings.SystemEnabled ? teal : danger;
        MicToggle.Content = _settings.MicEnabled ? "ON" : "OFF";
        MicToggle.Foreground = _settings.MicEnabled ? teal : danger;
    }

    /// <summary>50ms 마다 음량 막대와 상태·경고를 갱신한다.</summary>
    void MeterTick()
    {
        var now = DateTime.Now;
        float sp = _audio.SystemPeak, mp = _audio.MicPeak;
        // Windows 음소거 여부는 자주 바뀌지 않으므로 0.5초마다만 확인
        if (now - _muteCheckedAt > TimeSpan.FromMilliseconds(500))
        {
            _micMutedCache = _audio.MicMutedInWindows;
            _muteCheckedAt = now;
        }
        bool micMutedWin = _micMutedCache;
        bool micLive = _settings.MicEnabled && _audio.HasMic && !micMutedWin;

        SysMeter.Push(sp, !_settings.SystemEnabled || !_audio.HasSystem);
        MicMeter.Push(mp, !micLive);

        if (sp >= ClipPeak) _sysClipUntil = now.AddSeconds(2);
        if (mp >= ClipPeak) _micClipUntil = now.AddSeconds(2);
        if (mp > SilencePeak || !micLive) _micLastSound = now;
        // 막대는 매번(50ms), 상태 글자·알림·머리 칸은 4번에 1번(200ms)만 — 사람 눈에는 차이가 없고 일이 1/4 로 준다.
        // 단 녹화 중에는 머리 칸 깜빡임(0.5초)이 밀리지 않게 머리 칸만 매번 본다.
        if (_recordingUi) UpdateHeaderState();
        if (++_statusTick % 4 != 0) return;

        bool sysClip = now < _sysClipUntil && _settings.SystemEnabled;
        bool micClip = now < _micClipUntil && micLive;
        bool micSilent = micLive && now - _micLastSound > MicSilenceLimit;

        Brush danger = _danger, yellow = _yellow, teal = _teal;

        // 막대 옆 상태: 꺼짐은 ON/OFF 버튼이 보여주므로 비워 둔다. 자세한 이유는 마우스를 올리면.
        var sysState = !_audio.HasSystem ? ("[ NONE ]", danger, "출력 장치가 없습니다.")
            : !_settings.SystemEnabled ? ("", teal, "")
            : sysClip ? ("[ CLIP ]", danger, "소리가 너무 커서 찢어질 수 있습니다. 볼륨을 낮추세요.")
            : ("[ OK ]", teal, "");
        var micState = !_audio.HasMic ? ("[ NONE ]", danger, "마이크가 연결되어 있지 않습니다.")
            : !_settings.MicEnabled ? ("", teal, "")
            : micMutedWin ? ("[ MUTE ]", danger, "Windows 소리 설정에서 마이크가 음소거되어 있습니다.")
            : micClip ? ("[ CLIP ]", danger, "마이크 소리가 너무 커서 찢어질 수 있습니다. 마이크 음량을 낮추세요.")
            : micSilent ? ("[ QUIET ]", yellow, "마이크에 10초 넘게 소리가 없습니다. 장치와 음량을 확인하세요.")
            : ("[ OK ]", teal, "");
        SetState(SysState, (sysState.Item1, sysState.Item2));
        SetState(MicState, (micState.Item1, micState.Item2));
        SysState.ToolTip = sysState.Item3 == "" ? null : sysState.Item3;
        MicState.ToolTip = micState.Item3 == "" ? null : micState.Item3;

        // 알림: 상태 표시만으로는 알 수 없는 것만 (같은 내용을 두 번 보이지 않게)
        var a = new List<Alert>();
        if (_hotkeyFail != "") a.Add(new("[ FAIL ] ", _hotkeyFail, danger));
        if (_taskbarInside) a.Add(new("[ WAIT ] ", "작업표시줄이 녹화 영역에 들어옵니다. 작업표시줄 자동 숨기기를 켜세요.", yellow));
        if (_audio.HasSystem && _sysMissing) a.Add(new("[ WAIT ] ", $"\"{_settings.SystemDeviceName}\" 장치가 없어 기본 장치를 씁니다.", yellow));
        if (_audio.HasMic && _micMissing) a.Add(new("[ WAIT ] ", $"\"{_settings.MicDeviceName}\" 마이크가 없어 기본 장치를 씁니다.", yellow));

        AppendScriptAlerts(a);

        // 내용이 바뀔 때만 다시 그린다 (깜빡임 방지)
        string key = string.Join("|", a.Select(x => x.Tag + x.Message));
        if (key != _alertKey)
        {
            _alertKey = key;
            _alerts.Clear();
            foreach (var x in a) _alerts.Add(x);
        }

        UpdateHeaderState();   // 녹화 중 머리 칸 깜빡임도 이 50ms 박자로 움직인다
    }

    /// <summary>
    /// 머리 칸 상태: 준비 / 녹화 중 / 일시정지.
    /// 녹화 중에는 머리 칸 바탕이 0.5초마다 danger 로 켜졌다 꺼진다 (50ms 마다 불리므로 따로 타이머가 필요 없음).
    /// </summary>
    void UpdateHeaderState()
    {
        bool recording = _recordingUi && _recorder.Status == ScreenRecorderLib.RecorderStatus.Recording;
        bool lit = recording && (long)((DateTime.Now - _recStarted).TotalMilliseconds / 500) % 2 == 0;

        var (text, chipBg, chipFg) = !_recordingUi ? ("READY", _teal, _onAccent)
            : _recorder.Status == ScreenRecorderLib.RecorderStatus.Paused ? ("PAUSE", _yellow, _onAccent)
            : lit ? ("REC", _onAccent, _danger)   // 켜진 바탕 위에서는 칩을 뒤집어 보이게
            : ("REC", _danger, _onAccent);
        if (StateText.Text != text) StateText.Text = text;
        if (StateChip.Background != chipBg) StateChip.Background = chipBg;
        if (StateText.Foreground != chipFg) StateText.Foreground = chipFg;

        var headerBg = lit ? _danger : _bgPanel;
        if (Header.Background != headerBg)
        {
            Header.Background = headerBg;
            Wordmark.Foreground = lit ? _onAccent : _cream;
            HeaderCursor.Foreground = lit ? _onAccent : _yellow;
        }
    }

    static void SetState(TextBlock t, (string tag, Brush color) s)
    {
        if (t.Text != s.tag) t.Text = s.tag;
        if (t.Foreground != s.color) t.Foreground = s.color;
    }

    // ── 녹화 ──

    void OnRecClick(object sender, RoutedEventArgs e) => ToggleRecording();
    void OnPauseClick(object sender, RoutedEventArgs e) => TogglePause();

    void ToggleRecording()
    {
        if (_recorder.IsBusy)
        {
            if (_recorder.Status is ScreenRecorderLib.RecorderStatus.Recording or ScreenRecorderLib.RecorderStatus.Paused)
            {
                RecBtn.IsEnabled = false;   // 마무리하는 동안 다시 누르지 않게
                SetStatus("WAIT", "저장하는 중…");
                _recorder.Stop();
            }
            return;
        }
        StartRecording();
    }

    void StartRecording()
    {
        string folder = _settings.OutputFolder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { SetStatus("FAIL", $"저장 폴더를 만들 수 없습니다. {ex.Message}"); return; }

        long free = FreeBytes(folder);
        if (free >= 0 && free < MinFreeBytes)
        {
            SetStatus("FAIL", $"디스크 여유 공간이 {FormatSize(free)}뿐입니다. 2 GB 이상 비워 주세요.");
            return;
        }

        bool sysOn = _settings.SystemEnabled && _audio.HasSystem;
        bool micOn = _settings.MicEnabled && _audio.HasMic;
        _recPath = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}.mp4");

        SetRecordingUi(true);
        // 다듬기를 쓰면 마이크는 따로 받고, 녹화기에는 시스템 소리만 넣는다
        bool fx = StartMicFx(micOn);
        try
        {
            _recorder.Start(new RecordRequest(_region, _settings.Fps, _audio.SystemId, sysOn, _audio.MicId, fx ? false : micOn, _recPath));
        }
        catch (Exception ex)
        {
            if (fx) { var w = _micRec.Stop(); _micFxActive = false; try { if (w != null) File.Delete(w); } catch { } }
            SetRecordingUi(false);
            SetStatus("FAIL", $"녹화를 시작하지 못했습니다. {ex.Message}");
            return;
        }

        WriteRecordingLock();
        _recStarted = DateTime.Now;
        _pausedTotal = TimeSpan.Zero;
        _recTimer.Start();
        UpdateRecTimer();
        SetStatus("OK", $"녹화 중 · {Path.GetFileName(_recPath)}");
    }

    void TogglePause()
    {
        if (!_recorder.IsBusy) return;
        if (_recorder.Status == ScreenRecorderLib.RecorderStatus.Recording)
        {
            _pauseStarted = DateTime.Now;
            _micRec.Paused = true;
            _recorder.Pause();
        }
        else if (_recorder.Status == ScreenRecorderLib.RecorderStatus.Paused)
        {
            _pausedTotal += DateTime.Now - _pauseStarted;
            _micRec.Paused = false;
            _recorder.Resume();
        }
    }

    void OnRecorderStatus(ScreenRecorderLib.RecorderStatus s)
    {
        bool paused = s == ScreenRecorderLib.RecorderStatus.Paused;
        PauseBtn.Content = paused ? ">" : "II";
        if (paused) SetStatus("WAIT", "멈춤");
        else if (s == ScreenRecorderLib.RecorderStatus.Recording && _recPath != null) SetStatus("OK", $"녹화 중 · {Path.GetFileName(_recPath)}");
        UpdateHeaderState();
    }

    async void OnRecordingDone(string? path, string? error)
    {
        _recTimer.Stop();
        SetRecordingUi(false);
        RecBtn.IsEnabled = true;
        UpdateRecMeta();

        if (error == null && path != null && File.Exists(path))
        {
            _lastFile = path;
            // 마이크를 따로 받았으면 다듬어 합친다 (그동안 표시 파일은 남겨 둬서, 이때 꺼져도 다음에 마저 합침)
            if (await FinishMicFxAsync(path)) { ReleaseMemorySoon(); return; }
            SetStatus("OK", $"저장했습니다: {Path.GetFileName(path)} ({FormatSize(new FileInfo(path).Length)})");
        }
        else
        {
            if (_micFxActive) { _micRec.Stop(); _micFxActive = false; }
            SetStatus("FAIL", $"녹화에 실패했습니다. {error}");
        }

        DeleteRecordingLock();
        if (_closeAfterStop) { Close(); return; }
        ReleaseMemorySoon();
    }

    /// <summary>녹화 중에는 영역·장치·폴더를 잠그고, 테두리를 빨갛게, 이 프로그램 창은 녹화에서 뺀다.</summary>
    void SetRecordingUi(bool on)
    {
        _recordingUi = on;
        foreach (var c in new Control[] { Res720Btn, HdBtn, QhdBtn, Fps30, Fps60, ResetPosBtn, FolderBtn, SysCombo, MicCombo })
            c.IsEnabled = !on;
        Header.Cursor = on ? Cursors.Arrow : Cursors.SizeAll;
        RecBtn.Content = on ? "STOP" : "REC";
        PauseBtn.IsEnabled = on;
        PauseBtn.Content = "II";
        _overlay.SetRegion(_region, _settings.Dim / 100.0, on);
        bool exclude = on && !ShowInCaptureForTesting;
        ScreenRecorderLib.Recorder.SetExcludeFromCapture(_hwnd, exclude);
        ScreenRecorderLib.Recorder.SetExcludeFromCapture(_overlay.Handle, exclude);
        UpdateHeaderState();
    }

    void ApplyLiveAudio()
    {
        if (!_recorder.IsBusy) return;
        bool mic = _settings.MicEnabled && _audio.HasMic;
        if (_micFxActive) _micRec.Muted = !mic;   // 따로 받는 마이크는 끄면 무음으로 (시간은 계속 맞춤)
        _recorder.SetAudio(_settings.SystemEnabled && _audio.HasSystem, !_micFxActive && mic);
    }

    void UpdateRecTimer()
    {
        var now = DateTime.Now;
        var elapsed = now - _recStarted - _pausedTotal;
        if (_recorder.Status == ScreenRecorderLib.RecorderStatus.Paused) elapsed -= now - _pauseStarted;
        TimerText.Text = elapsed.ToString(@"hh\:mm\:ss");
        OnRecTick(elapsed.TotalSeconds);

        long size = _recPath != null && File.Exists(_recPath) ? new FileInfo(_recPath).Length : 0;
        RecMeta.Text = $"{FormatSize(size)} · 여유 {FormatSize(CachedFreeBytes())}";
    }

    void UpdateRecMeta()
    {
        double mbps = RegionRecorder.BitrateFor(_region.Width, _settings.Fps) / 1_000_000.0;
        long free = FreeBytes(_settings.OutputFolder);
        RecMeta.Text = $"H.264 GPU · {mbps:0.#}Mbps · 여유 {(free < 0 ? "?" : FormatSize(free))}";
    }

    void OnFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "녹화 파일을 저장할 폴더", InitialDirectory = _settings.OutputFolder };
        if (dlg.ShowDialog(this) != true) return;
        _settings.OutputFolder = dlg.FolderName;
        _settings.Save();
        FolderText.Text = dlg.FolderName;
        FolderText.ToolTip = dlg.FolderName;
        UpdateRecMeta();
    }

    void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastFile != null && File.Exists(_lastFile))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
            else
            {
                Directory.CreateDirectory(_settings.OutputFolder);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{_settings.OutputFolder}\"");
            }
        }
        catch (Exception ex) { SetStatus("FAIL", $"폴더를 열 수 없습니다. {ex.Message}"); }
    }

    /// <summary>녹화 중에 끄면 파일을 마무리한 뒤 닫는다 (그냥 닫으면 MP4 가 깨짐).</summary>
    void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_postProcessing)
        {
            e.Cancel = true;
            _closeAfterStop = true;
            SetStatus("WAIT", "마이크 소리를 다 합친 뒤 끕니다…");
            return;
        }
        if (!_recorder.IsBusy) return;
        e.Cancel = true;
        if (_closeAfterStop) return;
        _closeAfterStop = true;
        SetStatus("WAIT", "저장을 마치고 끕니다…");
        _recorder.Stop();
    }

    /// <summary>녹화 중 남은 공간은 2초마다만 다시 잰다.</summary>
    long CachedFreeBytes()
    {
        if (DateTime.Now - _freeCheckedAt > TimeSpan.FromSeconds(2))
        {
            _freeCache = FreeBytes(_settings.OutputFolder);
            _freeCheckedAt = DateTime.Now;
        }
        return _freeCache;
    }

    /// <summary>녹화기가 정리된 뒤(약 2초) 한 번만 메모리를 모아 돌려준다. 평소에는 GC 를 직접 부르지 않는다.</summary>
    void ReleaseMemorySoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        };
        t.Start();
    }

    static long FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

    // ── 공통 ──

    /// <summary>마지막 동작 결과 한 줄 (녹화·창 맞추기 공통).</summary>
    void SetStatus(string tag, string message)
    {
        StatusText.Visibility = Visibility.Visible;
        StatusText.ToolTip = message;
        StatusTag.Text = $"[ {tag} ] ";
        StatusTag.Foreground = tag switch { "FAIL" => _danger, "WAIT" => _yellow, _ => _teal };
        StatusMsg.Text = message;
    }

    static string Shorten(string s) => s.Length > 30 ? s[..30] + "…" : s;

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
