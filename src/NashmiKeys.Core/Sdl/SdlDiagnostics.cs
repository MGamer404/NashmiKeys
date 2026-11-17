using System.Runtime.InteropServices;

namespace NashmiKeys.Core.Sdl;

public static class SdlDiagnostics
{
    public static string GetVersionString()
    {
        return FormatVersion(Sdl3.SDL_GetVersion());
    }

    public static string FormatVersion(int version)
    {
        var major = version / 1_000_000;
        var minor = version / 1_000 % 1_000;
        var micro = version % 1_000;
        return $"{major}.{minor}.{micro}";
    }

    /// <summary>Prints SDL version and every detected gamepad. Returns a process exit code.</summary>
    public static int Run(TextWriter output)
    {
        var initialized = false;
        try
        {
            output.WriteLine("SDL version: " + GetVersionString());
            var revisionPointer = Sdl3.SDL_GetRevision();
            var revision = revisionPointer == IntPtr.Zero
                ? "(unknown)"
                : Marshal.PtrToStringUTF8(revisionPointer) ?? "(unknown)";
            output.WriteLine("SDL revision: " + revision);

            var hintResult = Sdl3.SDL_SetHint(Sdl3.HintJoystickAllowBackgroundEvents, "1");
            output.WriteLine($"SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS: {(hintResult != 0 ? "set" : "not set")}");

            if (Sdl3.SDL_Init(Sdl3.InitGamepad) == 0)
            {
                output.WriteLine("SDL_Init failed: " + Sdl3.GetErrorString());
                return 1;
            }

            initialized = true;
            Sdl3.SDL_UpdateGamepads();

            var pointer = Sdl3.SDL_GetGamepads(out var count);
            try
            {
                output.WriteLine($"Detected gamepads: {count}");
                if (count == 0)
                {
                    output.WriteLine("No gamepads detected.");
                    return 0;
                }

                for (var index = 0; index < count; index++)
                {
                    var id = (uint)Marshal.ReadInt32(pointer, index * sizeof(uint));
                    output.WriteLine($"  [{index + 1}] instance id {id}: {Sdl3.GetNameForId(id)}");
                    var gamepad = Sdl3.SDL_OpenGamepad(id);
                    if (gamepad == IntPtr.Zero)
                    {
                        output.WriteLine("      open failed: " + Sdl3.GetErrorString());
                        continue;
                    }

                    try
                    {
                        var type = Sdl3.SDL_GetGamepadType(gamepad);
                        var buttonCount = 0;
                        for (var button = 0; button < Sdl3.GamepadButtonCount; button++)
                        {
                            if (Sdl3.SDL_GamepadHasButton(gamepad, button) != 0)
                            {
                                buttonCount++;
                            }
                        }

                        var axisCount = 0;
                        for (var axis = 0; axis < 6; axis++)
                        {
                            if (Sdl3.SDL_GamepadHasAxis(gamepad, axis) != 0)
                            {
                                axisCount++;
                            }
                        }

                        var power = Sdl3.SDL_GetGamepadPowerInfo(gamepad, out var percent);
                        output.WriteLine($"      name: {Sdl3.GetName(gamepad)}");
                        output.WriteLine($"      type: {PadFamilyMapper.TypeName(type)} ({PadFamilyMapper.FromSdlType(type)})");
                        output.WriteLine($"      buttons: {buttonCount}/{Sdl3.GamepadButtonCount}, axes: {axisCount}/6");
                        output.WriteLine($"      power: {PadFamilyMapper.PowerStateName(power)}, battery: {(percent >= 0 ? percent + "%" : "n/a")}");
                    }
                    finally
                    {
                        Sdl3.SDL_CloseGamepad(gamepad);
                    }
                }

                return 0;
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    Sdl3.SDL_free(pointer);
                }
            }
        }
        catch (DllNotFoundException)
        {
            output.WriteLine("SDL3.dll was not found. Place it next to the executable.");
            return 1;
        }
        catch (BadImageFormatException)
        {
            output.WriteLine("SDL3.dll has the wrong architecture (need x64).");
            return 1;
        }
        catch (Exception exception)
        {
            output.WriteLine("SDL diagnostics failed: " + exception.Message);
            return 1;
        }
        finally
        {
            if (initialized)
            {
                Sdl3.SDL_Quit();
            }
        }
    }
}
