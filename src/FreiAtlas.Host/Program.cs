using FreiAtlas.Host;
using FreiAtlas.Platform.Windows.Windows;

try
{
    DpiAwareness.TryEnablePerMonitorV2();
    var options = HostOptions.Parse(args);
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler? cancelHandler = null;
    if (options.RequiresCancellationHandling)
    {
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
    }

    try
    {
        return new HostRuntime().Run(options, cancellation.Token);
    }
    finally
    {
        if (cancelHandler is not null)
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error={exception.Message}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"fatal={exception.Message}");
    return 1;
}
