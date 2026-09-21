using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Core.Tests;

public sealed class AreaMapProjectionProfileTests
{
    [Fact]
    public void Verified_ContainsAcceptedProjectionGeometryForCurrentMemoryProfile()
    {
        var profile = AreaMapProjectionProfile.Verified;

        Assert.Equal(Poe2MemoryProfile.Current.ProfileId, profile.ProfileId);
        Assert.Equal(
            new AreaMapProjectionParameters(
                AreaMapViewKind.LargeMap,
                new AreaMapLinearTransform(
                    0.0011527775f,
                    -0.0011527775f,
                    -0.00092354906f,
                    -0.00092354906f),
                AreaMapLinearTransform.Identity,
                new Vector2(0f, -20f)),
            profile.LargeMap);
        Assert.Equal(
            new AreaMapProjectionParameters(
                AreaMapViewKind.MiniMap,
                new AreaMapLinearTransform(
                    0.004591174f,
                    -0.004591174f,
                    -0.0036782247f,
                    -0.0036782247f),
                AreaMapLinearTransform.Identity,
                Vector2.Zero),
            profile.MiniMap);
        Assert.Equal(0.08f, profile.MiniMapVisibleCenterSafeInsetRatio);
    }
}
