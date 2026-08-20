using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassMusicPlayer.Services;

/// <summary>
/// Global keyboard handling: media keys (play/pause, next, prev, stop) via a
/// low-level keyboard hook, plus Ctrl+Alt combos that work app-wide.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_MEDIA_NEXT_TRACK = 0xB0;
    private const int VK_MEDIA_PREV_TRACK = 0xB1;
    private const int VK_MEDIA_STOP = 0xB2;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    private const int VK_SPACE = 0x20;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_M = 0x4D;
    private const int VK_S = 0x53;
    private const int VK_L = 0x4C;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public event Action? MediaPlayPause;
    public event Action? MediaNext;
    public event Action? MediaPrev;
    public event Action? MediaStop;
    public event Action? HotkeyPlayPause;
    public event Action? HotkeyNext;
    public event Action? HotkeyPrev;
    public event Action? HotkeyVolumeUp;
    public event Action? HotkeyVolumeDown;
    public event Action? HotkeyMute;
    public event Action? HotkeyToggleShuffle;
    public event Action? HotkeyToggleLoop;

    public GlobalHotkeys()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule == null) return;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool handled = false;

            switch (info.vkCode)
            {
                case VK_MEDIA_PLAY_PAUSE:
                    MediaPlayPause?.Invoke();
                    handled = true;
                    break;
                case VK_MEDIA_NEXT_TRACK:
                    MediaNext?.Invoke();
                    handled = true;
                    break;
                case VK_MEDIA_PREV_TRACK:
                    MediaPrev?.Invoke();
                    handled = true;
                    break;
                case VK_MEDIA_STOP:
                    MediaStop?.Invoke();
                    handled = true;
                    break;
                case VK_SPACE when IsCtrlAlt():
                    HotkeyPlayPause?.Invoke();
                    handled = true;
                    break;
                case VK_RIGHT when IsCtrlAlt():
                    HotkeyNext?.Invoke();
                    handled = true;
                    break;
                case VK_LEFT when IsCtrlAlt():
                    HotkeyPrev?.Invoke();
                    handled = true;
                    break;
                case VK_UP when IsCtrlAlt():
                    HotkeyVolumeUp?.Invoke();
                    handled = true;
                    break;
                case VK_DOWN when IsCtrlAlt():
                    HotkeyVolumeDown?.Invoke();
                    handled = true;
                    break;
                case VK_M when IsCtrlAlt():
                    HotkeyMute?.Invoke();
                    handled = true;
                    break;
                case VK_S when IsCtrlAlt():
                    HotkeyToggleShuffle?.Invoke();
                    handled = true;
                    break;
                case VK_L when IsCtrlAlt():
                    HotkeyToggleLoop?.Invoke();
                    handled = true;
                    break;
            }

            if (handled) return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsCtrlAlt()
    {
        return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 &&
               (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
    }

    public void Dispose()
    {
        Uninstall();
    }
}