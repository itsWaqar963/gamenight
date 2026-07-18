// Non-consuming PTT on key "2" / numpad 2.
// Uses a low-level keyboard hook so other apps (and typing) still receive the key.
// Listening is enabled only while joined to a room with push-to-talk on.
using System.Runtime.InteropServices;

namespace GameNight.Agent.Voice;

public sealed class PttHotkey : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int Vk2 = 0x32;
    private const int VkNumpad2 = 0x62;

    private readonly LowLevelKeyboardProc _proc;
    private readonly object _gate = new();
    private IntPtr _hook = IntPtr.Zero;
    private bool _listening;
    private bool _armed;
    private bool _disposed;

    public event Action<bool>? PttChanged; // true=down, false=up

    public PttHotkey()
    {
        // Keep delegate alive for the native hook lifetime.
        _proc = HookCallback;
    }

    /// <summary>
    /// When true, hold-2 raises PttChanged. When false, key 2 is ignored for voice
    /// (and is never swallowed either way — typing always works).
    /// </summary>
    public void SetListening(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_listening == enabled) return;
            _listening = enabled;
            if (enabled)
                InstallHook();
            else
            {
                if (_armed)
                {
                    _armed = false;
                    try { PttChanged?.Invoke(false); } catch { /* ignore */ }
                }
                RemoveHook();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _listening = false;
            _armed = false;
            RemoveHook();
        }
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        // WH_KEYBOARD_LL: module of this process (works with single-file publish).
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            AgentLog.Write("voice.log", "SetWindowsHookEx(WH_KEYBOARD_LL) failed — in-window PTT still works");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _listening)
        {
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (info.VkCode is Vk2 or VkNumpad2)
            {
                bool down = msg is WmKeyDown or WmSysKeyDown;
                bool up = msg is WmKeyUp or WmSysKeyUp;

                if (down && !_armed)
                {
                    _armed = true;
                    Raise(true);
                }
                else if (up && _armed)
                {
                    _armed = false;
                    Raise(false);
                }
            }
        }

        // Never consume — always pass the key through so typing and games still work.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Raise(bool down)
    {
        try { PttChanged?.Invoke(down); } catch { /* ignore */ }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
