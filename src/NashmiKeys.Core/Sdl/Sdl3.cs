using System.Runtime.InteropServices;

namespace NashmiKeys.Core.Sdl;

/// <summary>
/// SDL 3.4.20 P/Invoke surface, verified against the 3.4.20 headers.
/// Notes:
///  - SDL_JoystickID is Uint32.
///  - SDL_GetVersion returns an int in 3.4.x (SDL_VERSIONNUM encoding).
///  - C bool is one byte, so byte is used for bool returns.
///  - SDL_GetGamepads returns an SDL-allocated array freed with SDL_free.
/// </summary>
internal static class Sdl3
{
    public const uint InitGamepad = 0x00002000;

    public const int GamepadButtonSouth = 0;
    public const int GamepadButtonEast = 1;
    public const int GamepadButtonWest = 2;
    public const int GamepadButtonNorth = 3;
    public const int GamepadButtonBack = 4;
    public const int GamepadButtonGuide = 5;
    public const int GamepadButtonStart = 6;
    public const int GamepadButtonLeftStick = 7;
    public const int GamepadButtonRightStick = 8;
    public const int GamepadButtonLeftShoulder = 9;
    public const int GamepadButtonRightShoulder = 10;
    public const int GamepadButtonDpadUp = 11;
    public const int GamepadButtonDpadDown = 12;
    public const int GamepadButtonDpadLeft = 13;
    public const int GamepadButtonDpadRight = 14;
    public const int GamepadButtonMisc1 = 15;
    public const int GamepadButtonRightPaddle1 = 16;
    public const int GamepadButtonLeftPaddle1 = 17;
    public const int GamepadButtonRightPaddle2 = 18;
    public const int GamepadButtonLeftPaddle2 = 19;
    public const int GamepadButtonTouchpad = 20;
    public const int GamepadButtonCount = 26;

    public const int GamepadAxisLeftX = 0;
    public const int GamepadAxisLeftY = 1;
    public const int GamepadAxisRightX = 2;
    public const int GamepadAxisRightY = 3;
    public const int GamepadAxisLeftTrigger = 4;
    public const int GamepadAxisRightTrigger = 5;

    public const int PowerStateError = -1;
    public const int PowerStateUnknown = 0;
    public const int PowerStateOnBattery = 1;
    public const int PowerStateNoBattery = 2;
    public const int PowerStateCharging = 3;
    public const int PowerStateCharged = 4;

    public const string HintJoystickAllowBackgroundEvents = "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS";

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int SDL_GetVersion();

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr SDL_GetRevision();

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern byte SDL_Init(uint flags);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void SDL_Quit();

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern byte SDL_SetHint(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr SDL_GetGamepads(out int count);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr SDL_OpenGamepad(uint instanceId);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void SDL_CloseGamepad(IntPtr gamepad);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr SDL_GetGamepadNameForID(uint instanceId);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int SDL_GetGamepadType(IntPtr gamepad);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern byte SDL_GetGamepadButton(IntPtr gamepad, int button);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern byte SDL_GamepadHasButton(IntPtr gamepad, int button);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern byte SDL_GamepadHasAxis(IntPtr gamepad, int axis);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int SDL_GetGamepadPowerInfo(IntPtr gamepad, out int percent);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void SDL_UpdateGamepads();

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void SDL_SetGamepadEventsEnabled(byte enabled);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void SDL_SetJoystickEventsEnabled(byte enabled);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void SDL_free(IntPtr memory);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr SDL_GetError();

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern byte SDL_ClearError();

    public static string GetErrorString()
    {
        var pointer = SDL_GetError();
        return pointer == IntPtr.Zero ? "unknown SDL error" : Marshal.PtrToStringUTF8(pointer) ?? "unknown SDL error";
    }

    public static string GetName(IntPtr gamepad)
    {
        var pointer = SDL_GetGamepadName(gamepad);
        return pointer == IntPtr.Zero ? "Unknown controller" : Marshal.PtrToStringUTF8(pointer) ?? "Unknown controller";
    }

    public static string GetNameForId(uint instanceId)
    {
        var pointer = SDL_GetGamepadNameForID(instanceId);
        return pointer == IntPtr.Zero ? "Unknown controller" : Marshal.PtrToStringUTF8(pointer) ?? "Unknown controller";
    }
}
