namespace FreiAtlas.Patcher;

public enum GameMode
{
    Unknown,
    Bundles2,
    GGPK
}

public sealed class GameModeInfo
{
    public string GameDirectory { get; set; } = string.Empty;
    public GameMode Mode { get; set; } = GameMode.Unknown;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsChina { get; set; }
    public string LanguageCode { get; set; } = "zh-Hans";
    public string BaseItemsPath { get; set; } = string.Empty;
    public string EndgameMapsPath { get; set; } = string.Empty;
    public string WorldAreasPath { get; set; } = string.Empty;
    public string ChestsPath { get; set; } = string.Empty;
    public string ArchnemesisModsPath { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
