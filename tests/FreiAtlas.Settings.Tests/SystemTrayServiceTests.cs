using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FreiAtlas.Settings.Windowing;
using Forms = System.Windows.Forms;

namespace FreiAtlas.Settings.Tests;

public sealed class SystemTrayServiceTests
{
    [Fact]
    public void Minimize_HidesWindowWithoutClosingOrStoppingDispatcher()
    {
        RunOnStaThread(() =>
        {
            var window = CreateWindow();
            using var tray = new SystemTrayService(window);
            try
            {
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.WindowState = WindowState.Minimized;
                DrainDispatcher();

                Assert.False(window.IsVisible);
                Assert.False(closed);
                var callbackRan = false;
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(() =>
                {
                    callbackRan = true;
                    frame.Continue = false;
                });
                Dispatcher.PushFrame(frame);
                Assert.True(callbackRan);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void RestoreWindow_PreservesPreviousStateAcrossRepeatedMinimize(WindowState state)
    {
        RunOnStaThread(() =>
        {
            var window = CreateWindow();
            using var tray = new SystemTrayService(window);
            try
            {
                var transitions = new List<string>();
                window.StateChanged += (_, _) => transitions.Add($"状态事件：{window.WindowState}，可见：{window.IsVisible}");
                window.IsVisibleChanged += (_, _) => transitions.Add($"可见事件：{window.WindowState}，可见：{window.IsVisible}");
                window.WindowState = state;
                DrainDispatcher();
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    transitions.Add($"第 {cycle + 1} 轮最小化前：{window.WindowState}，可见：{window.IsVisible}");
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();
                    Assert.False(window.IsVisible, string.Join(Environment.NewLine, transitions));

                    tray.RestoreWindow();
                    DrainDispatcher();

                    Assert.True(window.IsVisible);
                    Assert.Equal(state, window.WindowState);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TrayMenuAndDoubleClick_RestoreWindowAndRemoveIconOnExit()
    {
        RunOnStaThread(() =>
        {
            var window = CreateWindow();
            using var tray = new SystemTrayService(window);
            try
            {
                // 检查实际控件事件绑定，不为测试向业务模块暴露托盘控件。
                var iconField = typeof(SystemTrayService).GetField(
                    "_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(iconField);
                var icon = Assert.IsType<Forms.NotifyIcon>(iconField.GetValue(tray));
                var menu = Assert.IsType<Forms.ContextMenuStrip>(icon.ContextMenuStrip);
                Assert.True(icon.Visible);
                Assert.NotNull(icon.Icon);
                Assert.Equal("FreiAtlas", icon.Text);

                window.WindowState = WindowState.Minimized;
                DrainDispatcher();
                var open = Assert.Single(menu.Items.OfType<Forms.ToolStripMenuItem>(), item => item.Text == "打开设置");
                open.PerformClick();
                DrainDispatcher();
                Assert.True(window.IsVisible);

                window.WindowState = WindowState.Minimized;
                DrainDispatcher();
                var doubleClick = typeof(Forms.NotifyIcon).GetMethod(
                    "OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(doubleClick);
                doubleClick.Invoke(icon, [new Forms.MouseEventArgs(Forms.MouseButtons.Left, 2, 0, 0, 0)]);
                DrainDispatcher();
                Assert.True(window.IsVisible);

                var closed = false;
                window.Closed += (_, _) => closed = true;
                var exit = Assert.Single(menu.Items.OfType<Forms.ToolStripMenuItem>(), item => item.Text == "退出");
                exit.PerformClick();
                Assert.True(closed);
                Assert.False(icon.Visible);
                Assert.True(menu.IsDisposed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RequestExit_UsesWindowClosingPipelineAndHonorsCancellation()
    {
        RunOnStaThread(() =>
        {
            var window = CreateWindow();
            using var tray = new SystemTrayService(window);
            try
            {
                var closingCalls = 0;
                var closed = false;
                window.Closing += (_, e) => e.Cancel = ++closingCalls == 1;
                window.Closed += (_, _) => closed = true;
                window.WindowState = WindowState.Minimized;

                tray.RequestExit();
                Assert.Equal(1, closingCalls);
                Assert.False(closed);

                tray.RestoreWindow();
                Assert.True(window.IsVisible);
                tray.RequestExit();
                Assert.True(closed);
                Assert.Equal(2, closingCalls);

                // 关闭后排队到达的托盘操作不能再次打开已销毁的窗口。
                tray.RestoreWindow();
                tray.RequestExit();
                Assert.Equal(2, closingCalls);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Dispose_IsIdempotentAndDetachesWindowHandlers()
    {
        RunOnStaThread(() =>
        {
            var window = CreateWindow();
            using var tray = new SystemTrayService(window);
            try
            {
                tray.Dispose();
                tray.Dispose();
                window.WindowState = WindowState.Minimized;
                Assert.True(window.IsVisible);

                tray.RestoreWindow();
                Assert.Equal(WindowState.Minimized, window.WindowState);
                var closed = false;
                window.Closed += (_, _) => closed = true;
                tray.RequestExit();
                Assert.False(closed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Window CreateWindow()
    {
        var window = new Window
        {
            Title = "FreiAtlas 托盘测试",
            Width = 200,
            Height = 100,
            Left = -10000,
            Top = -10000,
            Opacity = 0,
            ShowInTaskbar = false
        };
        window.Show();
        DrainDispatcher();
        return window;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "托盘窗口测试超时。");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
