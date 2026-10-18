using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DesktopCam;

/// <summary>
/// 패널 중 마이크 소리 다듬기: 녹화 동안 마이크를 따로 wav 로 받고, 끝나면 프리셋대로 다듬어 영상에 합친다.
/// 녹화 중 끊기면 다음에 켤 때 영상·wav 를 고쳐 같은 방식으로 합친다.
/// </summary>
public partial class PanelWindow
{
    /// <summary>녹화 표시 파일 내용: 끊겼을 때 무엇을 고치고 합칠지.</summary>
    record LockInfo(string Video, string? Wav, double Lead, MicFx? Fx, double Duration);

    readonly MicRecorder _micRec = new();
    readonly MicRecorder _sampleRec = new();
    AudioSettingsWindow? _audioWin;
    List<AudioDevice> _captureDevices = new();

    internal IReadOnlyList<AudioDevice> CaptureDevices => _captureDevices;

    /// <summary>톱니 버튼(소리 칸): 마이크 다듬기 설정 창.</summary>
    void OnAudioSettings(object sender, RoutedEventArgs e)
    {
        if (_audioWin != null) { _audioWin.Activate(); return; }
        _audioWin = new AudioSettingsWindow(this, _settings) { Owner = this };
        _audioWin.Closed += (_, _) => _audioWin = null;
        _audioWin.Show();
    }

    /// <summary>설정 창에서 다듬기 값이 바뀌면 저장하고 소리 칸 표시를 고친다.</summary>
    internal void OnMicFxChanged()
    {
        _settings.Save();
        UpdateFxLabel();
    }

    void UpdateFxLabel()
    {
        var fx = _settings.MicFx;
        FxLabel.Text = fx.Enabled ? "필터 적용 중" : "필터 끔 (원음)";
        FxLabel.Foreground = fx.Enabled ? _teal : (System.Windows.Media.Brush)FindResource("Muted");
        AudioGear.ToolTip = fx.Enabled
            ? $"마이크 다듬기: {fx.PresetName} — 녹화가 끝나면 자동으로 적용됩니다 (프리셋 · 잡음 제거 · EQ)"
            : "마이크 다듬기: 원음 — 다듬지 않고 그대로 녹음합니다 (프리셋 · 잡음 제거 · EQ)";
    }

    /// <summary>미리 듣기용: 지금 마이크를 seconds 초 동안 wav 로 받는다.</summary>
    internal async Task<string?> RecordMicSampleAsync(int seconds)
    {
        var fmt = _audio.MicFormat;
        if (fmt == null) return null;
        string path = Path.Combine(Path.GetTempPath(), $"desktopcam_raw_{Guid.NewGuid():N}.wav");
        _sampleRec.Start(path, fmt);
        await Task.Delay(seconds * 1000);
        return _sampleRec.Stop();
    }

    /// <summary>설정 창에서 고른 마이크로 바꾼다 (예: NVIDIA Broadcast).</summary>
    internal void UseMic(string id)
    {
        var item = MicCombo.Items.OfType<AudioDevice>().FirstOrDefault(d => d.Id == id);
        if (item != null) MicCombo.SelectedItem = item;   // OnMicDeviceChanged 가 저장·적용
    }
    bool _micFxActive, _postProcessing, _leadSaved;
    MicFx? _fxSnapshot;
    double _recElapsedSec;

    /// <summary>
    /// 녹화를 시작하기 전에: 다듬기를 쓸 수 있으면 마이크를 따로 받기 시작하고 true.
    /// 그때 녹화기(ScreenRecorderLib)에는 마이크를 넣지 않는다 (두 번 들어가지 않게).
    /// </summary>
    bool StartMicFx(bool micOn)
    {
        _micFxActive = false;
        _leadSaved = false;
        if (!micOn || !_settings.MicFx.Enabled) return false;
        if (Tools.Ffmpeg == null)
        {
            SetStatus("WAIT", "ffmpeg 가 없어 마이크 다듬기 없이(원음) 녹음합니다.");
            return false;
        }
        var fmt = _audio.MicFormat;
        if (fmt == null) return false;
        _fxSnapshot = _settings.MicFx.Clone();
        _micRec.Paused = false;
        _micRec.Muted = false;
        _micRec.Start(Path.ChangeExtension(_recPath!, ".mic.wav"), fmt);
        _micFxActive = true;
        return true;
    }

    /// <summary>마이크가 영상보다 몇 초 먼저 시작됐는지 (음수면 영상이 먼저).</summary>
    double MicLead()
    {
        long mic = _micRec.FirstSampleTicks, video = _recorder.FirstFrameTicks;
        if (mic == 0 || video == 0) return 0;
        return (double)(video - mic) / Stopwatch.Frequency;
    }

