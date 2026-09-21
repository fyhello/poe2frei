using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;

namespace FreiAtlas.Game.Tests.Content;

public sealed class BossContentResolverTests
{
    [Fact]
    public void TryResolve_ConfirmsHostileUniqueWithCatalogEvidence()
    {
        var resolver = CreateResolver();
        var area = CreateArea(7);

        Assert.True(resolver.TryResolve(
            CreateContext(area, CreateBoss()),
            out var content));

        Assert.Equal("boss:7:42", content.InstanceId);
        Assert.Equal("boss", content.ContentId);
        Assert.Equal(AreaContentKind.Boss, content.Kind);
        Assert.Equal(AreaContentPhase.Available, content.Phase);
        Assert.Equal(42u, content.SourceEntityId);
        Assert.Contains(
            content.Evidence,
            evidence => evidence.Source == "Catalog"
                        && evidence.Key == "ExactMetadata");
    }

    [Fact]
    public void TryResolve_DoesNotClaimUncataloguedHostileUniqueWithoutBossArenaEvidence()
    {
        var resolver = CreateResolver();
        var entity = CreateBoss(
            metadata: "Metadata/Monsters/RogueExiles/Dex/ExileRanger2",
            currentLife: 0);

        Assert.False(resolver.TryResolve(
            CreateContext(CreateArea(7), entity),
            out _));
    }

    [Fact]
    public void TryResolve_ConfirmsNearbyBossLandmarkButDoesNotClaimADistantUnique()
    {
        var resolver = CreateResolver();
        var entity = CreateBoss(
            metadata: "Metadata/Monsters/Test/UncataloguedUnique",
            gridPosition: new Vector2(100, 100));
        var nearby = CreateLandmark(new Vector2(130, 140));
        var distant = CreateLandmark(new Vector2(131, 140));

        Assert.True(resolver.TryResolve(
            CreateContext(CreateArea(7), entity, [nearby]),
            out var confirmed));
        Assert.Equal(AreaContentKind.Boss, confirmed.Kind);

        var otherSessionResolver = CreateResolver();
        Assert.False(otherSessionResolver.TryResolve(
            CreateContext(CreateArea(7), entity, [distant]),
            out _));
    }

    [Fact]
    public void TryResolve_DoesNotClaimUncataloguedUniqueNearBossHint()
    {
        var resolver = CreateResolverWithBossTilePattern();
        var entity = CreateBoss(
            metadata: "Metadata/Monsters/Test/UncataloguedUnique",
            gridPosition: new Vector2(100, 100));
        var hint = CreateLandmark(
            new Vector2(100, 100),
            AreaLandmarkKind.BossHint);

        Assert.False(resolver.TryResolve(
            CreateContext(CreateArea(7), entity, [hint]),
            out _));
    }

    [Fact]
    public void TryResolve_PreservesLandmarkAssociationAfterBossMovesOutOfRange()
    {
        var resolver = CreateResolver();
        var area = CreateArea(7);
        var landmark = CreateLandmark(new Vector2(100, 100));
        var metadata = "Metadata/Monsters/Test/UncataloguedUnique";

        Assert.True(resolver.TryResolve(
            CreateContext(
                area,
                CreateBoss(metadata: metadata, gridPosition: new Vector2(100, 100)),
                [landmark]),
            out var nearby));
        Assert.Equal(AreaContentKind.Boss, nearby.Kind);
        Assert.Contains(
            nearby.Evidence,
            evidence => evidence.Source == "Landmark"
                        && evidence.Key == "Id"
                        && evidence.Value == landmark.LandmarkId);

        Assert.True(resolver.TryResolve(
            CreateContext(
                area,
                CreateBoss(metadata: metadata, gridPosition: new Vector2(200, 200)),
                [landmark]),
            out var moved));
        Assert.Equal(AreaContentKind.Boss, moved.Kind);
        Assert.Contains(
            moved.Evidence,
            evidence => evidence.Source == "Landmark"
                        && evidence.Key == "Id"
                        && evidence.Value == landmark.LandmarkId);
    }

