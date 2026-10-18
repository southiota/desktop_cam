using System.Diagnostics;
using System.Runtime.InteropServices;
using static DesktopCam.Native;

namespace DesktopCam;

public record WindowInfo(IntPtr Handle, string Title, string Process, bool Minimized);

/// <summary>다른 프로그램 창을 녹화 영역에 맞추고, 원래 위치로 되돌린다.</summary>
public class WindowFitter
{
    readonly Dictionary<IntPtr, WINDOWPLACEMENT> _saved = new();

    static readonly HashSet<string> SkipClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>작업표시줄에 보이는 일반 창 목록 (이 프로그램 창 제외).</summary>
    public static List<WindowInfo> ListWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (IsCandidate(h)) list.Add(new WindowInfo(h, GetTitle(h), ProcessName(h), IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>맞추기 대상이 될 수 있는 창인지 (보이는 일반 창, 바탕화면·작업표시줄·이 프로그램 제외).</summary>
    public static bool IsCandidate(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (IsCloaked(h) || IsOwnProcess(h) || SkipClasses.Contains(GetClass(h))) return false;
        return !string.IsNullOrWhiteSpace(GetTitle(h));
    }

    static readonly Dictionary<uint, string> ProcessNames = new();

    static string ProcessName(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (ProcessNames.TryGetValue(pid, out var name)) return name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch { name = ""; }
        if (ProcessNames.Count > 512) ProcessNames.Clear();   // PID 가 재사용되므로 너무 커지면 비움
        ProcessNames[pid] = name;
        return name;
    }

    public bool HasSaved(IntPtr h) => _saved.ContainsKey(h);

    /// <summary>창의 보이는 테두리가 region과 정확히 겹치도록 옮긴다. 결과로 실제 맞춰진 크기를 돌려준다.</summary>
    public (bool ok, Native.RECT actual) Fit(IntPtr h, Native.RECT region)
    {
        if (!IsWindow(h)) return (false, default);

        if (!_saved.ContainsKey(h))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(h, ref wp)) _saved[h] = wp;
        }

        // 최대화·최소화 상태에서는 위치를 바꿀 수 없으므로 먼저 보통 상태로.
        // 최대화 상태에서 최소화된 창은 한 번 복원하면 최대화로 돌아오므로 두 번까지 복원한다.
        for (int i = 0; i < 2 && (IsIconic(h) || IsZoomed(h)); i++) ShowWindow(h, SW_RESTORE);

        // Windows 10/11 창은 보이지 않는 테두리(약 7px)가 있으므로 그만큼 더 크게 잡는다
        GetWindowRect(h, out var outer);
        var frame = GetFrameBounds(h);
        int l = frame.Left - outer.Left, t = frame.Top - outer.Top;
        int r = outer.Right - frame.Right, b = outer.Bottom - frame.Bottom;

        bool ok = SetWindowPos(h, IntPtr.Zero,
            region.Left - l, region.Top - t,
            region.Width + l + r, region.Height + t + b,
            SWP_NOZORDER | SWP_NOACTIVATE);
        SetForegroundWindow(h);

        var actual = GetFrameBounds(h);
        return (ok, actual);
    }

    /// <summary>맞추기 전 위치·크기·상태(최대화 등)로 되돌린다.</summary>
    public bool Restore(IntPtr h)
    {
        if (!_saved.Remove(h, out var wp) || !IsWindow(h)) return false;
        SetWindowPlacement(h, ref wp);
        SetForegroundWindow(h);
        return true;
    }

    public static bool Matches(Native.RECT a, Native.RECT b) =>
        Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 &&
        Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    /// <summary>지금 영역에 맞춰져 있는 창인지 (맞춘 뒤 사용자가 옮겼으면 false).</summary>
    public bool IsFitted(IntPtr h, Native.RECT region) =>
        _saved.ContainsKey(h) && !IsIconic(h) && Matches(GetFrameBounds(h), region);
}
