using System.Windows;

namespace FreiAtlas.Settings;

internal enum SettingsStartupMode
{
    Interactive,
    RuntimeProbe,
}

internal static class SettingsStartupModeResolver
{
    internal const string RuntimeProbeArgument = "--freiatlas-runtime-probe";

    internal static SettingsStartupMode Resolve(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument => string.Equals(
            argument,
            RuntimeProbeArgument,
            StringComparison.OrdinalIgnoreCase))
            ? SettingsStartupMode.RuntimeProbe
            : SettingsStartupMode.Interactive;
    }
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (SettingsStartupModeResolver.Resolve(e.Args) == SettingsStartupMode.RuntimeProbe)
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
