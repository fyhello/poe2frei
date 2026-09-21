namespace FreiAtlas.Settings.Tests;

public sealed class StartupModeTests
{
    [Fact]
    public void Resolve_RuntimeProbeArgument_ReturnsRuntimeProbe()
    {
        var mode = SettingsStartupModeResolver.Resolve(
            ["--freiatlas-runtime-probe"]);

        Assert.Equal(SettingsStartupMode.RuntimeProbe, mode);
    }

    [Fact]
    public void Resolve_OrdinaryArguments_ReturnsInteractive()
    {
        var mode = SettingsStartupModeResolver.Resolve(["--unknown"]);

        Assert.Equal(SettingsStartupMode.Interactive, mode);
    }
}
