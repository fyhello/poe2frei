using FreiAtlas.Atlas;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Host;

public static class AtlasWarmup
{
    public static AtlasSnapshot? WaitUntilStable(
        AtlasProviderService service,
        int maxSamples,
        TimeSpan sampleInterval)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (maxSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSamples));
        }

        for (var sample = 0; sample < maxSamples; sample++)
        {
            service.Sample();
            if (service.Current is { } stable)
            {
                return stable;
            }

            if (sample + 1 < maxSamples && sampleInterval > TimeSpan.Zero)
            {
                Thread.Sleep(sampleInterval);
            }
        }

        return service.Current;
    }
}
