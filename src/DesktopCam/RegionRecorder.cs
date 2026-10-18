using ScreenRecorderLib;

namespace DesktopCam;

/// <summary>녹화 한 번에 필요한 설정.</summary>
public record RecordRequest(
    Native.RECT Region,        // 녹화 영역 (모니터 기준 실제 픽셀)
    int Fps,
    string? SystemDeviceId, bool SystemOn,
    string? MicDeviceId, bool MicOn,
    string OutputPath);

/// <summary>
/// ScreenRecorderLib 로 주 모니터의 일부 영역을 MP4(H.264 + AAC)로 녹화한다.
/// 이벤트는 녹화 스레드에서 올라오므로 UI 에서는 Dispatcher 로 넘겨야 한다.
/// </summary>
public sealed class RegionRecorder : IDisposable
{
    Recorder? _rec;

    public event Action<RecorderStatus>? StatusChanged;
    public event Action<string>? Completed;              // 저장된 파일 경로
    public event Action<string, string?>? Failed;        // 오류 메시지, 파일 경로

    public bool IsBusy => _rec != null;

    /// <summary>화면 캡처 방식. 측정용으로 환경 변수 DESKTOPCAM_REC_API=WGC 로 바꿔 볼 수 있다.</summary>
    static RecorderApi Api => Environment.GetEnvironmentVariable("DESKTOPCAM_REC_API") == "WGC"
        ? RecorderApi.WindowsGraphicsCapture : RecorderApi.DesktopDuplication;
    public RecorderStatus Status => _rec?.Status ?? RecorderStatus.Idle;

    /// <summary>유튜브 권장 비트레이트: 720p60 7.5Mbps, 1080p60 12Mbps, 1440p60 24Mbps (30fps 는 2/3).</summary>
    public static int BitrateFor(int width, int fps)
    {
        double baseMbps = width >= 2560 ? 24 : width >= 1920 ? 12 : 7.5;
        return (int)((fps >= 60 ? baseMbps : baseMbps * 2 / 3) * 1_000_000);
    }

    public void Start(RecordRequest r)
    {
        if (_rec != null) throw new InvalidOperationException("이미 녹화 중입니다.");

        var source = new DisplayRecordingSource(DisplayRecordingSource.MainMonitor)
        {
            SourceRect = new ScreenRect(r.Region.Left, r.Region.Top, r.Region.Width, r.Region.Height),
            IsCursorCaptureEnabled = true,
            IsBorderRequired = false,
            RecorderApi = Api,
        };

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(r.Region.Width, r.Region.Height),
                Stretch = StretchMode.Uniform,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.CBR, EncoderProfile = H264Profile.High },
                Bitrate = BitrateFor(r.Region.Width, r.Fps),
                Framerate = r.Fps,
                IsFixedFramerate = true,          // 편집 프로그램에서 싱크가 밀리지 않게 고정 프레임
                IsHardwareEncodingEnabled = true, // NVENC 등 GPU 인코더 사용
                IsMp4FastStartEnabled = false,
                // 조각 MP4: 몇 초마다 재생 목차를 같이 써서, 녹화 중 PC 가 꺼지거나 강제 종료돼도 직전까지는 재생된다.
                // (일반 MP4 는 목차를 끝날 때 한 번에 써서, 중간에 끊기면 파일 전체가 재생되지 않음)
                IsFragmentedMp4Enabled = true,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = true,
                IsOutputDeviceEnabled = r.SystemOn,
                IsInputDeviceEnabled = r.MicOn,
                AudioOutputDevice = r.SystemDeviceId,
                AudioInputDevice = r.MicDeviceId,
                OutputVolume = 1f,
                InputVolume = 1f,
                Bitrate = AudioBitrate.bitrate_192kbps,
                Channels = AudioChannels.Stereo,
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
        };

        var rec = Recorder.CreateRecorder(options);
        FirstFrameTicks = 0;
        rec.OnFrameRecorded += OnFirstFrame;   // 첫 프레임 시각만 잰다 (마이크 따로 녹음과 맞추기)
        rec.OnStatusChanged += (_, e) => StatusChanged?.Invoke(e.Status);
        rec.OnRecordingComplete += (_, e) => { Release(); Completed?.Invoke(e.FilePath); };
        rec.OnRecordingFailed += (_, e) => { Release(); Failed?.Invoke(e.Error, e.FilePath); };
        _rec = rec;
        rec.Record(r.OutputPath);
    }

    /// <summary>첫 영상 프레임이 들어온 시각 (Stopwatch). 0 이면 아직.</summary>
    public long FirstFrameTicks { get; private set; }

    void OnFirstFrame(object? sender, FrameRecordedEventArgs e)
    {
        if (FirstFrameTicks == 0) FirstFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (sender is Recorder r) r.OnFrameRecorded -= OnFirstFrame;
    }

    /// <summary>녹화 중에 시스템 소리·마이크를 켜고 끈다.</summary>
    public void SetAudio(bool systemOn, bool micOn)
    {
        _rec?.GetDynamicOptionsBuilder()
            .SetDynamicAudioOptions(new DynamicAudioOptions { IsOutputDeviceEnabled = systemOn, IsInputDeviceEnabled = micOn })
            .Apply();
    }

    public void Pause() => _rec?.Pause();
    public void Resume() => _rec?.Resume();
    public void Stop() => _rec?.Stop();

    /// <summary>끝난 녹화기를 놓는다. 녹화 스레드의 콜백 안에서 불리므로 정리는 다른 스레드에서.</summary>
    void Release()
    {
        var r = _rec;
        _rec = null;
        if (r != null) ThreadPool.QueueUserWorkItem(_ => r.Dispose());
    }

    public void Dispose() => Release();
}
