using System.ComponentModel;
using System.Runtime.InteropServices;
using NashmiKeys.Core.Input;
using NashmiKeys.Core.Logging;

namespace NashmiKeys.Core.Injection;

/// <summary>
/// Win32 SendInput implementation. Entries carry BOTH the virtual key and the
/// scan code so games that read raw input still see the synthetic key.
/// </summary>
public sealed class SendInputInjector : IKeyInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToVscEx = 4;

    private readonly ILogger _logger;
    private readonly IntPtr _keyboardLayout;

    public SendInputInjector(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _keyboardLayout = GetKeyboardLayout(0);

        if (Marshal.SizeOf<Input>() != (IntPtr.Size == 8 ? 40 : 28))
        {
            _logger.Warn($"Unexpected INPUT structure size: {Marshal.SizeOf<Input>()}.");
        }
    }

    public void KeyDown(KeyDefinition key)
    {
        Send(key, keyUp: false);
    }

    public void KeyUp(KeyDefinition key)
    {
        Send(key, keyUp: true);
    }

    private void Send(KeyDefinition key, bool keyUp)
    {
        var scanCode = key.ScanCode;
        var extended = key.Extended;

        // MapVirtualKeyEx is used to confirm/derive the Set 1 scan code. The
        // catalog value wins when the mapping cannot express the distinction
        // (notably Numpad Enter, which shares VK_RETURN with Enter).
        var mapped = MapVirtualKeyExW(key.VirtualKey, MapVkToVscEx, _keyboardLayout);
        if (mapped != 0)
        {
            var mappedScan = (ushort)(mapped & 0xFF);
            if (scanCode == 0 || mappedScan == scanCode)
            {
                scanCode = mappedScan;
                if (((mapped >> 8) & 0xFF) == 0xE0)
                {
                    extended = true;
                }
            }
        }

        uint flags = 0;
        if (extended)
        {
            flags |= KeyEventExtendedKey;
        }

        if (keyUp)
        {
            flags |= KeyEventKeyUp;
        }

        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key.VirtualKey,
                    ScanCode = scanCode,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };

        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.Error($"SendInput failed for {key.Name} (keyUp={keyUp}), win32 error {error}.");
            throw new Win32Exception(error, $"SendInput failed for {key.Name}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyExW(uint code, uint mapType, IntPtr keyboardLayout);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);
}
