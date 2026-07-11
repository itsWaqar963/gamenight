// Simple agent status window. Tray stays primary; this is visual feedback for
// connection, monitoring, and updates. ContentHost is Dock=Fill so a future
// WebView2 can embed the web dashboard without rewriting the outer shell.
namespace GameNight.Agent;

public sealed class AgentStatusForm : Form
{
    private readonly Label _connectionValue = ValueLabel();
    private readonly Label _monitoringValue = ValueLabel();
    private readonly Label _presenceValue = ValueLabel();
    private readonly Label _radminValue = ValueLabel();
    private readonly Label _updateValue = ValueLabel();
    private readonly Label _versionValue = ValueLabel();
    private readonly Label _logsValue = ValueLabel();
    private readonly Button _pauseBtn = new() { Width = 150, Height = 32, FlatStyle = FlatStyle.System };
    private readonly Button _updateBtn = new() { Text = "Check for updates", Width = 150, Height = 32, FlatStyle = FlatStyle.System };
    private readonly Button _dashboardBtn = new() { Text = "Open dashboard", Width = 150, Height = 32, FlatStyle = FlatStyle.System };

    private readonly string _serverUrl;
    private bool _paused;
    private string _connection = "starting…";
    private string _radmin = "—";

    public event Action<bool>? PauseToggled;
    public event Action? UpdateCheckRequested;

    /// <summary>
    /// Reserved fill area — today holds the status grid; later can host WebView2
    /// pointed at the GameNight web app without changing the outer shell.
    /// </summary>
    public Panel ContentHost { get; }

    public AgentStatusForm(string serverUrl)
    {
        _serverUrl = serverUrl;

        Text = "GameNight Agent";
        Icon = AppIcon.ForWindow;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 430);
        BackColor = Color.FromArgb(245, 246, 248);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Color.FromArgb(28, 32, 38),
        };
        var brand = new PictureBox
        {
            Image = AppIcon.ForWindow.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Location = new Point(16, 16),
        };
        header.Controls.Add(brand);
        header.Controls.Add(new Label
        {
            Text = "GameNight",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 14f),
            AutoSize = true,
            Location = new Point(66, 14),
        });
        header.Controls.Add(new Label
        {
            Text = "Agent status",
            ForeColor = Color.FromArgb(180, 188, 198),
            AutoSize = true,
            Location = new Point(68, 40),
        });

        ContentHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = Color.FromArgb(245, 246, 248),
        };

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Width = 400,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(grid, 0, "Connection", _connectionValue);
        AddRow(grid, 1, "Monitoring", _monitoringValue);
        AddRow(grid, 2, "Presence", _presenceValue);
        AddRow(grid, 3, "Radmin", _radminValue);
        AddRow(grid, 4, "Updates", _updateValue);
        AddRow(grid, 5, "Logs", _logsValue);
        AddRow(grid, 6, "Version", _versionValue);
        _versionValue.Text = $"v{AgentInfo.Version}";
        _logsValue.Text = "—";

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Width = 400,
            Padding = new Padding(0, 12, 0, 0),
        };
        actions.Controls.Add(_pauseBtn);
        actions.Controls.Add(_updateBtn);
        actions.Controls.Add(_dashboardBtn);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            ForeColor = Color.FromArgb(100, 108, 118),
            Padding = new Padding(0, 16, 0, 0),
            Text = "Closing this window keeps the agent running in the tray.",
        };

        body.Controls.Add(grid);
        body.Controls.Add(actions);
        body.Controls.Add(hint);
        ContentHost.Controls.Add(body);

        Controls.Add(ContentHost);
        Controls.Add(header);

        _pauseBtn.Click += (_, _) =>
        {
            _paused = !_paused;
            RefreshMonitoringLabel();
            PauseToggled?.Invoke(_paused);
        };
        _updateBtn.Click += (_, _) =>
        {
            SetUpdateStatus("Checking…");
            UpdateCheckRequested?.Invoke();
        };
        _dashboardBtn.Click += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_serverUrl) { UseShellExecute = true });

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        RefreshMonitoringLabel();
        ApplyStatusColors();
    }

    public void ShowOrFocus()
    {
        if (InvokeRequired) { BeginInvoke(ShowOrFocus); return; }
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void SetConnectionStatus(string status)
    {
        if (InvokeRequired) { BeginInvoke(() => SetConnectionStatus(status)); return; }
        _connection = status;
        _connectionValue.Text = status;
        ApplyStatusColors();
    }

    public void SetPaused(bool paused)
    {
        if (InvokeRequired) { BeginInvoke(() => SetPaused(paused)); return; }
        if (_paused == paused) return;
        _paused = paused;
        RefreshMonitoringLabel();
    }

    public void SetPresence(string state, RadminInfo radmin)
    {
        if (InvokeRequired) { BeginInvoke(() => SetPresence(state, radmin)); return; }
        _presenceValue.Text = state switch
        {
            "in_game" => "In game (Far Cry 2)",
            "online" => "Online",
            "idle" => "Idle",
            _ => state,
        };
        _radmin = radmin.Connected
            ? (radmin.Ip is null ? "Connected" : $"Connected · {radmin.Ip}")
            : "Not connected";
        _radminValue.Text = _radmin;
        ApplyStatusColors();
    }

    public void SetUpdateStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetUpdateStatus(text)); return; }
        _updateValue.Text = text;
    }

    public void SetLogStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetLogStatus(text)); return; }
        _logsValue.Text = text;
    }

    private void RefreshMonitoringLabel()
    {
        _monitoringValue.Text = _paused ? "Paused" : "Active";
        _pauseBtn.Text = _paused ? "Resume monitoring" : "Pause monitoring";
        ApplyStatusColors();
    }

    private void ApplyStatusColors()
    {
        bool connected = _connection.Contains("connected", StringComparison.OrdinalIgnoreCase)
            && !_connection.Contains("reconnecting", StringComparison.OrdinalIgnoreCase);
        _connectionValue.ForeColor = connected
            ? Color.FromArgb(22, 128, 72)
            : Color.FromArgb(180, 100, 20);

        _monitoringValue.ForeColor = _paused
            ? Color.FromArgb(160, 100, 20)
            : Color.FromArgb(22, 128, 72);

        _radminValue.ForeColor = _radmin.StartsWith("Connected", StringComparison.Ordinal)
            ? Color.FromArgb(22, 128, 72)
            : Color.FromArgb(160, 60, 50);
    }

    private static void AddRow(TableLayoutPanel grid, int row, string key, Label value)
    {
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var keyLabel = new Label
        {
            Text = key,
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 96, 106),
            Margin = new Padding(0, 6, 8, 6),
            Anchor = AnchorStyles.Left,
        };
        value.Margin = new Padding(0, 6, 0, 6);
        grid.Controls.Add(keyLabel, 0, row);
        grid.Controls.Add(value, 1, row);
    }

    private static Label ValueLabel() => new()
    {
        Text = "—",
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.5f),
        ForeColor = Color.FromArgb(28, 32, 38),
        Anchor = AnchorStyles.Left,
    };
}
