using System.Numerics;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Core.Area;

public sealed record AreaMapProjectionProfile(
    string ProfileId,
    AreaMapProjectionParameters LargeMap,
    AreaMapProjectionParameters MiniMap,
    float MiniMapVisibleCenterSafeInsetRatio)
{
    public static AreaMapProjectionProfile Verified { get; } = new(
        Poe2MemoryProfile.Current.ProfileId,
        new AreaMapProjectionParameters(
            AreaMapViewKind.LargeMap,
            new AreaMapLinearTransform(
                0.0011527775f,
                -0.0011527775f,
                -0.00092354906f,
                -0.00092354906f),
            AreaMapLinearTransform.Identity,
            new Vector2(0f, -20f)),
        new AreaMapProjectionParameters(
            AreaMapViewKind.MiniMap,
            new AreaMapLinearTransform(
                0.004591174f,
                -0.004591174f,
                -0.0036782247f,
                -0.0036782247f),
            AreaMapLinearTransform.Identity,
            Vector2.Zero),
        0.08f);
}
