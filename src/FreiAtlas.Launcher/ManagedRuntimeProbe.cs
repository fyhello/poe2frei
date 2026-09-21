using System.Diagnostics;

namespace FreiAtlas.Launcher;

internal sealed record RuntimeProbeResult(
    RuntimeProbeStatus Status,
    string Details);

internal static class ManagedRuntimeProbe
{
    private const string RuntimeProbeArgument = "--freiatlas-runtime-probe";
    private const int ProbeTimeoutMilliseconds = 5_000;

    internal static RuntimeProbeResult Probe(string managedApplicationPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(managedApplicationPath)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(managedApplicationPath) ?? AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add(RuntimeProbeArgument);
            startInfo.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new RuntimeProbeResult(
                    RuntimeProbeStatus.Failed,
                    "无法创建运行环境检查进程。");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return new RuntimeProbeResult(
                    RuntimeProbeStatus.Failed,
                    "运行环境检查超时。");
            }

            _ = standardOutput.GetAwaiter().GetResult();
            return Classify(
                process.ExitCode,
                standardError.GetAwaiter().GetResult());
        }
        catch (Exception exception)
        {
            return new RuntimeProbeResult(
                RuntimeProbeStatus.Failed,
                exception.Message);
        }
    }

    internal static RuntimeProbeResult Classify(
        int exitCode,
        string standardError)
    {
        if (exitCode == 0)
        {
            return new RuntimeProbeResult(RuntimeProbeStatus.Available, string.Empty);
        }

        if (Contains(standardError, "Microsoft.WindowsDesktop.App")
            || Contains(standardError, "You must install or update .NET")
            || Contains(standardError, "hostfxr.dll"))
        {
            return new RuntimeProbeResult(
                RuntimeProbeStatus.MissingDesktopRuntime,
                standardError.Trim());
        }

        var details = string.IsNullOrWhiteSpace(standardError)
            ? $"运行环境检查进程异常退出，退出码：{exitCode}。"
            : standardError.Trim();
        return new RuntimeProbeResult(RuntimeProbeStatus.Failed, details);
    }

    private static bool Contains(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
}
