namespace FreiAtlas.Core.Settings;

public interface IAtlasSettingsSource
{
    AtlasDisplaySettings Current { get; }

    event Action<AtlasDisplaySettings>? SettingsChanged;
}
