using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopCam;

/// <summary>
/// 녹화가 끝난 뒤 따로 녹음한 마이크 소리를 다듬어 영상에 합친다 (ffmpeg).
/// 영상은 다시 인코딩하지 않고 그대로 복사하므로 빠르다 (소리만 새로 만듦).
/// </summary>
public static class AudioPost
{
    /// <summary>
    /// video(영상 + 시스템 소리 또는 영상만)와 mic.wav 를 합쳐 output 에 쓴다.
    /// micLead: 마이크 녹음이 영상보다 먼저 시작된 시간(초). 음수면 영상이 먼저.
    /// progress: 0~1. 실패하면 예외(마지막 ffmpeg 로그 포함).
    /// </summary>
    public static async Task MixAsync(string video, string micWav, double micLead, MicFx fx, string output,
                                      double durationSec, IProgress<double>? progress, CancellationToken ct = default)
    {
        string ffmpeg = Tools.Ffmpeg ?? throw new FileNotFoundException("ffmpeg.exe 가 없습니다.");
        bool hasSystemAudio = await HasAudioAsync(ffmpeg, video, ct);

        string chain = fx.BuildChain(Tools.RnnoiseModel, WavChannels(micWav));
        string micIn = micLead >= 0 ? $"-ss {micLead.ToString("0.###", CultureInfo.InvariantCulture)} " : "";
        if (micLead < 0) chain = $"adelay={(int)(-micLead * 1000)}:all=1," + chain;

        string graph = hasSystemAudio
            ? $"[1:a]{chain}[m];[0:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[s];[s][m]amix=inputs=2:duration=first:normalize=0[a]"
            : $"[1:a]{chain},apad[a]";

        string args = $"-hide_banner -nostats -y -i \"{video}\" {micIn}-i \"{micWav}\" -filter_complex \"{graph}\" " +
                      $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k {(hasSystemAudio ? "" : "-shortest ")}" +
                      $"-movflags +faststart -progress pipe:1 \"{output}\"";
        await RunAsync(ffmpeg, args, durationSec, progress, ct);
    }

    /// <summary>미리 듣기용: wav 하나를 다듬어 wav 로.</summary>
    public static Task ProcessWavAsync(string input, MicFx fx, string output, CancellationToken ct = default)
    {
        string ffmpeg = Tools.Ffmpeg ?? throw new FileNotFoundException("ffmpeg.exe 가 없습니다.");
        string args = $"-hide_banner -nostats -y -i \"{input}\" -af \"{fx.BuildChain(Tools.RnnoiseModel, WavChannels(input))}\" -c:a pcm_s16le \"{output}\"";
        return RunAsync(ffmpeg, args, 0, null, ct);
    }

    /// <summary>wav 머리에서 채널 수를 읽는다 (fmt 덩어리의 22번째 바이트).</summary>
    static int WavChannels(string wav)
    {
        try
        {
            using var fs = File.OpenRead(wav);
            var head = new byte[64];
            int n = fs.Read(head, 0, head.Length);
            int i = Encoding.ASCII.GetString(head, 0, n).IndexOf("fmt ", StringComparison.Ordinal);
            return i >= 0 ? BitConverter.ToInt16(head, i + 10) : 2;
        }
        catch { return 2; }
    }

    static async Task<bool> HasAudioAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg, $"-hide_banner -i \"{file}\"")
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return err.Contains("Audio:");
    }

    static async Task RunAsync(string ffmpeg, string args, double durationSec, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // 다음 녹화·작업을 방해하지 않게

        var errTail = new Queue<string>();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync(ct)) != null)
            {
                errTail.Enqueue(line);
                if (errTail.Count > 12) errTail.Dequeue();
            }
        }, ct);

        string? l;
        while ((l = await p.StandardOutput.ReadLineAsync(ct)) != null)
        {
            if (progress != null && durationSec > 0 && l.StartsWith("out_time_us=") &&
                long.TryParse(l.AsSpan(12), out long us))
                progress.Report(Math.Clamp(us / 1e6 / durationSec, 0, 1));
        }
        await p.WaitForExitAsync(ct);
        await errTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg 처리 실패: " + string.Join(" / ", errTail.TakeLast(3)));
        progress?.Report(1);
    }
}
