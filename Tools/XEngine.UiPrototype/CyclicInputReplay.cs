namespace XEngine.UiPrototype;

/// <summary>Three-second input-only cycle. The second half restores property text,
/// scrolling, splitter and branch expansion through widgets, without resetting models.</summary>
public static class CyclicInputReplay
{
    public const int Period = 180;
    public static PrototypeInput At(int tick, float width, float height)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        tick %= Period;
        float left = (width - 12) * .5f, inputY = (height - 88) * .6f + 54;
        if (tick is >= 10 and <= 13) return new(8, 46, tick == 11);
        if (tick is >= 16 and <= 19) return new(60, 90, tick == 17);
        if (tick == 22) return new(60, 90, Wheel: -2);
        if (tick is >= 26 and <= 29) return new(left + 60, 46, tick == 27);
        if (tick is >= 30 and <= 37) return new(width - 40, inputY, tick == 31,
            Keys: tick == 34 ? PrototypeKeys.Control | PrototypeKeys.A : PrototypeKeys.None,
            Text: tick == 36 ? "Edited property" : "");
        if (tick is >= 40 and <= 43) return new(60, 134, tick == 41);
        if (tick is >= 44 and <= 53) return new(width - 40, inputY, tick == 45,
            Keys: tick == 48 ? PrototypeKeys.Control | PrototypeKeys.A : tick == 51 ? PrototypeKeys.Backspace : PrototypeKeys.None,
            Text: tick == 50 ? "Typed input" : tick == 53 ? "t" : "");
        if (tick == 56) return new(left + 60, 90, Wheel: -3);
        if (tick is >= 60 and <= 65) return new(left + 8 + (tick >= 63 ? 80 : 0), 60, tick is >= 61 and <= 63);
        if (tick == 96) return new(60, 90, Wheel: 2);
        if (tick == 98) return new(left + 160, 90, Wheel: 3);
        if (tick is >= 100 and <= 103) return new(left + 8 + (tick < 102 ? 80 : 0), 60, tick is 101 or 102);
        if (tick is >= 108 and <= 111) return new(8, 46, tick == 109);
        if (tick is >= 116 and <= 119) return new(left + 60, 46, tick == 117);
        if (tick is >= 120 and <= 127) return new(width - 40, inputY, tick == 121,
            Keys: tick == 124 ? PrototypeKeys.Control | PrototypeKeys.A : PrototypeKeys.None,
            Text: tick == 126 ? "Property 0000: 0" : "");
        if (tick is >= 132 and <= 135) return new(60, 68, tick == 133);
        if (tick is >= 140 and <= 147) return new(width - 40, inputY, tick == 141,
            Keys: tick == 144 ? PrototypeKeys.Control | PrototypeKeys.A : PrototypeKeys.None,
            Text: tick == 146 ? "N1 rendering smoke" : "");
        if (tick is >= 152 and <= 155) return new(left + 8, 60, tick == 153);
        return new(-1, -1);
    }
}

/// <summary>Paces input ticks at 60 Hz. Never repeats wheel/text pulses between ticks
/// or skips pressed/released states after a delayed frame. Backlog is reported.</summary>
public sealed class TimedInputReplay
{
    private double _lastSeconds = -1;
    private PrototypeInput _last = new(-1, -1);
    public int CurrentTick { get; private set; } = -1;
    public int MaximumBacklogTicks { get; private set; }
    public bool Advanced { get; private set; }
    public PrototypeInput Advance(double seconds, float width, float height)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds < _lastSeconds || seconds * 60 >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        float delta = _lastSeconds < 0 ? 1f / 60 : (float)Math.Max(.000001, seconds - _lastSeconds);
        _lastSeconds = seconds;
        int target = (int)Math.Floor(seconds * 60);
        Advanced = CurrentTick < target;
        if (Advanced)
        {
            MaximumBacklogTicks = Math.Max(MaximumBacklogTicks, target - CurrentTick - 1);
            CurrentTick++;
            _last = CyclicInputReplay.At(CurrentTick, width, height);
            return _last with { DeltaSeconds = delta };
        }
        return _last with { Wheel = 0, Text = "", DeltaSeconds = delta };
    }
}
