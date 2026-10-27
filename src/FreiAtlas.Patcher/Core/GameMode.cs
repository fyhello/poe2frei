namespace FreiAtlas.Patcher.Core;

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

    /// <summary>
    /// 是否国服（WeGame）客户端。表示「归属哪个服务器」，不代表客户端界面语言——
    /// 国服客户端通常为简体，但国际服客户端打上汉化后同样是简体。
    /// </summary>
    public bool IsChina { get; set; }

    /// <summary>
    /// 客户端实际界面语言代码，取自游戏配置文件（如 zh-TW / zh-CN / en）。
    /// </summary>
    public string LanguageCode { get; set; } = "zh-Hans";

    /// <summary>
    /// 客户端当前是否使用简体中文。
    ///
    /// 这是所有「该输出简体还是繁体」判断的唯一依据。注意它不等于 <see cref="IsChina"/>：
    /// 国际服客户端打上汉化补丁后内容已是简体，但 <see cref="IsChina"/> 仍为 false。
    /// 国服客户端一律按简体处理。
    /// </summary>
    public bool IsSimplifiedChinese =>
        IsChina || LanguageCode.StartsWith("zh-cn", StringComparison.OrdinalIgnoreCase)
                || LanguageCode.StartsWith("zh-hans", StringComparison.OrdinalIgnoreCase)
                || LanguageCode.Equals("sc", StringComparison.OrdinalIgnoreCase);

    public string BaseItemsPath { get; set; } = string.Empty;
    public string EndgameMapsPath { get; set; } = string.Empty;
    public string WorldAreasPath { get; set; } = string.Empty;
    public string ChestsPath { get; set; } = string.Empty;
    public string WordsPath { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
