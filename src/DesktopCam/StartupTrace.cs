using System.Diagnostics;
using System.IO;

namespace DesktopCam;

/// <summary>확인용: 환경 변수 DESKTOPCAM_TRACE=1 이면 시작 단계별 시간을 %TEMP%\desktopcam_trace.txt 에 적는다.</summary>
public static class StartupTrace
{
    static readonly bool On = Environment.GetEnvironmentVariable("DESKTOPCAM_TRACE") == "1";
    static readonly Stopwatch Sw = Stopwatch.StartNew();
    static double _last;

    public static void Mark(string step)
    {
        if (!On) return;
        double now = Sw.Elapsed.TotalMilliseconds;
        double sinceProcess = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "desktopcam_trace.txt"),
            $"{sinceProcess,7:0}ms (+{now - _last,6:0}) {step}\n");
        _last = now;
    }
}
