// Non-consuming global PTT via WH_KEYBOARD_LL.
// Bound VK is user-configurable; keys are never swallowed (CallNextHookEx always).
// Listening is enabled only while joined to a room with push-to-talk on.
using System.Runtime.InteropServices;

namespace GameNight.Agent.Voice;

public sealed class PttHotkey : IDisposable
{
    public const int DefaultVk = 0x32; // '2'

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly LowLevelKeyboardProc _proc;
    private readonly object _gate = new();
    private IntPtr _hook = IntPtr.Zero;
    private bool _listening;
    private bool _armed;
    private bool _disposed;
    private int _boundVk = DefaultVk;
    private int? _numpadTwin;

    public event Action<bool>? PttChanged; // true=down, false=up

    public int BoundVk
    {
        get { lock (_gate) return _boundVk; }
    }

    public PttHotkey()
    {
        // Keep delegate alive for the native hook lifetime.
        _proc = HookCallback;
        SetBoundKey(DefaultVk);
    }

    /// <summary>
    /// Persist primary VK. Digit-row keys (0–9) also accept the matching numpad twin.
    /// </summary>
    public void SetBoundKey(int vkCode)
    {
        lock (_gate)
        {
            _boundVk = vkCode & 0xFF;
            _numpadTwin = DigitRowToNumpad(_boundVk);
            if (_armed)
            {
                _armed = false;
                try { PttChanged?.Invoke(false); } catch { /* ignore */ }
            }
        }
    }

    public bool MatchesVk(int vkCode)
    {
        lock (_gate)
        {
            int vk = vkCode & 0xFF;
            return vk == _boundVk || (_numpadTwin is int twin && vk == twin);
        }
    }

    public bool MatchesKeys(Keys key)
    {
        return MatchesVk((int)key);
    }

    /// <summary>
    /// When true, holding the bound key raises PttChanged. When false, the key is ignored for voice
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
            if (MatchesVk(info.VkCode))
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

    /// <summary>Map digit-row VK 0x30–0x39 to numpad 0x60–0x69; otherwise null.</summary>
    private static int? DigitRowToNumpad(int vk)
    {
        if (vk is >= 0x30 and <= 0x39)
            return 0x60 + (vk - 0x30);
        return null;
    }

    public static string DescribeVk(int vkCode)
    {
        int vk = vkCode & 0xFF;
        if (vk is >= 0x30 and <= 0x39)
            return ((char)vk).ToString();
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x7B)
            return "F" + (vk - 0x6F);
        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            0xBA => ";",
            0xDE => "'",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBD => "-",
            0xBB => "=",
            0xC0 => "`",
            _ when vk is >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
            _ => $"VK 0x{vk:X2}",
        };
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
