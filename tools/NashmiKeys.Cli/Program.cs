using System.Text;
using NashmiKeys.Core.Diagnostics;
using NashmiKeys.Core.Sdl;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // Console encoding is cosmetic.
}

var command = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : string.Empty;

switch (command)
{
    case "selftest":
        return SelfTestSuite.Run(Console.Out);

    case "diagnose":
    {
        var sdlPath = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        Console.WriteLine("NashmiKeys diagnostics");
        Console.WriteLine("SDL3 runtime: " + (File.Exists(sdlPath) ? sdlPath : "NOT FOUND next to the executable"));
        Console.WriteLine();
        return SdlDiagnostics.Run(Console.Out);
    }

    default:
        Console.WriteLine("NashmiKeys CLI");
        Console.WriteLine("  selftest   Run the built-in test suite (exit code 0 = pass)");
        Console.WriteLine("  diagnose   Initialize SDL, print its version, list connected gamepads");
        return 1;
}
