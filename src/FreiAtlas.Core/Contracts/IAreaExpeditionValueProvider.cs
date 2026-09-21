using FreiAtlas.Core.Area;

namespace FreiAtlas.Core.Contracts;

public interface IAreaExpeditionValueProvider
{
    string? GetValueText(AreaExpeditionDetails? details);
}
