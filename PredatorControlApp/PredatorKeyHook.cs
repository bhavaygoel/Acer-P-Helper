using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    // Catches the dedicated PredatorSense / NitroSense key with a low-level keyboard hook and
    // swallows it, so it runs our action instead of opening Acer's app. The key's scan code
    // varies by model, so it can be learned: the next key pressed after Learn() becomes the key.
    [SupportedOSPlatform("windows")]
    internal sealed class PredatorKeyHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_EXTENDED = 0x01;
        private const uint LLKHF_INJECTED = 0x10;

        // The NitroSense key on recent Acer laptops sends extended scan code 0x75
        public const int DefaultScanCode = 0x75;
        public const bool DefaultExtended = true;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint vkCode, scanCode, flags, time;
            public UIntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

        private readonly HookProc _callback;
        private IntPtr _hook;
        private bool _learning;
        private bool _held;

        public int ScanCode { get; set; } = DefaultScanCode;
        public bool Extended { get; set; } = DefaultExtended;

        /// <summary>Raised on the UI thread when the configured key is pressed.</summary>
        public event Action? Pressed;

        /// <summary>Raised when Learn() has captured a key: scan code and extended flag.</summary>
        public event Action<int, bool>? Learned;

        public PredatorKeyHook()
        {
            _callback = OnKeyboardEvent;
        }

        public bool Start()
        {
            if (_hook != IntPtr.Zero) return true;
            using var module = Process.GetCurrentProcess().MainModule;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(module?.ModuleName), 0);
            return _hook != IntPtr.Zero;
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        public void Learn() => _learning = true;
        public void CancelLearn() => _learning = false;

        private IntPtr OnKeyboardEvent(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                int msg = (int)wParam;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool extended = (data.flags & LLKHF_EXTENDED) != 0;

                if ((data.flags & LLKHF_INJECTED) == 0 && (down || up))
                {
                    // Only learn special keys, so a stray press can't hijack a normal key like Space
                    if (_learning && down && IsSpecialKey(data.vkCode))
                    {
                        _learning = false;
                        ScanCode = (int)data.scanCode;
                        Extended = extended;
                        Learned?.Invoke(ScanCode, Extended);
                        return (IntPtr)1;
                    }

                    if (data.scanCode == ScanCode && extended == Extended)
                    {
                        // Holding the key auto-repeats; act once per press
                        if (down && !_held) Pressed?.Invoke();
                        _held = down;
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        // Unmapped keys (vk 0 / 0xFF), F13-F24, and browser/media/launch/OEM keys
        internal static bool IsSpecialKey(uint vk) =>
            vk == 0 || vk == 0xFF || (vk >= 0x7C && vk <= 0x87) || (vk >= 0xA6 && vk <= 0xB7) || vk >= 0xE0;

        public static string Describe(int scanCode, bool extended) =>
            $"{(extended ? "E0 " : "")}{scanCode:X2}";

        public void Dispose() => Stop();
    }
}
