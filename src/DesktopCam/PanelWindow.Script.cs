using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopCam;

/// <summary>패널 중 마이크 모니터링 · 대본 · 음성으로 넘기기.</summary>
public partial class PanelWindow
{
    const int HotkeyScript = 5, HotkeyNext = 6, HotkeyPrev = 7;

    internal static readonly string DefaultScriptPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DesktopCAM", "대본.md");
    const double ScaleMin = 0.6, ScaleMax = 2.0, ScaleStep = 0.1;

    readonly MicMonitor _monitor = new();
    ScriptWindow? _scriptWin;
    SpeechFollower? _follower;
    List<ScriptLine> _lines = new();
    string[] _normLines = Array.Empty<string>();
    FileSystemWatcher? _scriptWatcher;
    DispatcherTimer? _scriptReload;
    string _voiceState = "";        // "" · LOAD · LISTEN · NOMODEL · FAIL:메시지
    CancellationTokenSource? _modelDl;   // 음성 모델 받는 중
    double _modelPct;
    string? _modelError;            // 받기 실패 이유 (다음에 다시 물어봄)
    bool _modelAsked;               // 이번 실행에서 받을지 이미 물어봤는지
    string? _monProblem;            // 모니터링이 켜져 있는데 못 듣는 이유
    ScriptEditorWindow? _editor;

    /// <summary>지금 줄이 바뀌었을 때 (편집 창이 목록 표시를 옮김).</summary>
    internal event Action? ScriptPositionChanged;
    /// <summary>들린 말과 지금 줄 일치율 (편집 창의 '들린 말' 칸).</summary>
    internal event Action<string, double>? HeardUpdate;
    internal int ScriptIndex => _settings.ScriptLine;
    bool _monSameDevice, _fillingMon;

    void InitScriptAndMonitor()
    {
        _audio.MicData += OnMicData;
        _settings.ScriptPos ??= _settings.ScriptAtBottom ? ScriptWindow.PosBottom : ScriptWindow.PosTop;   // 예전 설정 옮기기
        _settings.ScriptScale = Math.Clamp(_settings.ScriptScale, ScaleMin, ScaleMax);
        LoadScript(_settings.ScriptPath, keepLine: true);
    }

    /// <summary>캡처 스레드. 모니터링과 음성인식에 같은 마이크 소리를 나눠 준다.</summary>
    void OnMicData(byte[] data, int count, WaveFormat format)
    {
        _monitor.Feed(data, count, format);
        _follower?.Feed(data, count, format);
        _micRec.Feed(data, count, format);
        _sampleRec.Feed(data, count, format);
    }

    void DisposeScriptAndMonitor()
    {
        _audio.MicData -= OnMicData;
        _monitor.Dispose();
        _follower?.Dispose();
        _follower = null;
        _scriptWatcher?.Dispose();
        _editor?.Close();
        _scriptWin?.Close();
    }

    // ── 마이크 모니터링 ──

    /// <summary>모니터 장치 목록을 다시 읽는다. 저장된 장치가 없으면 헤드셋을 먼저 고른다.</summary>
    void ReloadMonitorDevices(List<AudioDevice> render)
    {
        _fillingMon = true;
        var list = render.Where(d => d.Id != null).ToList();
        MonCombo.ItemsSource = list;
        MonCombo.SelectedItem = list.FirstOrDefault(d => d.Id == _settings.MonitorDeviceId)
            ?? (_settings.MonitorDeviceId == null ? list.FirstOrDefault(d => d.IsHeadset) : null);
        if (_settings.MonitorDeviceId == null && MonCombo.SelectedItem is AudioDevice auto)
        {
            _settings.MonitorDeviceId = auto.Id;
            _settings.MonitorDeviceName = auto.Name;
        }
        _fillingMon = false;
        ApplyMonitor(restart: true);
    }

