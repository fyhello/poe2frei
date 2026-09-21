namespace FreiAtlas.App.Settings;

[Flags]
public enum AtlasHotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4
}

public sealed record AtlasHotkey
{
    private AtlasHotkey(
        string gesture,
        AtlasHotkeyModifiers modifiers,
        string key)
    {
        Gesture = gesture;
        Modifiers = modifiers;
        Key = key;
    }

    public static AtlasHotkey Default { get; } = Parse("F12");

    public string Gesture { get; }

    public AtlasHotkeyModifiers Modifiers { get; }

    public string Key { get; }

    public static AtlasHotkey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('+');
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The hotkey gesture is invalid.", nameof(value));
        }

        var modifiers = AtlasHotkeyModifiers.None;
        foreach (var rawModifier in parts[..^1])
        {
            var modifier = rawModifier.Trim().ToUpperInvariant() switch
            {
                "CTRL" => AtlasHotkeyModifiers.Control,
                "ALT" => AtlasHotkeyModifiers.Alt,
                "SHIFT" => AtlasHotkeyModifiers.Shift,
                _ => throw new ArgumentException(
                    "The hotkey modifier is invalid.",
                    nameof(value))
            };
            if ((modifiers & modifier) != 0)
            {
                throw new ArgumentException(
                    "The hotkey modifier is duplicated.",
                    nameof(value));
            }

            modifiers |= modifier;
        }

        var key = parts[^1].Trim().ToUpperInvariant();
        if (TryParseFunctionKey(key, out var functionKey))
        {
            if (modifiers != AtlasHotkeyModifiers.None)
            {
                throw new ArgumentException(
                    "Function keys cannot use modifiers.",
                    nameof(value));
            }

            return new AtlasHotkey(functionKey, modifiers, functionKey);
        }

        if (key.Length != 1
            || !((key[0] >= 'A' && key[0] <= 'Z')
                 || (key[0] >= '0' && key[0] <= '9'))
            || modifiers == AtlasHotkeyModifiers.None)
        {
            throw new ArgumentException("The hotkey key is invalid.", nameof(value));
        }

        var canonical = string.Join(
            '+',
            CanonicalModifierNames(modifiers).Append(key));
        return new AtlasHotkey(canonical, modifiers, key);
    }

    public static bool TryParse(string? value, out AtlasHotkey? hotkey)
    {
        try
        {
            hotkey = Parse(value!);
            return true;
        }
        catch (ArgumentException)
        {
            hotkey = null;
            return false;
        }
    }

    private static bool TryParseFunctionKey(
        string key,
        out string canonical)
    {
        canonical = string.Empty;
        if (key.Length < 2
            || key[0] != 'F'
            || !int.TryParse(key[1..], out var number)
            || number is < 1 or > 12)
        {
            return false;
        }

        canonical = $"F{number}";
        return true;
    }

    private static IEnumerable<string> CanonicalModifierNames(
        AtlasHotkeyModifiers modifiers)
    {
        if ((modifiers & AtlasHotkeyModifiers.Control) != 0)
        {
            yield return "Ctrl";
        }

        if ((modifiers & AtlasHotkeyModifiers.Alt) != 0)
        {
            yield return "Alt";
        }

        if ((modifiers & AtlasHotkeyModifiers.Shift) != 0)
        {
            yield return "Shift";
        }
    }
}
