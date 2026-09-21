namespace FreiAtlas.Core.Player;

public sealed record VitalPool(int Current, int Maximum, int ReservedFlat, int ReservedFraction)
{
    public int AvailableMaximum => Maximum - ReservedFlat
        - (int)(((long)Maximum * ReservedFraction + 9999) / 10000);

    public double? Percent => AvailableMaximum > 0 ? 100d * Current / AvailableMaximum : null;
}

public sealed record PlayerVitalsSnapshot(
    int ProcessId,
    long SessionSequence,
    DateTimeOffset CapturedAt,
    VitalPool? Health,
    VitalPool? Mana,
    VitalPool? EnergyShield,
    string? Error)
{
    public bool IsValid => Health is not null && Error is null;
    public bool IsAlive => IsValid && Health!.Current > 0;

    public static PlayerVitalsSnapshot Unavailable(int processId, DateTimeOffset now, string error)
        => new(processId, 0, now, null, null, null, error);
}