    [Fact]
    public void TryResolve_MarksAConfirmedBossWithZeroLifeCompleted()
    {
        var resolver = CreateResolver();

        Assert.True(resolver.TryResolve(
            CreateContext(CreateArea(7), CreateBoss(currentLife: 0)),
            out var content));

        Assert.Equal(AreaContentPhase.Completed, content.Phase);
    }

    [Fact]
    public void ResolveMissing_PreservesOnlyConfirmedBossesWithinTheSameAreaSession()
    {
        var resolver = CreateResolver();
        var area = CreateArea(7);
        Assert.True(resolver.TryResolve(
            CreateContext(area, CreateBoss()),
            out _));

        Assert.Empty(resolver.ResolveMissing(area, new HashSet<uint> { 42 }));
        var missing = Assert.Single(resolver.ResolveMissing(area, new HashSet<uint>()));
        Assert.Equal(AreaContentKind.Boss, missing.Kind);
        Assert.Equal(AreaContentPhase.Completed, missing.Phase);

        Assert.Empty(resolver.ResolveMissing(CreateArea(8), new HashSet<uint>()));
    }

    [Fact]
    public void ResolveMissing_DoesNotTrackAnUnconfirmedUnique()
    {
        var resolver = CreateResolver();
        var area = CreateArea(7);
        Assert.False(resolver.TryResolve(
            CreateContext(
                area,
                CreateBoss(metadata: "Metadata/Monsters/Test/UncataloguedUnique")),
            out _));

        Assert.Empty(resolver.ResolveMissing(area, new HashSet<uint>()));
    }

    [Fact]
    public void TryResolve_NeverClassifiesAFriendlyUniqueAsBossContent()
    {
        var resolver = CreateResolver();

        Assert.False(resolver.TryResolve(
            CreateContext(
                CreateArea(7),
                CreateBoss(disposition: AreaEntityDisposition.Friendly)),
            out _));
    }

    private static BossContentResolver CreateResolver()
        => new(AreaContentCatalog.FromJson("""
            {
              "expedition": { "exactMetadata": [] },
              "bosses": {
                "exactMetadata": ["Metadata/Monsters/Test/ConfirmedBoss"],
                "metadataPrefixes": [],
                "displayNames": [],
                "bossTilePatternsByArea": {}
              }
            }
            """));

    private static BossContentResolver CreateResolverWithBossTilePattern()
        => new(AreaContentCatalog.FromJson("""
            {
              "expedition": { "exactMetadata": [] },
              "bosses": {
                "exactMetadata": [],
                "metadataPrefixes": [],
                "displayNames": [],
                "bossTilePatternsByArea": {
                  "TestArea": ["Terrain/Test/BossArena"]
                }
              }
            }
            """));

    private static AreaIdentity CreateArea(long sequence)
        => new((uint)sequence, "TestArea", 80, sequence);

    private static AreaContentContext CreateContext(
        AreaIdentity area,
        AreaEntitySnapshot entity,
        IReadOnlyList<AreaLandmarkSnapshot>? landmarks = null)
        => new(area, entity, landmarks ?? [], []);

    private static AreaLandmarkSnapshot CreateLandmark(
        Vector2 position,
        AreaLandmarkKind kind = AreaLandmarkKind.BossArena)
        => new(
            "boss-arena",
            "Boss Arena",
            "Metadata/Terrain/Test/BossArena.tdtx",
            kind,
            position,
            1);

    private static AreaEntitySnapshot CreateBoss(
        string metadata = "Metadata/Monsters/Test/ConfirmedBoss",
        int currentLife = 100,
        AreaEntityDisposition disposition = AreaEntityDisposition.Hostile,
        Vector2? gridPosition = null)
        => new(
            42,
            metadata,
            "Test Tyrant",
            AreaEntityCategory.Monster,
            Vector3.Zero,
            gridPosition ?? Vector2.Zero,
            disposition,
            AreaEntityRarity.Unique,
            currentLife,
            100,
            true,
            false,
            AreaChestState.NotApplicable,
            []);
}