    void WriteRecordingLock()
    {
        var info = new LockInfo(_recPath!, _micFxActive ? _micRec.Path : null, MicLead(), _micFxActive ? _fxSnapshot : null, _recElapsedSec);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            File.WriteAllText(LockPath, JsonSerializer.Serialize(info));
        }
        catch { }
    }

    /// <summary>녹화 시간 갱신 때마다: 영상·마이크 첫 시각이 둘 다 잡히면 표시 파일에 맞춤 값을 한 번 적어 둔다.</summary>
    void OnRecTick(double elapsedSec)
    {
        _recElapsedSec = elapsedSec;
        if (_micFxActive && !_leadSaved && _micRec.FirstSampleTicks != 0 && _recorder.FirstFrameTicks != 0)
        {
            _leadSaved = true;
            WriteRecordingLock();
        }
    }

    /// <summary>녹화가 끝나면: 마이크 파일을 닫고 다듬어 합친다. 다듬기를 안 썼으면 false (그냥 저장 끝).</summary>
    async Task<bool> FinishMicFxAsync(string video)
    {
        if (!_micFxActive) return false;
        _micFxActive = false;
        string? wav = _micRec.Stop();
        if (wav == null || !File.Exists(wav)) return false;
        WriteLock(video, wav, MicLead(), _fxSnapshot!, _recElapsedSec);
        await PostProcessAsync(video, wav, MicLead(), _fxSnapshot!, _recElapsedSec);
        return true;
    }

    static void WriteLock(string video, string wav, double lead, MicFx fx, double dur)
    {
        try { File.WriteAllText(LockPath, JsonSerializer.Serialize(new LockInfo(video, wav, lead, fx, dur))); } catch { }
    }

    /// <summary>
    /// 다듬어 합치기. 성공하면 원래 영상 자리에 새 영상을 두고, 원본은 설정에 따라 남기거나 지운다.
    /// 실패하면 원본 영상(마이크 없음)과 마이크 wav 를 그대로 남긴다.
    /// </summary>
    async Task PostProcessAsync(string video, string wav, double lead, MicFx fx, double durationSec)
    {
        _postProcessing = true;
        RecBtn.IsEnabled = false;
        string dir = Path.GetDirectoryName(video)!, name = Path.GetFileNameWithoutExtension(video);
        string tmp = Path.Combine(dir, name + ".mixing.mp4");
        var progress = new Progress<double>(p => SetStatus("WAIT", $"마이크 소리 다듬는 중 {p:P0} ({fx.PresetName})"));
        try
        {
            SetStatus("WAIT", $"마이크 소리 다듬는 중 ({fx.PresetName})…");
            await AudioPost.MixAsync(video, wav, lead, fx, tmp, durationSec, progress);
            if (fx.KeepRaw)
            {
                File.Move(video, Path.Combine(dir, name + "_원본.mp4"), true);
                File.Move(wav, Path.Combine(dir, name + "_원본_마이크.wav"), true);
            }
            else
            {
                File.Delete(video);
                File.Delete(wav);
            }
            File.Move(tmp, video, true);
            _lastFile = video;
            SetStatus("OK", $"저장했습니다: {Path.GetFileName(video)} ({FormatSize(new FileInfo(video).Length)}, 마이크 {fx.PresetName})");
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            _lastFile = video;
            SetStatus("FAIL", $"마이크 소리를 합치지 못해 영상과 마이크 파일(.mic.wav)을 따로 남겼습니다. {ex.Message}");
        }
        finally
        {
            _postProcessing = false;
            RecBtn.IsEnabled = true;
            DeleteRecordingLock();
            if (_closeAfterStop) Close();
        }
    }

    /// <summary>시작할 때: 지난 녹화가 끊겼으면 영상(과 마이크 wav)을 고치고, 다듬기를 썼으면 마저 합친다.</summary>
    async void RepairInterruptedRecording()
    {
        if (!File.Exists(LockPath)) return;
        LockInfo? info = null;
        try
        {
            string text = File.ReadAllText(LockPath).Trim();
            info = text.StartsWith('{') ? JsonSerializer.Deserialize<LockInfo>(text) : new LockInfo(text, null, 0, null, 0);
        }
        catch { }
        if (info == null || !File.Exists(info.Video)) { DeleteRecordingLock(); return; }
        try
        {
            var seconds = Mp4Repair.Repair(info.Video) ?? info.Duration;
            if (info.Wav != null && info.Fx != null && File.Exists(info.Wav))
            {
                MicRecorder.RepairHeader(info.Wav);
                SetStatus("WAIT", "지난 녹화가 끊겨 고치는 중입니다…");
                await PostProcessAsync(info.Video, info.Wav, info.Lead, info.Fx, seconds);
                SetStatus("WAIT", $"지난 녹화가 중간에 끊겨 {TimeSpan.FromSeconds(seconds):mm\\:ss}까지 살렸습니다: {Path.GetFileName(info.Video)}");
                return;
            }
            DeleteRecordingLock();
            _lastFile = info.Video;
            SetStatus("WAIT", $"지난 녹화가 중간에 끊겨 {TimeSpan.FromSeconds(seconds):mm\\:ss}까지 살렸습니다: {Path.GetFileName(info.Video)}");
        }
        catch (Exception ex)
        {
            DeleteRecordingLock();
            SetStatus("FAIL", $"끊긴 녹화를 고치지 못했습니다. {ex.Message}");
        }
    }
}
