using System.Numerics;
using FreiAtlas.Core.Pickup;

namespace FreiAtlas.Core.Tests;

public sealed class PickupTargetSelectorTests
{
    private static readonly Vector2 Player = new(10, 20);

    [Fact]
    public void Select_PicksNearestWhitelistedItemWithinPlayerPickupRange()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["Perfect Jeweller's Orb"],
            PickupRangeGrid = 3f
        };
        var farther = Candidate(2, "Perfect Jeweller's Orb", "CurrencyRerollSocketNumbers03", new(12, 20));
        var nearer = Candidate(1, "Perfect Jeweller's Orb", "CurrencyRerollSocketNumbers03", new(11, 20));
        var outside = Candidate(3, "Perfect Jeweller's Orb", "CurrencyRerollSocketNumbers03", new(14, 20));

        var result = PickupTargetSelector.Select(settings, Player, [farther, outside, nearer]);

        Assert.True(result.ShouldPickup);
        Assert.Equal(PickupDecisionReason.Matched, result.Reason);
        Assert.Same(nearer, result.Target);
    }

    [Fact]
    public void Select_MatchesStableArtNameIgnoringCaseAndWhitespace()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["  currencyaddmodtorare  "],
            PickupRangeGrid = 2f
        };
        var item = Candidate(7, "崇高石", "CurrencyAddModToRare", new(11, 20));

        var result = PickupTargetSelector.Select(settings, Player, [item]);

        Assert.True(result.ShouldPickup);
        Assert.Equal(7u, result.Target!.EntityId);
    }

    [Fact]
    public void Select_MatchesDisplayNameWithStackCountAndMetadataSuffix()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["完美工匠石"],
            PickupRangeGrid = 20f
        };
        var item = Candidate(16, "3x 完美工匠石[223e]", "CurrencyRerollSocketNumbers03", new(18, 20));

        var result = PickupTargetSelector.Select(settings, Player, [item]);

        Assert.True(result.ShouldPickup);
        Assert.Equal(16u, result.Target!.EntityId);
    }

    [Fact]
    public void Select_IgnoresZeroWidthFormattingInWhitelist()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石\u200B"],
            PickupRangeGrid = 20f
        };
        var item = Candidate(13, "9x 崇高石", "CurrencyAddModToRare", new(19, 20));

        var result = PickupTargetSelector.Select(settings, Player, [item]);

        Assert.True(result.ShouldPickup);
        Assert.Equal(13u, result.Target!.EntityId);
    }

    [Fact]
    public void Select_DoesNotMoveOrSelectItemOutsidePickupRange()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["CurrencyAddModToRare"],
            PickupRangeGrid = 2f
        };
        var item = Candidate(1, "崇高石", "CurrencyAddModToRare", new(13, 20));

        var result = PickupTargetSelector.Select(settings, Player, [item]);

        Assert.False(result.ShouldPickup);
        Assert.Null(result.Target);
        Assert.Equal(PickupDecisionReason.NoEligibleItem, result.Reason);
    }

    [Fact]
    public void Select_FailsClosedWhenDisabledOrWhitelistEmpty()
    {
        var item = Candidate(1, "崇高石", "CurrencyAddModToRare", new(10, 20));
        var disabled = PickupTargetSelector.Select(
            new PickupSettings { Whitelist = ["崇高石"] }, Player, [item]);
        var empty = PickupTargetSelector.Select(
            new PickupSettings { Enabled = true, Whitelist = [] }, Player, [item]);

        Assert.Equal(PickupDecisionReason.Disabled, disabled.Reason);
        Assert.Null(disabled.Target);
        Assert.Equal(PickupDecisionReason.EmptyWhitelist, empty.Reason);
        Assert.Null(empty.Target);
    }

    [Fact]
    public void Select_FailsClosedWithoutFinitePlayerPosition()
    {
        var settings = new PickupSettings { Enabled = true, Whitelist = ["崇高石"] };
        var item = Candidate(1, "崇高石", "CurrencyAddModToRare", Player);

        var missing = PickupTargetSelector.Select(settings, null, [item]);
        var invalid = PickupTargetSelector.Select(settings, new Vector2(float.NaN, 0), [item]);

        Assert.Equal(PickupDecisionReason.MissingPlayerPosition, missing.Reason);
        Assert.Equal(PickupDecisionReason.MissingPlayerPosition, invalid.Reason);
        Assert.Null(missing.Target);
        Assert.Null(invalid.Target);
    }

    [Fact]
    public void SettingsValidation_RejectsInvalidPickupRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PickupSettings { PickupRangeGrid = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PickupSettings { PickupRangeGrid = float.NaN }.Validate());
    }

    [Fact]
    public void Select_LooseMatch_SingleKeywordMatchesMultipleVariants()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高"],
            PickupRangeGrid = 10f
        };

        var perfect = Candidate(1, "完美崇高石", "CurrencyAddModToRare3", new(12, 20));
        var high = Candidate(2, "高级崇高石", "CurrencyAddModToRare2", new(13, 20));
        var normal = Candidate(3, "崇高石", "CurrencyAddModToRare", new(14, 20));

        Assert.True(settings.IsWhitelisted(perfect.ItemName, perfect.ArtName));
        Assert.True(settings.IsWhitelisted(high.ItemName, high.ArtName));
        Assert.True(settings.IsWhitelisted(normal.ItemName, normal.ArtName));

        var result = PickupTargetSelector.Select(settings, Player, [perfect, high, normal]);
        Assert.True(result.ShouldPickup);
        Assert.Equal(1u, result.Target!.EntityId);
    }

    [Fact]
    public void Select_LooseMatch_BroadKeywordMatchesMultipleCurrencies()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["石"],
            PickupRangeGrid = 10f
        };

        Assert.True(settings.IsWhitelisted("神圣石", "CurrencyDivine"));
        Assert.True(settings.IsWhitelisted("混沌石", "CurrencyChaos"));
        Assert.True(settings.IsWhitelisted("崇高石", "CurrencyExalt"));
        Assert.True(settings.IsWhitelisted("工匠石", "CurrencyJeweller"));
        Assert.False(settings.IsWhitelisted("卡兰德魔镜", "CurrencyMirror"));
    }

    [Fact]
    public void Select_Whitelist_ParsesCommaSeparatedEntries()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高, 神圣，混沌\r\n工匠石"],
            PickupRangeGrid = 20f
        };

        Assert.Equal(["崇高", "神圣", "混沌", "工匠石"], settings.Whitelist);
        Assert.True(settings.IsWhitelisted("神圣石"));
        Assert.True(settings.IsWhitelisted("混沌石"));
    }

    [Fact]
    public void Select_PickupRange_AllowsUpToSixtyGrids()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 60f
        };
        settings.Validate();

        var inRange = Candidate(10, "崇高石", "CurrencyExalt", new(60, 20)); // 距离 50 格
        var outRange = Candidate(11, "崇高石", "CurrencyExalt", new(75, 20)); // 距离 65 格

        var result = PickupTargetSelector.Select(settings, Player, [inRange, outRange]);
        Assert.True(result.ShouldPickup);
        Assert.Equal(10u, result.Target!.EntityId);
    }

    [Fact]
    public void Select_LooseMatch_DoesNotFalselyMatchShorterItemWhenKeywordIsLonger()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["完美崇高石"],
            PickupRangeGrid = 10f
        };

        var normal = Candidate(1, "崇高石", "CurrencyExalt", new(12, 20));
        var perfect = Candidate(2, "完美崇高石", "CurrencyExaltPerfect", new(13, 20));

        Assert.False(settings.IsWhitelisted(normal.ItemName, normal.ArtName));
        Assert.True(settings.IsWhitelisted(perfect.ItemName, perfect.ArtName));

        var result = PickupTargetSelector.Select(settings, Player, [normal, perfect]);
        Assert.True(result.ShouldPickup);
        Assert.Equal(2u, result.Target!.EntityId);
    }

    private static PickupItemCandidate Candidate(
        uint id,
        string name,
        string art,
        Vector2 position)
        => new(id, name, art, position, Vector2.Distance(Player, position));
}
