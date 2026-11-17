using System.ComponentModel;
using System.Runtime.InteropServices;
using NashmiKeys.Core.Input;

namespace NashmiKeys.App.Helpers;

/// <summary>
/// WH_KEYBOARD_LL hook that records keys and swallows ALL keyboard events while
/// installed, so recorded shortcuts (Alt+Tab, Win, Esc...) do not act on
/// Windows. Must be installed and disposed on the UI thread.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x01;

    private readonly HookProc _callback;
    private IntPtr _hook;

    public LowLevelKeyboardHook()
    {
        _callback = HookCallback;
    }

    public event Action<KeyDefinition>? KeyCaptured;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard capture hook.");
        }
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var info = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                    var extended = (info.Flags & LlkhfExtended) != 0;
                    if (KeyCatalog.TryFromKeyboard(info.VirtualKey, info.ScanCode, extended, out var key))
                    {
                        KeyCaptured?.Invoke(key);
                    }
                }

                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Uninstall();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
