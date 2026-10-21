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

    /// <summary>
    /// 켜면 맞추는 창을 제목 표시줄 높이만큼 위로 늘려, 제목 표시줄은 녹화 영역 바로 위(영역 밖)에 두고
    /// 그 아래 내용이 영역을 꽉 채우게 한다. 창 스타일은 바꾸지 않는다.
    /// </summary>
    public bool StripCaption { get; set; }

    /// <summary>맞춘 창마다 위로 올린 제목 표시줄 높이 (0 = 그냥 맞춤). 맞춘 상태인지 판단할 때 쓴다.</summary>
    readonly Dictionary<IntPtr, int> _lift = new();

    public enum CaptionResult { None, Lifted, NotFound, Blocked }

    public record FitResult(bool Ok, Native.RECT Actual, Native.RECT Target, int Lift, CaptionResult Caption);

    /// <summary>
    /// 창의 보이는 테두리를 region 에 맞춘다. 제목 표시줄 빼기가 켜져 있으면 테두리를
    /// (region.Left, region.Top − H, region.Width, region.Height + H) 로 잡아 제목 표시줄(높이 H)을 영역 위로 밀어 올린다.
    /// </summary>
    public FitResult Fit(IntPtr h, Native.RECT region)
    {
        if (!IsWindow(h)) return new(false, default, region, 0, CaptionResult.None);

        if (!_saved.ContainsKey(h))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(h, ref wp)) _saved[h] = wp;
        }

        // 최대화·최소화 상태에서는 위치를 바꿀 수 없으므로 먼저 보통 상태로.
        // 최대화 상태에서 최소화된 창은 한 번 복원하면 최대화로 돌아오므로 두 번까지 복원한다.
        for (int i = 0; i < 2 && (IsIconic(h) || IsZoomed(h)); i++) ShowWindow(h, SW_RESTORE);

        // Windows 10/11 창은 보이지 않는 테두리(약 7px)가 있으므로 그만큼 더 크게 잡는다.
        GetWindowRect(h, out var outer);
        var frame = GetFrameBounds(h);
        int l = frame.Left - outer.Left, t = frame.Top - outer.Top;
        int r = outer.Right - frame.Right, b = outer.Bottom - frame.Bottom;

        int lift = 0;
        var caption = CaptionResult.None;
        if (StripCaption)
        {
            lift = TitleBarHeight(h, frame);
            caption = lift > 0 ? CaptionResult.Lifted : CaptionResult.NotFound;
        }
        var target = new Native.RECT(region.Left, region.Top - lift, region.Right, region.Bottom);

        // 영역이 화면 맨 위면 Top 이 음수(화면 위 밖)가 되지만 그대로 둔다.
        bool ok = SetWindowPos(h, IntPtr.Zero,
            target.Left - l, target.Top - t,
            target.Width + l + r, target.Height + t + b,
            SWP_NOZORDER | SWP_NOACTIVATE);
        SetForegroundWindow(h);

        var actual = GetFrameBounds(h);
        // Windows 나 프로그램이 위치를 되돌려 위로 올라가지 못한 경우
        if (ok && lift > 0 && actual.Top > target.Top + 1) caption = CaptionResult.Blocked;
        if (ok) _lift[h] = lift;
        return new(ok, actual, target, lift, caption);
    }

    /// <summary>스스로 그리는 제목 표시줄 높이 (배율 100% 기준 DIP). 값을 바꾸려면 여기만 고친다.</summary>
    const int SelfDrawnTitleBarDip = 40;

    /// <summary>
    /// 제목 표시줄 높이 H (실제 픽셀). 못 찾으면 0 (그냥 맞춤).
    /// 1) Windows 가 그리는 제목 표시줄: 보이는 테두리 위쪽 ~ 내용 영역 위쪽 (8 이상이면 그 값).
    /// 2) 스스로 그리는 창: DWM 캡션 버튼(최소화·최대화·닫기)이 있으면 제목 표시줄이 있는 창으로 보고 40 × 배율.
    ///    버튼 높이(30)는 실제 줄보다 작아서 높이 값으로 쓰지 않는다.
    ///    실측 (Win11 · 100%): 탐색기 · Chrome · Windows Terminal 탭 줄 약 40, Orca(Electron) 약 35, 시스템 표준 캡션 31.
    /// </summary>
    static int TitleBarHeight(IntPtr h, Native.RECT frame)
    {
        var p = new POINT();
        if (ClientToScreen(h, ref p))
        {
            int drawn = p.Y - frame.Top;
            if (drawn >= 8) return drawn;
        }
        if (DwmGetWindowAttribute(h, DWMWA_CAPTION_BUTTON_BOUNDS, out RECT btn, Marshal.SizeOf<RECT>()) == 0 && btn.Height > 0)
        {
            uint dpi = GetDpiForWindow(h);
            if (dpi == 0) dpi = 96;
            return (int)Math.Round(SelfDrawnTitleBarDip * dpi / 96.0);
        }
        return 0;
    }

    /// <summary>맞추기 전 위치·크기·상태(최대화 등)로 되돌린다.</summary>
    public bool Restore(IntPtr h)
    {
        _lift.Remove(h);
        if (!_saved.Remove(h, out var wp) || !IsWindow(h)) return false;
        SetWindowPlacement(h, ref wp);
        SetForegroundWindow(h);
        return true;
    }

    /// <summary>프로그램을 끌 때: 아직 영역에 맞춰져 있는 창만 원래대로 (맞춘 뒤 사용자가 옮긴 창은 그대로 둔다).</summary>
    public void RestoreAllFitted(Native.RECT region)
    {
        foreach (var h in _saved.Keys.ToList())
        {
            if (!IsWindow(h) || !IsFitted(h, region)) continue;
            _lift.Remove(h);
            _saved.Remove(h, out var wp);
            SetWindowPlacement(h, ref wp);
        }
    }

    public static bool Matches(Native.RECT a, Native.RECT b) =>
        Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 &&
        Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    /// <summary>지금 영역에 맞춰져 있는 창인지 (맞춘 뒤 사용자가 옮겼으면 false).
    /// 제목 표시줄 빼기로 위로 늘린 창도 맞춘 상태로 본다.</summary>
    public bool IsFitted(IntPtr h, Native.RECT region)
    {
        if (!_saved.ContainsKey(h) || IsIconic(h)) return false;
        var frame = GetFrameBounds(h);
        if (Matches(frame, region)) return true;
        return _lift.TryGetValue(h, out int lift) && lift > 0 &&
               Matches(frame, new Native.RECT(region.Left, region.Top - lift, region.Right, region.Bottom));
    }
}
