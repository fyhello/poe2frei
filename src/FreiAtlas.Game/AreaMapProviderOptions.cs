using FreiAtlas.Core.Area;

namespace FreiAtlas.Game;

public sealed record AreaMapProviderOptions
{
    public TimeSpan WorldInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan RealtimeInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    public TimeSpan SameAreaRootGracePeriod { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan SameAreaEntityTreeGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    public AreaUiRect ClientViewport { get; init; } = new(0f, 0f, 1920f, 1080f);

    public bool IncludeExpeditionResearchEvidence { get; init; }

    internal void Validate()
    {
        if (WorldInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(WorldInterval));
        }

        if (RealtimeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RealtimeInterval));
        }

        if (SameAreaRootGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SameAreaRootGracePeriod));
        }

        if (SameAreaEntityTreeGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SameAreaEntityTreeGracePeriod));
        }

        if (!float.IsFinite(ClientViewport.X)
            || !float.IsFinite(ClientViewport.Y)
            || !float.IsFinite(ClientViewport.Width)
            || !float.IsFinite(ClientViewport.Height)
            || ClientViewport.Width <= 0f
            || ClientViewport.Height <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(ClientViewport));
        }
    }
}
