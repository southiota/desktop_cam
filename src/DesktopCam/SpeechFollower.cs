using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace DesktopCam;

/// <summary>
/// Vosk 한국어 모델로 마이크 소리를 실시간으로 받아 적는다. 인터넷을 쓰지 않는다.
/// 모델은 켤 때 따로 스레드에서 불러오고(약 1.5초, 메모리 약 450MB), 끄면 바로 내려놓는다.
/// </summary>
public sealed class SpeechFollower : IDisposable
{
    public const string ModelName = "vosk-model-small-ko-0.22";
    const int Rate = 16000;

    readonly BlockingCollection<short[]> _queue = new(boundedCapacity: 100);
    readonly Thread _worker;
    volatile bool _resetRequested, _stopping;
    readonly string _modelPath;

    // 입력 → 16kHz 모노 변환 상태 (조각 사이에서 이어짐)
    double _srcPos;
    float _lastSample;

    // 0.1초(1600 샘플)씩 모아서 인식기에 넣는다. 10ms 조각마다 넣으면 결과가 1초에 100번 올라와 화면이 밀린다.
    const int Batch = Rate / 10;
    readonly List<short> _pending = new(Batch * 2);

    /// <summary>들린 말 (지금 줄을 읽기 시작한 뒤로 쌓인 것), 문장이 끝났는지. 인식 스레드에서 호출됨.</summary>
    public event Action<string, bool>? Heard;
    /// <summary>"LOAD" / "LISTEN" / "FAIL:메시지". 인식 스레드에서 호출됨.</summary>
    public event Action<string>? StateChanged;

    public SpeechFollower(string modelPath)
    {
        _modelPath = modelPath;
        _worker = new Thread(Run) { IsBackground = true, Name = "SpeechFollower", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>실행 폴더의 models\ 또는 %LOCALAPPDATA%\DesktopCAM\models\ 에서 모델을 찾는다.</summary>
    public static string? FindModel()
    {
        foreach (var root in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCAM", "models"),
        })
        {
            var p = Path.Combine(root, ModelName);
            if (File.Exists(Path.Combine(p, "am", "final.mdl"))) return p;
        }
        return null;
    }

    /// <summary>다음 줄로 넘어갈 때: 쌓인 말을 비우고 새로 듣는다.</summary>
    public void ResetUtterance() => _resetRequested = true;

    /// <summary>마이크 소리를 넣는다 (캡처 스레드). 16kHz 모노 16bit 로 바꿔 큐에 넣고, 밀리면 버린다.</summary>
    public void Feed(byte[] data, int count, WaveFormat format)
    {
        if (_stopping) return;
        _pending.AddRange(ToMono16k(data, count, format));
        if (_pending.Count < Batch) return;
        var batch = _pending.ToArray();
        _pending.Clear();

        // 조용할 때는 인식기에 넣지 않는다 (CPU 절약). 말이 시작되면 바로 앞 0.1초도 같이 넣어 첫 음절을 놓치지 않고,
        // 말이 끝난 뒤 1초는 계속 넣어 인식기가 '문장이 끝났다'를 알아채게 한다.
        bool voice = Peak(batch) >= VoicePeak;
        if (voice)
        {
            if (_tail == 0 && _lastSilent != null) _queue.TryAdd(_lastSilent);
            _tail = TailBatches;
        }
        else if (_tail > 0) _tail--;
        else { _lastSilent = batch; return; }
        _lastSilent = null;
        _queue.TryAdd(batch);
    }

    const short VoicePeak = 200;          // 약 -44dBFS. 마이크 잡음보다 크고 말소리보다 훨씬 작다
    const int TailBatches = 10;           // 말이 끝난 뒤 1초
    int _tail;
    short[]? _lastSilent;

    static short Peak(short[] s)
    {
        int m = 0;
        foreach (var v in s) { int a = v < 0 ? -v : v; if (a > m) m = a; }
        return (short)Math.Min(m, short.MaxValue);
    }

    void Run()
    {
        Model? model = null;
        VoskRecognizer? rec = null;
        try
        {
            StateChanged?.Invoke("LOAD");
            Vosk.Vosk.SetLogLevel(-1);
            model = new Model(_modelPath);
            rec = new VoskRecognizer(model, Rate);
            StateChanged?.Invoke("LISTEN");

            string done = "";   // 지금 줄에서 끝난 문장들
            string lastPartial = "";
            foreach (var chunk in _queue.GetConsumingEnumerable())
            {
                if (_stopping) break;
                if (_resetRequested)
                {
                    _resetRequested = false;
                    rec.Reset();
                    done = "";
                    lastPartial = "";
                }
                if (rec.AcceptWaveform(chunk, chunk.Length))
                {
                    string text = Read(rec.Result(), "text");
                    lastPartial = "";
                    if (text.Length > 0) done = (done + " " + text).Trim();
                    Heard?.Invoke(done, true);
                }
                else
                {
                    string partial = Read(rec.PartialResult(), "partial");
                    if (partial.Length > 0 && partial != lastPartial)
                    {
                        lastPartial = partial;
                        Heard?.Invoke((done + " " + partial).Trim(), false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StateChanged?.Invoke("FAIL:" + ex.Message);
        }
        finally
        {
            rec?.Dispose();
            model?.Dispose();
        }
    }

    static string Read(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>캡처 형식(보통 32bit float, 44.1/48kHz, 1~2채널)을 16kHz 모노 16bit 로. 선형 보간으로 줄인다.</summary>
    short[] ToMono16k(byte[] data, int count, WaveFormat f)
    {
        int ch = f.Channels, bytes = f.BitsPerSample / 8;
        if (ch <= 0 || bytes <= 0) return Array.Empty<short>();
        int frames = count / (ch * bytes);
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                       || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32 && IsFloatExtensible(f));

        float Sample(int frame)
        {
            float sum = 0;
            int off = frame * ch * bytes;
            for (int c = 0; c < ch; c++, off += bytes)
            {
                sum += isFloat ? BitConverter.ToSingle(data, off)
                     : bytes == 2 ? BitConverter.ToInt16(data, off) / 32768f
                     : bytes == 3 ? ((data[off] << 8 | data[off + 1] << 16 | data[off + 2] << 24) >> 8) / 8388608f
                     : BitConverter.ToInt32(data, off) / 2147483648f;
            }
            return sum / ch;
        }

        double step = (double)f.SampleRate / Rate;
        var outList = new List<short>(frames * Rate / Math.Max(1, f.SampleRate) + 2);
        // _srcPos 는 이번 조각 시작 기준 위치. 음수면 앞 조각의 마지막 샘플과 섞는다.
        while (_srcPos < frames - 1)
        {
            int i = (int)Math.Floor(_srcPos);
            double frac = _srcPos - i;
            float a = i < 0 ? _lastSample : Sample(i);
            float b = Sample(i + 1);
            float v = (float)(a + (b - a) * frac);
            outList.Add((short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue));
            _srcPos += step;
        }
        if (frames > 0) _lastSample = Sample(frames - 1);
        _srcPos -= frames;
        return outList.ToArray();
    }

    static bool IsFloatExtensible(WaveFormat f) =>
        f is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

    public void Dispose()
    {
        _stopping = true;
        _queue.CompleteAdding();
    }
}
