using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher;

public enum ColorModStyle
{
    Whole,
    NumberOnly,
    FollowGame
}

public sealed record ColorModStyleOption(ColorModStyle Value, string Name);

public sealed record ColorModSegment(bool IsColored, string Text);

public sealed record CsdDescriptionBlock(
    string[] StatIds,
    string DisplayName,
    byte[]? CurrentColor,
    string CurrentColorTag);

public sealed class ColorModEntry
{
    [JsonPropertyName("statId")]
    public string StatId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string HexColor
    {
        get => ColorToHex(Color);
        set => Color = HexToColor(value);
    }

    [JsonIgnore]
    public byte[] Color { get; set; } = new byte[3];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("style")]
    public string StyleName
    {
        get => Style.ToString();
        set => Style = Enum.TryParse<ColorModStyle>(value, true, out var s) ? s : ColorModStyle.Whole;
    }

    [JsonIgnore]
    public ColorModStyle Style { get; set; } = ColorModStyle.Whole;

    [JsonPropertyName("group")]
    public string Group { get; set; } = "地图";

    [JsonPropertyName("currentColorTag")]
    public string CurrentColorTag { get; set; } = string.Empty;

    [JsonIgnore]
    public byte[]? CurrentColor { get; set; }

    [JsonPropertyName("currentColorHex")]
    public string? CurrentColorHex => CurrentColor != null ? ColorToHex(CurrentColor) : null;

    [JsonIgnore]
    public string? ConfigWrap { get; set; }

    [JsonPropertyName("isDanger")]
    public bool IsDanger { get; set; }

    public static ColorModEntry FromRuleItem(FreiAtlas.Core.Settings.ColorModRuleItem item)
    {
        return new ColorModEntry
        {
            StatId = item.Id,
            DisplayName = item.Text,
            Group = item.Group,
            HexColor = item.Color,
            Style = Enum.TryParse<ColorModStyle>(item.Style, true, out var s) ? s : ColorModStyle.Whole,
            CurrentColorTag = item.Tag,
            IsDanger = item.IsDanger,
            Enabled = item.Enabled
        };
    }

    public static string ColorToHex(byte[]? c)
    {
        if (c == null || c.Length < 3) return "#ffffff";
        return $"#{c[0]:X2}{c[1]:X2}{c[2]:X2}";
    }

    public static byte[] HexToColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return [255, 255, 255];
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return [r, g, b];
        }
        return [255, 255, 255];
    }
}

public sealed class ColorModOverrideDoc
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("namedColors")]
    public Dictionary<string, ColorModNamedColor> NamedColors { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("simpleStats")]
    public Dictionary<string, string> SimpleStats { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("blocks")]
    public Dictionary<string, ColorModBlockPlan> Blocks { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ColorModNamedColor
{
    [JsonPropertyName("alpha")]
    public int Alpha { get; set; } = 255;

    [JsonPropertyName("rgb")]
    public List<int> Rgb { get; set; } = [];
}

public sealed class ColorModBlockPlan
{
    [JsonPropertyName("stats")]
    public List<string> Stats { get; set; } = [];

    [JsonPropertyName("slots")]
    public List<ColorModSlotPlan> Slots { get; set; } = [];
}

public sealed class ColorModSlotPlan
{
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("wrap")]
    public string? Wrap { get; set; }

    [JsonPropertyName("wrapTrad")]
    public string? WrapTrad { get; set; }

    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }

    [JsonPropertyName("tail")]
    public string? Tail { get; set; }

    [JsonPropertyName("tiers")]
    public List<ColorModTier>? Tiers { get; set; }
}

public sealed class ColorModTier
{
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("wrap")]
    public string? Wrap { get; set; }

    [JsonPropertyName("wrapTrad")]
    public string? WrapTrad { get; set; }

    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }

    [JsonPropertyName("tail")]
    public string? Tail { get; set; }
}

public sealed record ColorModPreviewHint(ColorModStyle Style, string? Wrap);
