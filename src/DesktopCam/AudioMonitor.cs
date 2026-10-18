using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace DesktopCam;

public record AudioDevice(string? Id, string Name, bool IsHeadset = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// 시스템 소리(출력 장치)와 마이크(입력 장치)의 실시간 음량을 읽는다.
/// 마이크 캡처 스트림은 음량 측정용으로 늘 열어 두고, 받은 소리는 MicData 로 넘긴다 (모니터링·음성인식이 씀).
/// </summary>
public sealed class AudioMonitor : IDisposable, IMMNotificationClient
{
    readonly MMDeviceEnumerator _enumerator = new();
    MMDevice? _system, _mic;
    WasapiCapture? _micStream;

    /// <summary>장치가 꽂히거나 빠지거나 기본 장치가 바뀌었을 때 (오디오 스레드에서 호출됨).</summary>
    public event Action? DevicesChanged;

    /// <summary>마이크에서 소리가 들어올 때마다 (캡처 스레드에서 호출됨). 버퍼는 호출이 끝나면 재사용되므로 바로 복사해야 한다.</summary>
    public event Action<byte[], int, WaveFormat>? MicData;

    public AudioMonitor() => _enumerator.RegisterEndpointNotificationCallback(this);

    /// <summary>지금 쓰는 장치의 ID ("기본 장치"를 골랐으면 그 순간의 기본 장치). 녹화기에 그대로 넘긴다.</summary>
    public string? SystemId => _system?.ID;
    public string? MicId => _mic?.ID;
    public string SystemName => _system?.FriendlyName ?? "";
    public string MicName => _mic?.FriendlyName ?? "";
    public bool HasSystem => _system != null;
    public bool HasMic => _mic != null;
    /// <summary>마이크 캡처 형식 (모니터링 출력을 열 때 필요). 캡처를 못 열었으면 null.</summary>
    public WaveFormat? MicFormat => _micStream?.WaveFormat;

    public float SystemPeak => Peak(_system);
    public float MicPeak => Peak(_mic);

    /// <summary>Windows 소리 설정에서 마이크가 음소거되어 있는지.</summary>
    public bool MicMutedInWindows
    {
        get { try { return _mic?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
    }

    static float Peak(MMDevice? d)
    {
        try { return d?.AudioMeterInformation.MasterPeakValue ?? 0; }
        catch { return 0; }   // 장치가 막 빠진 경우
    }

    /// <summary>
    /// 출력·입력 장치 목록을 뒤에서(다른 스레드) 읽는다. 장치마다 이름·종류를 여는 데 시간이 걸려
    /// (HDMI·블루투스 장치가 있으면 1초 넘게) 화면이 멈추지 않게 한다. 돌려주는 것은 이름·ID 뿐인 기록이라 스레드를 넘겨도 안전.
    /// </summary>
    public static Task<(List<AudioDevice> render, List<AudioDevice> capture)> ListAllAsync() => Task.Run(() =>
    {
        using var en = new MMDeviceEnumerator();
        return (List(en, DataFlow.Render), List(en, DataFlow.Capture));
    });

    /// <summary>선택 목록: 맨 앞은 "기본 장치", 그 뒤로 연결된 장치들.</summary>
    static List<AudioDevice> List(MMDeviceEnumerator en, DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            var def = en.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            list.Add(new AudioDevice(null, $"기본 장치 · {def.FriendlyName}"));
        }
        catch { list.Add(new AudioDevice(null, "기본 장치 · 없음")); }

        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            bool headset = flow == DataFlow.Render && IsHeadset(d);
            list.Add(new AudioDevice(d.ID, headset ? $"{d.FriendlyName} · 헤드셋" : d.FriendlyName, headset));
        }
        return list;
    }

    /// <summary>Windows 가 헤드폰·헤드셋으로 알려 주는 출력 장치인지 (블루투스·USB 헤드셋 등).
    /// 오디오 인터페이스에 꽂은 헤드폰은 '라인 출력'으로 보이므로 여기서 알 수 없다.</summary>
    public static bool IsHeadset(MMDevice d)
    {
        try
        {
            var v = d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor]?.Value;
            return v is uint f && (f == 3 /* Headphones */ || f == 5 /* Headset */);
        }
        catch { return false; }
    }

    /// <summary>ID 로 연결된 출력 장치를 찾는다. 없거나 빠져 있으면 null.</summary>
    public MMDevice? GetActiveRender(string? id)
    {
        if (id == null) return null;
        try
        {
            var d = _enumerator.GetDevice(id);
            return d.State == DeviceState.Active && d.DataFlow == DataFlow.Render ? d : null;
        }
        catch { return null; }
    }

    /// <summary>시스템 소리 장치를 고른다. 찾지 못하면 기본 장치로 돌아가고 false.</summary>
    public bool SelectSystem(string? id)
    {
        _system = Find(DataFlow.Render, id, out bool found);
        return found;
    }

    /// <summary>마이크를 고른다. 찾지 못하면 기본 장치로 돌아가고 false.</summary>
    public bool SelectMic(string? id)
    {
        StopMicStream();
        _mic = Find(DataFlow.Capture, id, out bool found);
        if (_mic != null)
        {
            try
            {
                var stream = new WasapiCapture(_mic);
                stream.DataAvailable += (_, e) => MicData?.Invoke(e.Buffer, e.BytesRecorded, stream.WaveFormat);
                _micStream = stream;
                stream.StartRecording();
            }
            catch { StopMicStream(); }   // 다른 프로그램이 독점 중이면 음량만 0 으로 보임
        }
        return found;
    }

    MMDevice? Find(DataFlow flow, string? id, out bool found)
    {
        found = true;
        if (id != null)
        {
            try
            {
                var d = _enumerator.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
            catch { }
            found = false;
        }
        try { return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    void StopMicStream()
    {
        if (_micStream == null) return;
        try { _micStream.StopRecording(); } catch { }
        _micStream.Dispose();
        _micStream = null;
    }

    public void Dispose()
    {
        StopMicStream();
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }

    // ── 장치 변경 알림 ──
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Multimedia) DevicesChanged?.Invoke();
    }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