    /// <summary>설정대로 모니터링을 켜거나 끈다. 녹화하는 장치와 같으면 켜지 않는다.</summary>
    void ApplyMonitor(bool restart = false)
    {
        _monProblem = null;
        _monSameDevice = false;
        if (restart) _monitor.Stop();
        if (!_settings.MonitorEnabled) { _monitor.Stop(); UpdateMonitorUi(); return; }

        var dev = _audio.GetActiveRender(_settings.MonitorDeviceId);
        if (dev == null)
        {
            _monitor.Stop();
            _monProblem = _settings.MonitorDeviceId == null ? "들을 장치를 고르세요."
                : $"\"{_settings.MonitorDeviceName}\" 연결을 기다리는 중입니다. 연결되면 자동으로 켜집니다.";
        }
        else if (_settings.SystemEnabled && dev.ID == _audio.SystemId)
        {
            _monitor.Stop();
            _monSameDevice = true;
            _monProblem = "녹화하는 시스템 소리 장치와 같아서 켜지 않았습니다.";
        }
        else if (!(_monitor.IsRunning && _monitor.DeviceId == dev.ID))
        {
            var fmt = _audio.MicFormat;
            if (fmt == null) _monProblem = "마이크를 열 수 없습니다.";
            else
            {
                try { _monitor.Start(dev, fmt); }
                catch (Exception ex) { _monitor.Stop(); _monProblem = $"장치를 열 수 없습니다. {ex.Message}"; }
            }
        }
        UpdateMonitorUi();
    }

    /// <summary>모니터 버튼 하나가 상태를 보여준다: ON(듣는 중) · WAIT(장치 기다림) · SAME(녹화 장치와 같음) · OFF.</summary>
    void UpdateMonitorUi()
    {
        var (text, brush) = !_settings.MonitorEnabled ? ("OFF", _danger)
            : _monSameDevice ? ("SAME", _danger)
            : _monProblem != null ? ("WAIT", _yellow)
            : ("ON", _teal);
        MonToggle.Content = text;
        MonToggle.Foreground = brush;
        MonToggle.ToolTip = (_monProblem ?? "마이크 소리 듣기 켜기 / 끄기");
    }

    void OnMonToggle(object sender, RoutedEventArgs e)
    {
        _settings.MonitorEnabled = !_settings.MonitorEnabled;
        if (_settings.MonitorEnabled && _settings.MonitorDeviceId == null && MonCombo.SelectedItem is AudioDevice d)
        {
            _settings.MonitorDeviceId = d.Id;
            _settings.MonitorDeviceName = d.Name;
        }
        ApplyMonitor();
        _settings.Save();
    }

