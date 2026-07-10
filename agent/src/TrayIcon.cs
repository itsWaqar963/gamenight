// The agent's entire UI (SDD §12): a tray icon and four menu items.
// The real dashboard is the website; the agent stays invisible.
namespace GameNight.Agent;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _paused;
    public event Action<bool>? PauseToggled;

    public TrayIcon(string serverUrl, ServerLink link)
    {
        var menu = new ContextMenuStrip();
        _menu = menu;
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

        // BeginInvoke needs a native handle; a ContextMenuStrip only creates
        // one when first opened. Force it now, or early status updates throw.
        _ = menu.Handle;

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameNight agent",
            Visible = true,
            ContextMenuStrip = menu,
        };

        link.StatusChanged += s =>
        {
            try
            {
                menu.BeginInvoke(() =>
                {
                    status.Text = s;
                    string tip = $"GameNight — {s}";
                    _icon.Text = tip[..Math.Min(63, tip.Length)];
                });
            }
            catch { /* UI cosmetics must NEVER kill the connection loop */ }
        };
    }

    /// <summary>Show a native Windows toast (balloon tip). Phase 4.</summary>
    /// Safe to call from any thread — marshals to the UI thread via the menu.
    public void ShowToast(string title, string body)
    {
        void Show()
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = body;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(5000);
            }
            catch { /* toast is best-effort; never crash on it */ }
        }
        try
        {
            if (_menu.InvokeRequired) _menu.BeginInvoke(Show);
            else Show();
        }
        catch { /* if the handle isn't ready, silently skip this toast */ }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}