namespace FreiAtlas.Core.Recovery;

public enum RecoveryThresholdMode { Percentage, Fixed }

public sealed record RecoveryRule
{
    public bool Enabled { get; init; }
    public RecoveryThresholdMode Mode { get; init; }
    public int Percentage { get; init; } = 60;
    public int FixedValue { get; init; } = 500;
    public string Key { get; init; } = "1";
    public int IntervalMilliseconds { get; init; } = 3000;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Percentage is < 1 or > 100
            || FixedValue is < 1 or > 10_000_000
            || IntervalMilliseconds is < 250 or > 60_000
            || !RecoveryKey.TryParse(Key, out _))
            throw new ArgumentException("恢复设置无效：请检查阈值、按键和触发间隔。");
    }
}

public sealed record QuickAssistSettings
{
    public RecoveryRule Health { get; init; } = new();
    public RecoveryRule Mana { get; init; } = new() { Percentage = 40, Key = "2" };
    public FreiAtlas.Core.PortalSqueeze.PortalSqueezeSettings PortalSqueeze { get; init; } = new();
    public static QuickAssistSettings Default => new();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Health);
        ArgumentNullException.ThrowIfNull(Mana);
        ArgumentNullException.ThrowIfNull(PortalSqueeze);
        Health.Validate();
        Mana.Validate();
        PortalSqueeze.Validate();
    }
}

public sealed record RecoveryKey(string Gesture, ushort VirtualKey, IReadOnlyList<ushort> Modifiers)
{
    public static bool TryParse(string? gesture, out RecoveryKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4) return false;
        var modifiers = new List<ushort>();
        foreach (var part in parts[..^1])
        {
            var value = part switch { "Ctrl" => (ushort)0x11, "Alt" => (ushort)0x12, "Shift" => (ushort)0x10, _ => (ushort)0 };
            if (value == 0 || modifiers.Contains(value)) return false;
            modifiers.Add(value);
        }
        var name = parts[^1];
        ushort code = 0;
        if (name.Length == 1 && (name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) code = name[0];
        else if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var number) && number is >= 1 and <= 12) code = (ushort)(0x6F + number);
        else if (name.Length == 7 && name.StartsWith("Numpad", StringComparison.Ordinal) && name[6] is >= '0' and <= '9') code = (ushort)(0x60 + name[6] - '0');
        else if (name == "Space") code = 0x20;
        if (code == 0) return false;
        key = new RecoveryKey(gesture, code, modifiers.AsReadOnly());
        return true;
    }
}