    void OnMonDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingMon || MonCombo.SelectedItem is not AudioDevice d) return;
        _settings.MonitorDeviceId = d.Id;
        _settings.MonitorDeviceName = d.Name;
        ApplyMonitor(restart: true);
        _settings.Save();
    }

    // ── 대본 ──

    void LoadScript(string? path, bool keepLine)
    {
        _scriptWatcher?.Dispose();
        _scriptWatcher = null;
        _lines = new List<ScriptLine>();

        if (path != null && File.Exists(path))
        {
            try { _lines = ScriptDoc.Load(path); }
            catch (Exception ex) { SetStatus("FAIL", $"대본을 읽지 못했습니다. {ex.Message}"); }

            // 메모장에서 저장하면 바로 다시 읽는다 (저장이 여러 번 알려지므로 0.3초 모아서)
            _scriptWatcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _scriptWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(ScheduleScriptReload);
            _scriptWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleScriptReload);
        }

        _normLines = _lines.Select(l => ScriptMatcher.Normalize(l.Text)).ToArray();
        _settings.ScriptLine = keepLine ? Math.Clamp(_settings.ScriptLine, 0, Math.Max(0, _lines.Count - 1)) : 0;
        RefreshScript();
        ApplyVoice();
    }

    void ScheduleScriptReload()
    {
        _scriptReload ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _scriptReload.Stop();
        _scriptReload.Tick -= OnScriptReloadTick;
        _scriptReload.Tick += OnScriptReloadTick;
        _scriptReload.Start();
    }

    void OnScriptReloadTick(object? sender, EventArgs e)
    {
        _scriptReload!.Stop();
        LoadScript(_settings.ScriptPath, keepLine: true);
        SetStatus("OK", $"대본을 다시 읽었습니다 ({_lines.Count}줄).");
    }

    void RefreshScript()
    {
        int i = _settings.ScriptLine;
        ScriptCounter.Text = _lines.Count == 0 ? "0 / 0" : $"{i + 1} / {_lines.Count}";
        ScriptCounter.ToolTip = _settings.ScriptPath;
        ScriptShowBtn.Content = _settings.ScriptVisible ? "보이기 ON" : "보이기 OFF";
        ScriptShowBtn.Foreground = _settings.ScriptVisible ? _teal : _danger;
        _scriptWin?.Render(_lines, i);
        _follower?.ResetUtterance();
        ScriptPositionChanged?.Invoke();
    }

    void SetScriptLine(int i)
    {
        if (_lines.Count == 0) return;
        i = Math.Clamp(i, 0, _lines.Count - 1);
        if (i == _settings.ScriptLine) return;
        _settings.ScriptLine = i;
        RefreshScript();
        _settings.Save();
    }

    void OnScriptPrev(object sender, RoutedEventArgs e) => SetScriptLine(_settings.ScriptLine - 1);
    void OnScriptNext(object sender, RoutedEventArgs e) => SetScriptLine(_settings.ScriptLine + 1);

    // ── 편집 창이 쓰는 것들 ──

    /// <summary>편집 창에서 줄을 누르면 그 줄로.</summary>
    internal void JumpToLine(int i) => SetScriptLine(i);

    /// <summary>파일을 대본으로 쓴다 (불러오기·저장).</summary>
    internal void OpenScriptFile(string? path, bool keepLine = false)
    {
        _settings.ScriptPath = path;
        LoadScript(path, keepLine);
        _settings.Save();
    }

    /// <summary>편집 중인 내용을 저장 전에 바로 화면에 반영한다.</summary>
    internal void SetScriptLinesLive(List<ScriptLine> lines)
    {
        _lines = lines;
        _normLines = _lines.Select(l => ScriptMatcher.Normalize(l.Text)).ToArray();
        _settings.ScriptLine = Math.Clamp(_settings.ScriptLine, 0, Math.Max(0, _lines.Count - 1));
        RefreshScript();
        ApplyVoice();
    }

    /// <summary>위치 · 크기 · 보일 것 설정을 대본 창에 적용한다.</summary>
    internal void ApplyScriptDisplay()
    {
        _scriptWin?.SetOptions(_settings.ScriptShowPrev, _settings.ScriptShowSection, _settings.ScriptShowCue, _settings.ScriptNextCount);
        RefreshScript();
        PlaceScript();
        _settings.Save();
    }

    internal void ToggleVoice()
    {
        // 켜져 있는데 모델이 없으면: 끄지 않고 받을지 다시 묻는다
        if (_settings.ScriptVoice && _voiceState == "NOMODEL" && _modelDl == null) { AskModelDownload(); return; }
        _settings.ScriptVoice = !_settings.ScriptVoice;
        ApplyVoice();
        _settings.Save();
    }

    /// <summary>톱니 버튼: 대본 편집 창을 연다 (이미 열려 있으면 앞으로).</summary>
    void OnScriptEditor(object sender, RoutedEventArgs e)
    {
        if (_editor != null) { _editor.Activate(); return; }
        _editor = new ScriptEditorWindow(this, _settings) { Owner = this };   // 패널의 자식 창: 늘 패널 앞에
        _editor.Closed += (_, _) => _editor = null;
        _editor.Show();
    }

    void OnScriptShow(object sender, RoutedEventArgs e) => ToggleScript();

    void ToggleScript()
    {
        _settings.ScriptVisible = !_settings.ScriptVisible;
        ApplyScriptVisibility();
        _settings.Save();
    }

    void ApplyScriptVisibility()
    {
        if (_settings.ScriptVisible)
        {
            if (_scriptWin == null)
            {
                _scriptWin = new ScriptWindow { Owner = _overlay };
                _scriptWin.SetOptions(_settings.ScriptShowPrev, _settings.ScriptShowSection, _settings.ScriptShowCue, _settings.ScriptNextCount);
            }
            _scriptWin.Show();
            PlaceScript();
        }
        else _scriptWin?.Hide();
        RefreshScript();
        ApplyVoice();
    }

    void PlaceScript()
    {
        if (_scriptWin is { IsVisible: true }) _scriptWin.Place(_region, _settings.ScriptPos ?? ScriptWindow.PosTop, _settings.ScriptScale);
    }

    // ── 음성으로 넘기기 ──

    void OnVoiceToggle(object sender, RoutedEventArgs e) => ToggleVoice();

    /// <summary>대본이 보이고 음성 넘기기가 켜져 있을 때만 모델을 올린다. 끄면 바로 내려 메모리를 돌려준다.</summary>
    void ApplyVoice()
    {
        bool want = _settings.ScriptVisible && _settings.ScriptVoice && _lines.Count > 0;
        if (want && _follower == null)
        {
            var model = SpeechFollower.FindModel();
            if (model == null)
            {
                _voiceState = "NOMODEL";
                if (_modelDl == null && !_modelAsked) { _modelAsked = true; Dispatcher.BeginInvoke(AskModelDownload, DispatcherPriority.ApplicationIdle); }
            }
            else
            {
                var f = new SpeechFollower(model);
                f.StateChanged += s => Dispatcher.BeginInvoke(() => { if (_follower == f) { _voiceState = s; UpdateVoiceUi(); } });
                f.Heard += (text, final) => Dispatcher.BeginInvoke(() => { if (_follower == f) OnHeard(text, final); });
                _follower = f;
            }
        }
        else if (!want && _follower != null)
        {
            _follower.Dispose();
            _follower = null;
            _voiceState = "";
            ReleaseMemorySoon();
        }
        else if (!want) _voiceState = "";
        UpdateVoiceUi();
    }

    /// <summary>
    /// 들린 말과 지금 줄을 비교해 넘길지 정한다. 넘김 기준(편집 창에서 고름):
    ///   빠르게   말하는 중 70% 이상, 또는 말이 끊겼을 때 40% 이상
    ///   보통     말하는 중 85% 이상, 또는 말이 끊겼을 때 50% 이상
    ///   정확하게 말이 끊겼을 때 80% 이상만
    /// 아주 짧은 줄(네 글자 안팎)은 잘못 넘어가지 않게 말이 끊길 때만, 70% 이상에서 넘긴다.
    /// </summary>
    void OnHeard(string heard, bool final)
    {
        if (_lines.Count == 0 || !_settings.ScriptVisible) return;
        int i = _settings.ScriptLine;
        string line = _normLines[i];
        double score = ScriptMatcher.Score(line, heard);
        _scriptWin?.SetProgress(score);
        HeardUpdate?.Invoke(heard, score);

        var (live, end) = _settings.ScriptSensitivity switch
        {
            "Fast" => (0.70, 0.40),
            "Strict" => (2.0, 0.80),   // 말하는 중에는 넘기지 않음
            _ => (0.85, 0.50),
        };
        bool shortLine = line.Length < 12;   // 자모 12개 ≈ 네 글자
        bool done = shortLine ? final && score >= Math.Max(0.7, end) : score >= live || (final && score >= end);
        if (!done) return;
        if (i < _lines.Count - 1) SetScriptLine(i + 1);
        else _scriptWin?.SetProgress(1);
    }

    /// <summary>모델이 없을 때: 받을지 묻고, 받으면 바로 자동스크롤을 시작한다.</summary>
    void AskModelDownload()
    {
        if (_modelDl != null || SpeechFollower.FindModel() != null) { ApplyVoice(); return; }
        var ok = MessageBox.Show(this,
            $"자동스크롤 [AI] 에 쓰는 한국어 음성 모델({ModelDownloader.SizeText})이 아직 없습니다.\n\n" +
            "지금 받을까요? 한 번만 받으면 되고, 음성인식은 인터넷 없이 이 PC 안에서만 합니다.\n(받는 곳: alphacephei.com)",
            "Desktop CAM · 음성 모델 받기", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok == MessageBoxResult.Yes) _ = DownloadModelAsync();
    }

    async Task DownloadModelAsync()
    {
        var cts = _modelDl = new CancellationTokenSource();
        _modelError = null;
        _modelPct = 0;
        UpdateVoiceUi();
        try
        {
            await ModelDownloader.DownloadAsync(p => Dispatcher.BeginInvoke(() => { _modelPct = p; UpdateVoiceUi(); }), cts.Token);
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            _modelError = ex is System.Net.Http.HttpRequestException ? "인터넷 연결을 확인하세요." : ex.Message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _modelDl = null;
            cts.Dispose();
        }
        _voiceState = "";
        ApplyVoice();   // 받았으면 바로 듣기 시작, 아니면 다시 NOMODEL
    }

    void UpdateVoiceUi()
    {
        bool active = _settings.ScriptVoice && _settings.ScriptVisible && _lines.Count > 0;
        var (tag, brush, tip) = !active ? ("", _teal, "")
            : _modelDl != null ? ("[ LOAD ]", _yellow, $"음성 모델을 받는 중입니다 ({_modelPct:0}%).")
            : _voiceState == "NOMODEL" ? ("[ NO MODEL ]", _danger, $"{SpeechFollower.ModelName} 모델이 없습니다.")
            : _voiceState == "LOAD" ? ("[ LOAD ]", _yellow, "음성 모델을 불러오는 중입니다.")
            : _voiceState == "LISTEN" ? ("[ LISTEN ]", _teal, "말하면 대본을 따라 넘어갑니다.")
            : _voiceState.StartsWith("FAIL:") ? ("[ FAIL ]", _danger, _voiceState[5..])
            : ("[ LOAD ]", _yellow, "");
        // 버튼 하나가 상태를 보여준다: 음성 넘김 ON(듣는 중 teal · 불러오는 중 yellow · 문제 danger) / OFF
        VoiceBtn.Content = _settings.ScriptVoice ? "자동스크롤 [AI] ON" : "자동스크롤 [AI] OFF";
        VoiceBtn.Foreground = !_settings.ScriptVoice ? _danger
            : tag == "[ LOAD ]" ? _yellow
            : tag is "[ FAIL ]" or "[ NO MODEL ]" ? _danger
            : _teal;
        VoiceBtn.ToolTip = "자동스크롤 [AI]: 마이크로 들리는 말을 PC 안에서 음성인식해, 지금 줄을 다 읽으면 자동으로 다음 줄로 넘깁니다. 끄면 < > 버튼이나 Ctrl+Alt+N/B 로만 넘어갑니다."
                           + (tip == "" ? "" : "\n" + tip);
    }

    /// <summary>MeterTick 의 알림 목록에 덧붙인다 (상태 표시만으로 이유를 알 수 없는 것만).</summary>
    void AppendScriptAlerts(List<Alert> a)
    {
        if (_settings.MonitorEnabled && _monSameDevice)
            a.Add(new("[ SAME ] ", "모니터 장치가 녹화하는 시스템 소리 장치와 같아 목소리가 두 번 녹음됩니다. 다른 장치(헤드셋)를 고르세요.", _danger));
        if (_modelDl != null)
            a.Add(new("[ DOWN ] ", $"음성 모델 받는 중 {_modelPct:0}% ({ModelDownloader.SizeText}). 다 받으면 자동스크롤이 바로 켜집니다.", _yellow));
        else if (_voiceState == "NOMODEL" && _settings.ScriptVoice && _settings.ScriptVisible)
            a.Add(new("[ FAIL ] ", (_modelError != null ? $"음성 모델 받기 실패: {_modelError} " : "음성 모델이 없습니다. ")
                                   + "자동스크롤 [AI] 버튼을 누르면 받을 수 있습니다.", _danger));
    }
}
