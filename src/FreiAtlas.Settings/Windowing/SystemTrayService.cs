using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FreiAtlas.Settings.Windowing;

internal sealed class SystemTrayService : IDisposable
{
    private readonly Window _window;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private WindowState _restoreState;
    private bool _disposed;

    public SystemTrayService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _restoreState = window.WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开设置", null, (_, _) => RestoreWindow());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => RequestExit());
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "FreiAtlas",
            ContextMenuStrip = _menu
        };

        try
        {
            _notifyIcon.Visible = true;
            _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
            _window.StateChanged += OnWindowStateChanged;
            _window.Closed += OnWindowClosed;
            OnWindowStateChanged(window, EventArgs.Empty);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RestoreWindow()
    {
        if (_disposed) return;

        // 先显示再恢复，让 WPF 同步原生窗口状态，避免下一次最小化漏发状态事件。
        var restoreState = _restoreState;
        _window.Show();
        _window.WindowState = restoreState;
        _window.Activate();
    }

    public void RequestExit()
    {
        if (!_disposed) _window.Close();
    }

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) RestoreWindow();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.Hide();
        }
        else
        {
            _restoreState = _window.WindowState;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.StateChanged -= OnWindowStateChanged;
        _window.Closed -= OnWindowClosed;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
