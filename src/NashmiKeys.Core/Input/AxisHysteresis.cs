namespace NashmiKeys.Core.Input;

/// <summary>
/// Threshold state machine with separate activation and release points so a
/// stick resting near a threshold cannot chatter.
/// </summary>
public sealed class AxisHysteresis
{
    private readonly double _activateAt;
    private readonly double _releaseBelow;
    private bool _active;

    public AxisHysteresis(double activateAt, double releaseBelow)
    {
        if (activateAt <= releaseBelow)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseBelow), "Release point must be below activation point.");
        }

        _activateAt = activateAt;
        _releaseBelow = releaseBelow;
    }

    public bool IsActive => _active;

    public bool Update(double value)
    {
        var magnitude = Math.Abs(value);
        if (_active)
        {
            if (magnitude < _releaseBelow)
            {
                _active = false;
            }
        }
        else if (magnitude >= _activateAt)
        {
            _active = true;
        }

        return _active;
    }

    public void Reset()
    {
        _active = false;
    }
}
