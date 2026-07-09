// The agent's entire UI (SDD §12): a tray icon and four menu items.
// The real dashboard is the website; the agent stays invisible.
namespace GameNight.Agent;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _paused;
    public event Action<bool>? PauseToggled;

    public TrayIcon(string serverUrl, ServerLink link)
    {
        var menu = new ContextMenuStrip();
        var status = new ToolStripMenuItem("starting…") { Enabled = false };
        var pause = new ToolStripMenuItem("Pause monitoring");
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open dashboard", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(serverUrl) { UseShellExecute = true }));
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Application.Exit());

        pause.Click += (_, _) =>
        {
            _paused = !_paused;
            pause.Text = _paused ? "Resume monitoring" : "Pause monitoring";
            PauseToggled?.Invoke(_paused);
        };

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameNight agent",
            Visible = true,
            ContextMenuStrip = menu,
        };

        link.StatusChanged += s => menu.BeginInvoke(() =>
        {
            status.Text = s;
            _icon.Text = $"GameNight — {s}"[..Math.Min(63, $"GameNight — {s}".Length)]; // tooltip cap is 63 chars
        });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
