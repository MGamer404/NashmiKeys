using NashmiKeys.Core.Input;

namespace NashmiKeys.Core.Sdl;

public sealed record PadInfo(
    uint InstanceId,
    string Name,
    PadFamily Family,
    string SdlTypeName,
    int? BatteryPercent,
    string PowerState);

public sealed record ControllerSnapshot(
    long TimestampMs,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<PadInfo> Pads,
    int DetectedCount,
    string SdlVersion,
    string? Error)
{
    public int ConnectedCount => Pads.Count;

    public PadFamily Family => Pads.Count > 0 ? Pads[0].Family : InputCatalog.DefaultFamily;

    public int? BatteryPercent => Pads.Select(pad => pad.BatteryPercent).FirstOrDefault(percent => percent.HasValue);

    public static ControllerSnapshot Empty(string sdlVersion, string? error = null)
    {
        return new ControllerSnapshot(
            Environment.TickCount64,
            Array.Empty<string>(),
            Array.Empty<PadInfo>(),
            0,
            sdlVersion,
            error);
    }
}
