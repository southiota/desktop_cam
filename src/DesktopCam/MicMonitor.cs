using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopCam;

/// <summary>
/// 마이크 소리를 다른 출력 장치(헤드셋)로 그대로 들려준다.
/// 녹화하는 시스템 소리 장치로 내보내면 목소리가 두 번 녹음되므로, 어느 장치로 낼지는 PanelWindow 가 막는다.
/// 출력 장치는 UI 스레드에서 열고, 캡처 스레드는 버퍼에 소리만 넣는다.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    static readonly TimeSpan MaxLag = TimeSpan.FromMilliseconds(120);   // 이보다 밀리면 버려서 지연이 쌓이지 않게

    WasapiOut? _out;
    volatile BufferedWaveProvider? _buf;
    WaveFormat? _inFormat;

    public string? DeviceId { get; private set; }
    public bool IsRunning => _out != null;

    /// <summary>모니터링을 시작한다 (UI 스레드). micFormat 은 마이크 캡처 형식.</summary>
    public void Start(MMDevice output, WaveFormat micFormat)
    {
        Stop();
        var buf = new BufferedWaveProvider(micFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,   // 소리가 모자라면 무음으로 채워 끊기지 않게
        };

        ISampleProvider sp = buf.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sp = mux;
        }
        int rate = output.AudioClient.MixFormat.SampleRate;
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);

        var o = new WasapiOut(output, AudioClientShareMode.Shared, true, 40);
        o.Init(sp);
        o.Play();
        _out = o;
        _inFormat = micFormat;
        DeviceId = output.ID;
        _buf = buf;
    }

    /// <summary>캡처 스레드에서 불린다. 형식이 다르면(마이크가 바뀌는 중) 버린다.</summary>
    public void Feed(byte[] data, int count, WaveFormat format)
    {
        var buf = _buf;
        if (buf == null || !format.Equals(_inFormat)) return;
        if (buf.BufferedDuration > MaxLag) buf.ClearBuffer();
        buf.AddSamples(data, 0, count);
    }

    public void Stop()
    {
        _buf = null;
        _inFormat = null;
        DeviceId = null;
        var o = _out;
        _out = null;
        if (o == null) return;
        try { o.Stop(); } catch { }
        o.Dispose();
    }

    public void Dispose() => Stop();
}
