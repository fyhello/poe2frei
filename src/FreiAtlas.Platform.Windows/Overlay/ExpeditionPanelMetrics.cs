namespace FreiAtlas.Platform.Windows.Overlay;

internal readonly record struct ExpeditionPanelMetrics(
    float Scale,
    int TitleHeight,
    int SummaryHeight,
    int RecipeHeight,
    int RuneSize,
    int RuneGap,
    int RewardSize,
    int RewardWidth,
    int ValueWidth,
    int GroupInset,
    int HorizontalMargin,
    int VerticalMargin,
    int MaximumHeight,
    float CornerRadius)
{
    public const float MinimumScale = 0.75f;
    public const float MaximumScale = 1.50f;

    public static ExpeditionPanelMetrics Create(float scale)
    {
        var clamped = float.IsFinite(scale)
            ? Math.Clamp(scale, MinimumScale, MaximumScale)
            : 1f;
        int Px(float value) => Math.Max(1, (int)MathF.Round(value * clamped));

        return new ExpeditionPanelMetrics(
            clamped,
            Px(32),
            Px(30),
            Px(30),
            Px(22),
            Px(1),
            Px(20),
            Px(40),
            Px(52),
            Px(7),
            Px(32),
            Px(56),
            Px(760),
            6f * clamped);
    }

    public int Px(float value)
        => Math.Max(1, (int)MathF.Round(value * Scale));
}
