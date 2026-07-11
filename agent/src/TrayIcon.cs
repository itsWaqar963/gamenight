// The agent's tray UI (SDD §12). Status window is optional visual feedback;
// the agent still runs headless in the tray when the window is closed.
namespace GameNight.Agent;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _pause;
    private bool _paused;
    private string _connectionStatus = "starting…";
    public event Action<bool>? PauseToggled;
    public event Action? UpdateCheckRequested;
    public event Action? OpenStatusRequested;

    public TrayIcon(string serverUrl, ServerLink link)
    {
        var menu = new ContextMenuStrip();
        _menu = menu;
        _status = new ToolStripMenuItem(StatusLabel()) { Enabled = false };
        var version = new ToolStripMenuItem($"Version {AgentInfo.Version}") { Enabled = false };
        _pause = new ToolStripMenuItem("Pause monitoring");
        var checkUpdate = new ToolStripMenuItem("Check for updates");
        menu.Items.Add(_status);
        menu.Items.Add(version);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open status", null, (_, _) => OpenStatusRequested?.Invoke());
        menu.Items.Add("Open dashboard", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(serverUrl) { UseShellExecute = true }));
        menu.Items.Add(checkUpdate);
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Application.Exit());

        checkUpdate.Click += (_, _) => UpdateCheckRequested?.Invoke();

        _pause.Click += (_, _) => SetPaused(!_paused, raiseEvent: true);

        // BeginInvoke needs a native handle; a ContextMenuStrip only creates
        // one when first opened. Force it now, or early status updates throw.
        _ = menu.Handle;

        _icon = new NotifyIcon
        {
            Icon = AppIcon.ForTray,
            Text = TipText(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenStatusRequested?.Invoke();

        link.StatusChanged += s =>
        {
            try
            {
                menu.BeginInvoke(() =>
                {
                    _connectionStatus = s;
                    _status.Text = StatusLabel();
                    _icon.Text = TipText();
                });
            }
            catch { /* UI cosmetics must NEVER kill the connection loop */ }
        };
    }

    public void SetPaused(bool paused, bool raiseEvent = false)
    {
        void Apply()
        {
            if (_paused == paused && !raiseEvent) return;
            _paused = paused;
            _pause.Text = _paused ? "Resume monitoring" : "Pause monitoring";
            if (raiseEvent) PauseToggled?.Invoke(_paused);
        }
        try
        {
            if (_menu.InvokeRequired) _menu.BeginInvoke(Apply);
            else Apply();
        }
        catch { /* tray cosmetics must never crash */ }
    }

    private string StatusLabel() => _connectionStatus;

    // NotifyIcon.Text max is 63 chars on Windows.
    private string TipText()
    {
        string tip = $"GameNight v{AgentInfo.Version} — {_connectionStatus}";
        return tip[..Math.Min(63, tip.Length)];
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
