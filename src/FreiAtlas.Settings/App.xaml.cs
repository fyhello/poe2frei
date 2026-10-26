using System.IO;
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

        // 兜住从 async void 事件处理器逸出的异常。
        //
        // MainWindow.OnWebMessageReceived 是 async void：await 之后的异常不会
        // 回到调用栈，而是直接抛给 SynchronizationContext。命令处理器内部虽有
        // try/catch，但白名单只覆盖三种异常类型，IOException、
        // UnauthorizedAccessException 等会一路逸出并终止进程 —— 用户看到的是
        // 设置窗口凭空消失，且没有任何日志。
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportUnhandled(args.Exception);
        };

        if (SettingsStartupModeResolver.Resolve(e.Args) == SettingsStartupMode.RuntimeProbe)
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 把未捕获异常的摘要告知用户，并在标准错误留一份完整堆栈供排查。
    ///
    /// 不能静默吞掉：用户至少要知道操作失败了。也不做去重/计数 ——
    /// 一次操作失败通常是单一异常，值得完整呈现。
    /// </summary>
    private static void ReportUnhandled(Exception exception)
    {
        try
        {
            Console.Error.WriteLine("[FreiAtlas] 未捕获异常：" + exception);
        }
        catch (IOException)
        {
            // 无控制台时忽略，不影响后续提示。
        }

        try
        {
            MessageBox.Show(
                "操作过程中发生未预期的错误，本次操作可能未生效。\n\n"
                + exception.GetType().Name + "：" + exception.Message
                + "\n\n如果问题持续出现，请先尝试「一键恢复纯净原版」。",
                "FreiAtlas 设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (InvalidOperationException)
        {
            // 应用正在退出、无法再弹窗时忽略。
        }
    }
}
