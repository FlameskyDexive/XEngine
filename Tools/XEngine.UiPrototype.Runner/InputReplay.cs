namespace XEngine.UiPrototype;

internal static class InputReplay
{
    // All events target widgets by coordinates, keys and characters, never model commands.
    public static PrototypeInput At(int tick, float width, float height)
    {
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
        return new(-1, -1);
    }
}
