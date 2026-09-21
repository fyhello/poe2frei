using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasNavigationCatalogTests
{
    [Fact]
    public void Build_IgnoresBlankNamesAndGroupsTrimmedNamesIgnoringCase()
    {
        var catalog = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(5, 2), "  Dunes "),
            Node(new AtlasGridPos(1, 9), "dUNES"),
            Node(new AtlasGridPos(2, 0), "Mesa"),
            Node(new AtlasGridPos(3, 0), "  "),
            Node(new AtlasGridPos(4, 0), null)
        ]);

        Assert.Collection(
            catalog.Entries,
            entry => Assert.Equal(
                new AtlasNavigationCatalogEntry("dUNES", 2, 2),
                entry),
            entry => Assert.Equal(
                new AtlasNavigationCatalogEntry("Mesa", 1, 1),
                entry));
    }

    [Fact]
    public void Build_CountsOnlyIncompleteInstancesSeparately()
    {
        var catalog = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(0, 0), "Dunes", completed: true),
            Node(new AtlasGridPos(1, 0), "dunes"),
            Node(new AtlasGridPos(2, 0), "Mesa", completed: true)
        ]);

        Assert.Collection(
            catalog.Entries,
            entry => Assert.Equal(
                new AtlasNavigationCatalogEntry("Dunes", 2, 1),
                entry),
            entry => Assert.Equal(
                new AtlasNavigationCatalogEntry("Mesa", 1, 0),
                entry));
    }

    [Fact]
    public void Build_UsesLowestGridAsStableDisplayNameRepresentative()
    {
        var catalog = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(1, 8), " MAP NAME "),
            Node(new AtlasGridPos(1, 3), "Map Name"),
            Node(new AtlasGridPos(0, 9), "map name")
        ]);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("map name", entry.DisplayName);
        Assert.Equal(3, entry.NodeCount);
    }

    [Fact]
    public void Build_SortsEntriesOrdinalIgnoreCaseThenOrdinal()
    {
        var catalog = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(0, 0), "zeta"),
            Node(new AtlasGridPos(1, 0), "Beta"),
            Node(new AtlasGridPos(2, 0), "alpha")
        ]);

        Assert.Equal(
            ["alpha", "Beta", "zeta"],
            catalog.Entries.Select(entry => entry.DisplayName));
    }

    [Fact]
    public void Build_InputOrderDoesNotChangeEntriesOrSignature()
    {
        var nodes = new[]
        {
            Node(new AtlasGridPos(8, 3), "Dunes"),
            Node(new AtlasGridPos(2, 5), " dunes "),
            Node(new AtlasGridPos(7, 1), "Mesa")
        };

        var forward = AtlasNavigationCatalogBuilder.Build(nodes);
        var reverse = AtlasNavigationCatalogBuilder.Build(nodes.Reverse().ToArray());

        Assert.Equal(forward.Entries, reverse.Entries);
        Assert.Equal(forward.Signature, reverse.Signature);
        Assert.Matches("^[0-9A-F]{64}$", forward.Signature);
    }

    [Fact]
    public void Build_NameOrCountChangeProducesDifferentSignature()
    {
        var baseline = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(0, 0), "Dunes")
        ]);
        var countChanged = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(0, 0), "Dunes"),
            Node(new AtlasGridPos(1, 0), "dunes")
        ]);
        var nameChanged = AtlasNavigationCatalogBuilder.Build(
        [
            Node(new AtlasGridPos(0, 0), "Mesa")
        ]);

        Assert.NotEqual(baseline.Signature, countChanged.Signature);
        Assert.NotEqual(baseline.Signature, nameChanged.Signature);
    }

    [Fact]
    public void Build_RejectsNullNodes()
    {
        Assert.Throws<ArgumentNullException>(
            () => AtlasNavigationCatalogBuilder.Build(null!));
    }

    [Fact]
    public void CatalogEntry_ExposesOnlySanitizedDirectoryFields()
    {
        var properties = typeof(AtlasNavigationCatalogEntry)
            .GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(["DisplayName", "IncompleteNodeCount", "NodeCount"], properties);
    }

    private static AtlasNodeSnapshot Node(
        AtlasGridPos grid,
        string? displayName,
        bool completed = false)
        => new(
            grid,
            null,
            displayName,
            0f,
            0f,
            true,
            true,
            completed,
            false,
            [],
            []);
}
