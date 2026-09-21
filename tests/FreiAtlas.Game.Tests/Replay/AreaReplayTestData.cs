using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Tests.Replay;

internal static class AreaReplayTestData
{
    public const string CharacterName = "LiveHero";
    public const string RawAddress = "0x00007FF612345678";
    public const string DecimalAddress = "140733193388032";
    public const string WindowsAccountName = "SensitiveAccount";
    public const string StateValue = "0x00000003";

    public static AreaMapSnapshot Snapshot(
        uint areaHash,
        string areaCode,
        long sessionSequence,
        DateTimeOffset capturedAt,
        AreaContentPhase bossPhase = AreaContentPhase.Available,
        Vector2? largeMapShift = null)
        => new(
            capturedAt,
            30744,
            "poe2-test-profile",
            AreaMapSnapshotStatus.Stable,
            new AreaIdentity(areaHash, areaCode, 81, sessionSequence),
            new AreaPlayerSnapshot(
                CharacterName,
                95,
                new Vector3(100, 200, 10),
                new Vector2(10, 20)),
            [new AreaEntitySnapshot(
                17,
                "Metadata/Unknown/LiveHero/ReplayEntity",
                "ReplayEntity for LiveHero",
                AreaEntityCategory.Other,
                new Vector3(110, 210, 10),
                new Vector2(11, 21),
                AreaEntityDisposition.Neutral,
                AreaEntityRarity.NonMonster,
                0,
                0,
                false,
                false,
                AreaChestState.NotApplicable,
                ["Owner:LiveHero"])],
            [
                new AreaContentSnapshot(
                    $"boss:{sessionSequence}:31",
                    "map_boss",
                    "LiveHero Replay Boss",
                    AreaContentKind.Boss,
                    bossPhase,
                    new Vector3(300, 400, 10),
                    new Vector2(30, 40),
                    0.95f,
                    31,
                    []),
                new AreaContentSnapshot(
                    $"expedition:{sessionSequence}:73",
                    "expedition",
                    "Expedition Encounter",
                    AreaContentKind.Expedition,
                    AreaContentPhase.Available,
                    new Vector3(500, 600, 10),
                    new Vector2(50, 60),
                    0.95f,
                    73,
                    [
                        new AreaContentEvidence(
                            "StateMachine",
                            "StateMachine.State",
                            StateValue,
                            0.2f),
                        new AreaContentEvidence(
                            "StateMachine",
                            "Debug.Pointer",
                            RawAddress,
                            0.1f)
                    ],
                    new AreaExpeditionDetails(6))
            ],
            [],
            new AreaTerrainSnapshot(
                2,
                2,
                new byte[] { 1, 0, 1, 1 },
                ["Metadata/Terrain/LiveHero/ReplayTile"]),
            new AreaMapViewsSnapshot(
                new AreaMapViewSnapshot(
                    AreaMapViewKind.LargeMap,
                    AreaMapViewAvailability.Unverified,
                    true,
                    largeMapShift ?? new Vector2(3, 4),
                    1.25f,
                    0f,
                    false,
                    new AreaUiRect(0, 0, 1600, 900),
                    0.85f),
                new AreaMapViewSnapshot(
                    AreaMapViewKind.MiniMap,
                    AreaMapViewAvailability.Unverified,
                    true,
                    new Vector2(7, 8),
                    0.75f,
                    0.5f,
                    true,
                    new AreaUiRect(1350, 20, 230, 230),
                    0.85f)),
            [new AreaReadDiagnostic(
                "synthetic-address",
                $"Read failed at {RawAddress} and {DecimalAddress} for LiveHero "
                + $"in C:\\Users\\{WindowsAccountName}\\trace.",
                AreaDiagnosticSeverity.Warning)]);

    public static string CreateDirectory()
        => Path.Combine(
            Path.GetTempPath(),
            "frei-area-replay-tests",
            Guid.NewGuid().ToString("N"));

    public static AreaExpeditionRecipePanelSnapshot VerifiedRecipePanel()
        => new(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            new AreaUiRect(20, 70, 360, 390),
            new AreaUiRect(31.5f, 99, 333, 346.5f),
            "expedition:1:73",
            [new AreaExpeditionRecipePanelRow(
                "recipe-1",
                20,
                new AreaUiRect(31.5f, 99, 333, 31.5f),
                true)]);
}
