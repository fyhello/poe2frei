using FreiAtlas.Host;

namespace FreiAtlas.Host.Tests;

public sealed class HostOptionsTests
{
    [Fact]
    public void Parse_ReplayModeCapturesPath()
    {
        var options = HostOptions.Parse(
            [
                "--replay",
                "data/atlas/replay/pid7928-stable.json"
            ]);

        Assert.Equal(
            "data/atlas/replay/pid7928-stable.json",
            options.ReplayPath);
        Assert.False(options.AtlasProbe);
        Assert.False(options.AtlasOverlay);
    }

    [Fact]
    public void Parse_PidProbeCapturesPid()
    {
        var options = HostOptions.Parse(
            [
                "--pid",
                "7928",
                "--atlas-probe"
            ]);

        Assert.Equal(7928, options.ProcessId);
        Assert.True(options.AtlasProbe);
        Assert.False(options.AtlasOverlay);
    }

    [Fact]
    public void Parse_AtlasProbeWithoutPid_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HostOptions.Parse(["--atlas-probe"]));

        Assert.Contains("--pid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AtlasOverlayCapturesExplicitPid()
    {
        var options = HostOptions.Parse(
            ["--pid", "55272", "--atlas-overlay"]);

        Assert.Equal(55272, options.ProcessId);
        Assert.True(options.AtlasOverlay);
        Assert.False(options.AtlasProbe);
    }

    [Fact]
    public void Parse_AtlasOverlayWithoutPid_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HostOptions.Parse(["--atlas-overlay"]));

        Assert.Contains("--pid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AtlasOverlayAndProbe_Throws()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
            ["--pid", "55272", "--atlas-overlay", "--atlas-probe"]));
    }

    [Fact]
    public void Parse_AtlasOverlayAndReplay_Throws()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
            ["--pid", "55272", "--atlas-overlay", "--replay", "atlas.json"]));
    }

    [Fact]
    public void Parse_AreaProbeCapturesExplicitPid()
    {
        var options = HostOptions.Parse(
            ["--pid", "30744", "--area-probe"]);

        Assert.Equal(30744, options.ProcessId);
        Assert.True(options.AreaProbe);
        Assert.False(options.AreaWatch);
    }

    [Fact]
    public void Parse_AreaProbeWithoutPid_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HostOptions.Parse(["--area-probe"]));

        Assert.Contains("--pid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AreaExpeditionProbeCapturesExplicitPid()
    {
        var options = HostOptions.Parse(
            ["--pid", "30744", "--area-expedition-probe"]);

        Assert.Equal(30744, options.ProcessId);
        Assert.True(options.AreaExpeditionProbe);
        Assert.False(options.AreaProbe);
        Assert.False(options.AreaWatch);
    }

    [Fact]
    public void Parse_AreaExpeditionProbeWithoutPid_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HostOptions.Parse(["--area-expedition-probe"]));

        Assert.Contains("--pid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AreaExpeditionProbeCombinedWithAnotherMode_Throws()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
            [
                "--pid",
                "30744",
                "--area-expedition-probe",
                "--area-probe"
            ]));
    }

    [Fact]
    public void Parse_PidWithoutExplicitMode_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            HostOptions.Parse(["--pid", "30744"]));
    }

    [Fact]
    public void Parse_AreaRecordCapturesPidAndDirectory()
    {
        var options = HostOptions.Parse(
            ["--pid", "30744", "--area-record", "data/area-map/run-1"]);

        Assert.Equal(30744, options.ProcessId);
        Assert.Equal("data/area-map/run-1", options.AreaRecordPath);
        Assert.Null(options.AreaReplayPath);
    }

    [Fact]
    public void Parse_AreaReplayCapturesDirectoryWithoutPid()
    {
        var options = HostOptions.Parse(["--area-replay", "data/area-map/run-1"]);

        Assert.Null(options.ProcessId);
        Assert.Equal("data/area-map/run-1", options.AreaReplayPath);
    }

    [Fact]
    public void Parse_AreaReplayWithPidThrows()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
            ["--pid", "30744", "--area-replay", "data/area-map/run-1"]));
    }

    [Theory]
    [InlineData("viewport", AreaProjectionProbeMode.Viewport)]
    [InlineData("targets", AreaProjectionProbeMode.Targets)]
    public void Parse_AreaProjectionProbeCapturesExplicitPidModeAndLog(
        string value,
        AreaProjectionProbeMode expected)
    {
        var options = HostOptions.Parse(
        [
            "--pid", "29368",
            "--area-projection-probe", value,
            "--projection-log", "data/area-map/projection-current"
        ]);

        Assert.Equal(29368, options.ProcessId);
        Assert.Equal(expected, options.AreaProjectionProbeMode);
        Assert.Equal(
            "data/area-map/projection-current",
            options.ProjectionLogPath);
        Assert.True(options.RequiresCancellationHandling);
    }

    [Fact]
    public void Parse_AreaProjectionProbeRequiresExplicitPid()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
        [
            "--area-projection-probe", "viewport",
            "--projection-log", "data/area-map/projection-current"
        ]));
    }

    [Fact]
    public void Parse_AreaProjectionProbeRejectsMissingOrInvalidStage()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
        [
            "--pid", "29368",
            "--area-projection-probe",
            "--projection-log", "data/area-map/projection-current"
        ]));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
        [
            "--pid", "29368",
            "--area-projection-probe", "invalid",
            "--projection-log", "data/area-map/projection-current"
        ]));
    }

    [Fact]
    public void Parse_AreaProjectionProbeAndProjectionLogMustBePaired()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
        [
            "--pid", "29368",
            "--area-projection-probe", "viewport"
        ]));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(
        [
            "--projection-log", "data/area-map/projection-current"
        ]));
    }

    [Fact]
    public void Parse_AreaProjectionProbeRejectsEveryExistingExecutionMode()
    {
        string[][] conflicts =
        [
            ["--area-watch"],
            ["--area-record", "data/area-map/run-1"],
            ["--replay", "atlas.json"],
            ["--atlas-overlay"]
        ];

        foreach (var conflict in conflicts)
        {
            Assert.Throws<ArgumentException>(() => HostOptions.Parse(
            [
                "--pid", "29368",
                "--area-projection-probe", "targets",
                "--projection-log", "data/area-map/projection-current",
                .. conflict
            ]));
        }
    }
}
