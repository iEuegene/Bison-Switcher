namespace Kright.Services;

/// <summary>How a wheel "click" turns into motion. Pure maths, no Win32, so it
/// can be unit-tested. Units are wheel-delta units (120 = one click).</summary>
public readonly record struct ScrollFeel(double StepFactor, double DurationMs, double Acceleration)
{
    public static readonly ScrollFeel Soft = new(0.85, 500, 1.5);
    public static readonly ScrollFeel Standard = new(1.0, 360, 2.0);
    public static readonly ScrollFeel Fast = new(1.25, 220, 2.5);

    /// <summary>0 = soft, 1 = standard, 2 = fast, anything else = the user's own values.</summary>
    public static ScrollFeel ForPreset(int preset, ScrollFeel custom) => preset switch
    {
        0 => Soft,
        1 => Standard,
        2 => Fast,
        _ => custom,
    };
}

/// <summary>Smooth mode: every click adds distance to a target, and the page
/// moves towards it on an exponential ease-out. Clicks in quick succession
/// (a flick, or a free-spinning wheel) get the acceleration multiplier.</summary>
public sealed class ScrollPhysics
{
    /// <summary>Clicks closer than this count as one fast series.</summary>
    public const double StreakGapMs = 80;
    /// <summary>Leftover distance below this is sent at once, so motion ends cleanly.</summary>
    private const double Snap = 2;

    private double _pending;     // distance still to travel, signed
    private double _carry;       // fraction of a unit not yet sent
    private double _lastClickMs = double.NegativeInfinity;
    private int _streak;

    public bool Active => _pending != 0;

    /// <summary>A wheel click arrived. <paramref name="delta"/> is the raw value (±120·n).</summary>
    public void AddClick(int delta, double nowMs, ScrollFeel feel)
    {
        int dir = Math.Sign(delta);
        if (dir == 0) return;

        // Reversing kills what is left: the page should obey the new direction now.
        if (_pending != 0 && Math.Sign(_pending) != dir)
        {
            _pending = 0;
            _carry = 0;
            _streak = 0;
        }

        _streak = nowMs - _lastClickMs < StreakGapMs ? Math.Min(_streak + 1, 4) : 0;
        _lastClickMs = nowMs;

        double accel = 1 + (Math.Max(feel.Acceleration, 1) - 1) * _streak / 4.0;
        _pending += delta * feel.StepFactor * accel;
    }

    /// <summary>Advance by <paramref name="dtMs"/>; returns whole units to send now.</summary>
    public int Step(double dtMs, ScrollFeel feel)
    {
        if (_pending == 0) return 0;
        double tau = Math.Max(feel.DurationMs, 40) / 4.0; // ~98% of the way after DurationMs
        double move = Math.Abs(_pending) <= Snap
            ? _pending
            : _pending * (1 - Math.Exp(-Math.Max(dtMs, 0) / tau));
        _pending -= move;
        if (Math.Abs(_pending) < 1e-9) _pending = 0;

        _carry += move;
        int whole = (int)Math.Truncate(_carry);
        _carry -= whole;
        if (_pending == 0)
        {
            // Last frame: round the leftover fraction so totals add up.
            int rest = (int)Math.Round(_carry);
            whole += rest;
            _carry = 0;
        }
        return whole;
    }

    public void Cancel()
    {
        _pending = 0;
        _carry = 0;
        _streak = 0;
    }
}
