using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace DesktopCam;

/// <summary>
/// 녹화하는 동안 마이크 소리를 그대로(처리 전) wav 로 따로 남긴다. 끝나면 AudioPost 가 다듬어 영상에 합친다.
/// 일시정지 중에는 쓰지 않고, 녹화 중 마이크를 끄면 그 시간만큼 무음을 써서 영상과 시간이 어긋나지 않게 한다.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    readonly object _lock = new();
    WaveFileWriter? _writer;
    WaveFormat? _format;
    long _firstSampleTicks;   // 첫 소리가 들어온 시각 (Stopwatch)

    public string? Path { get; private set; }
    public bool Paused { get; set; }
    public bool Muted { get; set; }

    /// <summary>첫 소리가 들어온 시각. 영상 첫 프레임 시각과 빼서 맞춘다.</summary>
    public long FirstSampleTicks => Interlocked.Read(ref _firstSampleTicks);

    public void Start(string path, WaveFormat format)
    {
        lock (_lock)
        {
            Path = path;
            _format = format;
            _writer = new WaveFileWriter(path, format);
            _firstSampleTicks = 0;
        }
    }

    /// <summary>캡처 스레드에서 불린다.</summary>
    public void Feed(byte[] data, int count, WaveFormat format)
    {
        lock (_lock)
        {
            if (_writer == null || Paused || !format.Equals(_format)) return;
            if (_firstSampleTicks == 0)
            {
                // 이 조각은 방금 끝난 소리이므로, 조각 길이만큼 앞선 때가 시작
                double sec = (double)count / format.AverageBytesPerSecond;
                Interlocked.Exchange(ref _firstSampleTicks, Stopwatch.GetTimestamp() - (long)(sec * Stopwatch.Frequency));
            }
            if (Muted) _writer.Write(new byte[count], 0, count);
            else _writer.Write(data, 0, count);
        }
    }

    /// <summary>끝내고 파일 경로를 돌려준다.</summary>
    public string? Stop()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            return Path;
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 갑자기 끊겨 머리(RIFF·data 크기)가 0 으로 남은 wav 를 고친다. 크기를 실제 파일 길이에 맞춘다.
    /// </summary>
    public static bool RepairHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        if (fs.Length < 44) return false;
        var head = new byte[Math.Min(4096, (int)fs.Length)];
        fs.ReadExactly(head);
        if (head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F') return false;
        int pos = 12;
        while (pos + 8 <= head.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(head, pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(pos + 4));
            if (id == "data")
            {
                long dataLen = fs.Length - (pos + 8);
                int blockAlign = 1;
                int fmt = System.Text.Encoding.ASCII.GetString(head).IndexOf("fmt ", StringComparison.Ordinal);
                if (fmt >= 0) blockAlign = Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(fmt + 20)));
                dataLen -= dataLen % blockAlign;   // 반쯤 써진 표본은 버림
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Min(dataLen, int.MaxValue));
                fs.Position = pos + 4; fs.Write(b);
                BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Min(pos + dataLen, int.MaxValue));
                fs.Position = 4; fs.Write(b);
                fs.SetLength(pos + 8 + dataLen);
                return true;
            }
            pos += 8 + size + (size & 1);
        }
        return false;
    }
}
