using NashmiKeys.Core.Input;

namespace NashmiKeys.Core.Sdl;

public static class PadFamilyMapper
{
    // SDL_GamepadType values from SDL_gamepad.h (3.4.20).
    public static PadFamily FromSdlType(int type)
    {
        return type switch
        {
            4 or 5 or 6 => PadFamily.PlayStation,          // PS3, PS4, PS5
            7 or 8 or 9 or 10 or 11 => PadFamily.Nintendo, // Switch Pro / Joy-Cons / GameCube
            _ => PadFamily.Xbox                             // Xbox 360/One, Steam, standard, unknown
        };
    }

    public static string TypeName(int type)
    {
        return type switch
        {
            0 => "Unknown",
            1 => "Standard",
            2 => "Xbox 360",
            3 => "Xbox One",
            4 => "PlayStation 3",
            5 => "PlayStation 4",
            6 => "PlayStation 5",
            7 => "Nintendo Switch Pro",
            8 => "Nintendo Switch Joy-Con Left",
            9 => "Nintendo Switch Joy-Con Right",
            10 => "Nintendo Switch Joy-Con Pair",
            11 => "Nintendo GameCube",
            12 => "Steam",
            _ => $"Type {type}"
        };
    }

    public static string PowerStateName(int state)
    {
        return state switch
        {
            -1 => "Error",
            0 => "Unknown",
            1 => "On battery",
            2 => "No battery",
            3 => "Charging",
            4 => "Charged",
            _ => $"State {state}"
        };
    }
}
