using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopCam;

/// <summary>
/// 시스템 전체 키보드 · 마우스 저수준 훅 (WH_KEYBOARD_LL · WH_MOUSE_LL).
/// 녹화 영역 안에서만 쓰는 Ctrl 단축키(Ctrl+← → · L ; ' . /)와 Ctrl+우클릭 메뉴용.
/// 콜백은 설치한 스레드(UI 스레드)에서 불리고 빨리 돌아와야 하므로, 판단만 하고 실제 동작은 Dispatcher 로 미룬다.
/// </summary>
public sealed class InputHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const int WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public Native.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc _kbProc, _msProc;   // GC 가 가져가지 않게 붙잡아 둔다
    IntPtr _kb, _ms;
    bool _eatRightUp;

    /// <summary>Ctrl(Shift · Alt · Win 없이) + 가상 키 코드가 눌렸을 때. 화면 좌표의 커서 위치를 같이 준다. true 면 그 키를 먹는다.</summary>
    public Func<int, Native.POINT, bool>? CtrlKey;
    /// <summary>Ctrl + 우클릭을 누를 때 (true 면 그 클릭을 먹는다). 놓을 때 RightClicked 가 불린다.</summary>
    public Func<Native.POINT, bool>? CtrlRightDown;
    public event Action<Native.POINT>? CtrlRightClicked;

    public bool Installed => _kb != IntPtr.Zero && _ms != IntPtr.Zero;

    public InputHook()
    {
        _kbProc = OnKeyboard;
        _msProc = OnMouse;
        var mod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _kb = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
        _ms = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    static bool OnlyCtrl() => Down(VK_CONTROL) && !Down(VK_SHIFT) && !Down(VK_MENU) && !Down(VK_LWIN) && !Down(VK_RWIN);

    IntPtr OnKeyboard(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && (int)w is WM_KEYDOWN or WM_SYSKEYDOWN && CtrlKey != null)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            if (OnlyCtrl())
            {
                Native.GetCursorPos(out var p);
                try { if (CtrlKey((int)k.vkCode, p)) return 1; } catch { }
            }
        }
        return CallNextHookEx(_kb, code, w, l);
    }

    IntPtr OnMouse(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int msg = (int)w;
            if (msg == WM_RBUTTONDOWN && CtrlRightDown != null && OnlyCtrl())
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                bool eat = false;
                try { eat = CtrlRightDown(m.pt); } catch { }
                if (eat) { _eatRightUp = true; return 1; }
            }
            else if (msg == WM_RBUTTONUP && _eatRightUp)
            {
                _eatRightUp = false;
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                CtrlRightClicked?.Invoke(m.pt);
                return 1;   // 아래 프로그램에 오른쪽 메뉴가 뜨지 않게 놓는 것도 먹는다
            }
        }
        return CallNextHookEx(_ms, code, w, l);
    }

    public void Dispose()
    {
        if (_kb != IntPtr.Zero) { UnhookWindowsHookEx(_kb); _kb = IntPtr.Zero; }
        if (_ms != IntPtr.Zero) { UnhookWindowsHookEx(_ms); _ms = IntPtr.Zero; }
    }
}
