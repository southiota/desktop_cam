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

    /// <summary>맞춘 창마다 영역 밖으로 뺀 두께 (왼·위·오른·아래, 0 = 그냥 맞춤). 맞춘 상태인지 판단할 때 쓴다.</summary>
    readonly Dictionary<IntPtr, Native.RECT> _inset = new();

    public enum CaptionResult { None, Lifted, Web, NotFound, Blocked }

    /// <summary>Inset = 영역 밖으로 뺀 두께 (Left·Top·Right·Bottom 에 각각 왼·위·오른·아래 두께). Lift = 위 두께.</summary>
    public record FitResult(bool Ok, Native.RECT Actual, Native.RECT Target, Native.RECT Inset, CaptionResult Caption)
    {
        public int Lift => Inset.Top;
    }

    static Native.RECT Grow(Native.RECT r, Native.RECT ins) =>
        new(r.Left - ins.Left, r.Top - ins.Top, r.Right + ins.Right, r.Bottom + ins.Bottom);

    /// <summary>
    /// 창의 보이는 테두리를 region 에 맞춘다. 제목 표시줄 빼기가 켜져 있으면
    /// - 웹 브라우저: 탭 줄 · 주소창 · 왼쪽 · 오른쪽 · 아래 테두리를 모두 영역 밖으로 빼고 웹 화면만 영역을 채운다.
    /// - 그 밖의 창: 테두리를 (region.Left, region.Top − H, region.Width, region.Height + H) 로 잡아 제목 표시줄(높이 H)을 영역 위로 밀어 올린다.
    /// </summary>
    public FitResult Fit(IntPtr h, Native.RECT region)
    {
        if (!IsWindow(h)) return new(false, default, region, default, CaptionResult.None);

        if (!_saved.ContainsKey(h))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(h, ref wp)) _saved[h] = wp;
        }

        // 최대화·최소화 상태에서는 위치를 바꿀 수 없으므로 먼저 보통 상태로.
        // 최대화 상태에서 최소화된 창은 한 번 복원하면 최대화로 돌아오므로 두 번까지 복원한다.
        for (int i = 0; i < 2 && (IsIconic(h) || IsZoomed(h)); i++) ShowWindow(h, SW_RESTORE);

        var ins = new Native.RECT();
        var caption = CaptionResult.None;
        bool web = StripCaption && IsBrowser(h);
        if (StripCaption)
        {
            ins.Top = TitleBarHeight(h, GetFrameBounds(h));
            caption = ins.Top > 0 ? CaptionResult.Lifted : CaptionResult.NotFound;
        }

        var target = Grow(region, ins);
        bool ok = Place(h, target);

        // 브라우저: 옮긴 뒤 웹 화면 자리를 재서 탭 줄 · 주소창 · 테두리 두께를 구하고, 그만큼 크게 다시 맞춘다.
        // (창 크기에 따라 줄 높이가 바뀔 수 있어 두 번까지 고쳐 맞춤)
        if (ok && web)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                if (WebInset(h) is not Native.RECT w) break;
                ins = w;
                caption = CaptionResult.Web;
                var next = Grow(region, ins);
                if (Matches(next, target)) break;
                target = next;
                if (!(ok = Place(h, target))) break;
            }
        }
        SetForegroundWindow(h);

        var actual = GetFrameBounds(h);
        // Windows 나 프로그램이 위치를 되돌려 위로 올라가지 못한 경우
        if (ok && ins.Top > 0 && actual.Top > target.Top + 1) caption = CaptionResult.Blocked;
        if (ok) _inset[h] = ins;
        return new(ok, actual, target, ins, caption);
    }

    /// <summary>보이는 테두리를 target 에 맞춘다. Windows 10/11 창은 보이지 않는 테두리(약 7px)가 있으므로 그만큼 더 크게 잡는다.
    /// 영역이 화면 맨 위면 Top 이 음수(화면 위 밖)가 되지만 그대로 둔다.</summary>
    static bool Place(IntPtr h, Native.RECT target)
    {
        GetWindowRect(h, out var outer);
        var frame = GetFrameBounds(h);
        int l = frame.Left - outer.Left, t = frame.Top - outer.Top;
        int r = outer.Right - frame.Right, b = outer.Bottom - frame.Bottom;
        return SetWindowPos(h, IntPtr.Zero,
            target.Left - l, target.Top - t,
            target.Width + l + r, target.Height + t + b,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "whale", "brave", "opera", "vivaldi", "chromium", "librewolf", "waterfox", "floorp", "zen",
    };

    public static bool IsBrowser(IntPtr h) => Browsers.Contains(ProcessName(h));

    /// <summary>
    /// 브라우저 창에서 웹 화면 바깥 두께 (왼·위·오른·아래, 실제 픽셀). 못 찾으면 null.
    /// Chromium 계열(Chrome · Edge · Whale …)은 웹 화면 자식 창(Chrome_RenderWidgetHostHWND)을,
    /// Firefox 는 UI Automation 의 문서(Document) 요소를 잰다.
    /// 브라우저가 바뀐 창 크기를 반영할 때까지 잠깐(최대 약 0.5초) 기다린다.
    /// </summary>
    static Native.RECT? WebInset(IntPtr h)
    {
        Native.RECT? last = null;
        bool chromium = ChromiumContent(h) != null;
        for (int i = 0; i < 12; i++)
        {
            var frame = GetFrameBounds(h);
            var content = chromium ? ChromiumContent(h) : UiaDocument(h);
            if (content is Native.RECT c && c.Width > 100 && c.Height > 100)
            {
                var ins = new Native.RECT(c.Left - frame.Left, c.Top - frame.Top, frame.Right - c.Right, frame.Bottom - c.Bottom);
                bool sane = ins.Left >= 0 && ins.Top >= 0 && ins.Right >= 0 && ins.Bottom >= 0 &&
                            ins.Top < frame.Height / 2 && ins.Left + ins.Right < frame.Width / 2 && ins.Bottom < frame.Height / 4;
                if (!sane) return null;
                if (last is Native.RECT p && Matches(p, ins)) return ins;   // 두 번 연달아 같으면 자리 잡은 것
                last = ins;
            }
            else if (i >= 2 && last == null) return null;
            Thread.Sleep(40);
        }
        return last;
    }

    /// <summary>Chromium 웹 화면 자식 창 중 보이는 가장 큰 것의 자리. 없으면 null.</summary>
    static Native.RECT? ChromiumContent(IntPtr h)
    {
        Native.RECT? best = null;
        EnumChildWindows(h, (c, _) =>
        {
            if (IsWindowVisible(c) && GetClass(c) == "Chrome_RenderWidgetHostHWND" && GetWindowRect(c, out var r) &&
                (best is not Native.RECT b || (long)r.Width * r.Height > (long)b.Width * b.Height))
                best = r;
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>UI Automation 으로 찾은 첫 문서(웹 화면) 요소의 자리 (Firefox 등). 없으면 null.</summary>
    static Native.RECT? UiaDocument(IntPtr h)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(h);
            var doc = root?.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Document));
            if (doc == null) return null;
            var b = doc.Current.BoundingRectangle;
            if (b.IsEmpty || b.Width < 1) return null;
            return new Native.RECT((int)Math.Round(b.Left), (int)Math.Round(b.Top), (int)Math.Round(b.Right), (int)Math.Round(b.Bottom));
        }
        catch { return null; }
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
        _inset.Remove(h);
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
            _inset.Remove(h);
            _saved.Remove(h, out var wp);
            SetWindowPlacement(h, ref wp);
        }
    }

    public static bool Matches(Native.RECT a, Native.RECT b) =>
        Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 &&
        Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    /// <summary>지금 영역에 맞춰져 있는 창인지 (맞춘 뒤 사용자가 옮겼으면 false).
    /// 제목 표시줄 빼기로 영역 밖까지 늘린 창도 맞춘 상태로 본다.</summary>
    public bool IsFitted(IntPtr h, Native.RECT region)
    {
        if (!_saved.ContainsKey(h) || IsIconic(h)) return false;
        var frame = GetFrameBounds(h);
        if (Matches(frame, region)) return true;
        return _inset.TryGetValue(h, out var ins) && Matches(frame, Grow(region, ins));
    }
}
