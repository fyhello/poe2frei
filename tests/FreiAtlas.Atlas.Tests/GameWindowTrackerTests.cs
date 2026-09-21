using System.Drawing;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Atlas.Tests;

public sealed class GameWindowTrackerTests
{
    [Fact]
    public void Track_ReturnsSnapshotForConfiguredProcess()
    {
        var windows = new FakeWindowInspector();
        windows.Add(new WindowInfo(
            (nint)0x101,
            7928,
            new Rectangle(100, 200, 1280, 720),
            IsForeground: true,
            IsMinimized: false)
        {
            ClientBounds = new Rectangle(108, 238, 1264, 674)
        });

        var tracker = new GameWindowTracker(windows);

        var snapshot = tracker.Track(7928);

        Assert.NotNull(snapshot);
        Assert.Equal((nint)0x101, snapshot!.Handle);
        Assert.Equal(7928, snapshot.ProcessId);
        Assert.Equal(new Rectangle(100, 200, 1280, 720), snapshot.Bounds);
        Assert.Equal(new Rectangle(108, 238, 1264, 674), snapshot.ClientBounds);
        Assert.True(snapshot.IsForeground);
        Assert.False(snapshot.IsMinimized);
    }

    [Fact]
    public void Track_ReturnsNullWhenProcessHasNoTopLevelWindow()
    {
        var tracker = new GameWindowTracker(new FakeWindowInspector());

        var snapshot = tracker.Track(7928);

        Assert.Null(snapshot);
    }

    [Fact]
    public void Track_PrefersVisibleForegroundWindowOverHiddenWindow()
    {
        var windows = new FakeWindowInspector();
        windows.Add(new WindowInfo(
            (nint)0x201,
            7928,
            new Rectangle(0, 0, 640, 480),
            IsForeground: false,
            IsMinimized: true));
        windows.Add(new WindowInfo(
            (nint)0x202,
            7928,
            new Rectangle(10, 20, 1920, 1080),
            IsForeground: true,
            IsMinimized: false));

        var tracker = new GameWindowTracker(windows);

        var snapshot = tracker.Track(7928);

        Assert.NotNull(snapshot);
        Assert.Equal((nint)0x202, snapshot!.Handle);
    }

    private sealed class FakeWindowInspector : IWindowInspector
    {
        private readonly List<WindowInfo> _windows = [];

        public void Add(WindowInfo window) => _windows.Add(window);

        public IReadOnlyList<WindowInfo> EnumerateTopLevelWindows()
            => _windows;
    }
}
