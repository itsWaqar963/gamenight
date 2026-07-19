// Agent window with Status | Voice tabs (ADR-0012). Tray stays primary;
// closing hides to tray. Voice tab uses voice-server signaling + native WebRTC.
namespace GameNight.Agent;

public sealed class AgentStatusForm : Form
{
    private static readonly Color Bg = Color.FromArgb(22, 24, 28);
    private static readonly Color Surface = Color.FromArgb(32, 36, 42);
    private static readonly Color HeaderBg = Color.FromArgb(16, 18, 22);
    private static readonly Color TextMuted = Color.FromArgb(140, 148, 160);
    private static readonly Color TextPrimary = Color.FromArgb(232, 236, 240);
    private static readonly Color Ok = Color.FromArgb(62, 196, 120);
    private static readonly Color Warn = Color.FromArgb(230, 170, 70);
    private static readonly Color Bad = Color.FromArgb(220, 90, 90);

    private readonly Label _connectionValue = ValueLabel();
    private readonly Label _monitoringValue = ValueLabel();
    private readonly Label _presenceValue = ValueLabel();
    private readonly Label _radminValue = ValueLabel();
    private readonly Label _updateValue = ValueLabel();
    private readonly Label _versionValue = ValueLabel();
    private readonly Label _logsValue = ValueLabel();
    private readonly Button _pauseBtn = ActionButton("Pause monitoring");
    private readonly Button _updateBtn = ActionButton("Check for updates");
    private readonly Button _dashboardBtn = ActionButton("Open dashboard");

    private readonly string _serverUrl;
    private readonly Voice.VoiceTabPanel _voiceTab;
    private bool _paused;
    private string _connection = "starting…";
    private string _radmin = "—";

    public event Action<bool>? PauseToggled;
    public event Action? UpdateCheckRequested;

    public Voice.VoiceTabPanel VoiceTab => _voiceTab;

    public AgentStatusForm(string serverUrl, AgentConfig config)
    {
        _serverUrl = serverUrl;
        _voiceTab = new Voice.VoiceTabPanel(config);

        Text = "GameNight Agent";
        Icon = AppIcon.ForWindow;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 560);
        BackColor = Bg;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        KeyPreview = true;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = HeaderBg,
        };
        var brand = new PictureBox
        {
            Image = AppIcon.ForWindow.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Location = new Point(16, 16),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(brand);
        header.Controls.Add(new Label
        {
            Text = "GameNight",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 14f),
            AutoSize = true,
            Location = new Point(66, 14),
            BackColor = Color.Transparent,
        });
        header.Controls.Add(new Label
        {
            Text = "Agent",
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(68, 40),
            BackColor = Color.Transparent,
        });

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 6),
        };
        // Dark-ish tabs via owner-draw would be overkill; system tabs are fine.

        var statusPage = new TabPage("Status")
        {
            BackColor = Bg,
            Padding = new Padding(12),
        };
        statusPage.Controls.Add(BuildStatusBody());

        var voicePage = new TabPage("Voice")
        {
            BackColor = Bg,
            Padding = new Padding(0),
        };
        voicePage.Controls.Add(_voiceTab);

        tabs.TabPages.Add(statusPage);
        tabs.TabPages.Add(voicePage);

        Controls.Add(tabs);
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

        KeyDown += async (_, e) =>
        {
            if (IsTypingTarget(ActiveControl)) return;
            var hotkey = _voiceTab.PttHotkey;
            if (hotkey is null || !hotkey.MatchesKeys(e.KeyCode)) return;
            e.SuppressKeyPress = true;
            await _voiceTab.TryHandlePttHotkeyAsync(true);
        };
        KeyUp += async (_, e) =>
        {
            var hotkey = _voiceTab.PttHotkey;
            if (hotkey is null || !hotkey.MatchesKeys(e.KeyCode)) return;
            await _voiceTab.TryHandlePttHotkeyAsync(false);
        };

        RefreshMonitoringLabel();
        ApplyStatusColors();
    }

    private static bool IsTypingTarget(Control? c) =>
        c is TextBox or ComboBox or RichTextBox;

    private Control BuildStatusBody()
    {
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Bg,
        };

        var card = new Panel
        {
            Width = 400,
            Height = 230,
            BackColor = Surface,
            Padding = new Padding(12),
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 2,
            BackColor = Surface,
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
        card.Controls.Add(grid);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Width = 400,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = Bg,
        };
        actions.Controls.Add(_pauseBtn);
        actions.Controls.Add(_updateBtn);
        actions.Controls.Add(_dashboardBtn);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            ForeColor = TextMuted,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 16, 0, 0),
            Text = "Closing this window keeps the agent running in the tray. Use the Voice tab for squad chat.",
        };

        body.Controls.Add(card);
        body.Controls.Add(actions);
        body.Controls.Add(hint);
        return body;
    }

    public void ShowOrFocus()
    {
        if (InvokeRequired) { BeginInvoke(ShowOrFocus); return; }
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void ShowVoiceTab()
    {
        ShowOrFocus();
        if (Controls.OfType<TabControl>().FirstOrDefault() is { } tabs && tabs.TabPages.Count > 1)
            tabs.SelectedIndex = 1;
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
        _connectionValue.ForeColor = connected ? Ok : Warn;
        _monitoringValue.ForeColor = _paused ? Warn : Ok;
        _radminValue.ForeColor = _radmin.StartsWith("Connected", StringComparison.Ordinal) ? Ok : Bad;
        _presenceValue.ForeColor = TextPrimary;
        _updateValue.ForeColor = TextPrimary;
        _logsValue.ForeColor = TextPrimary;
        _versionValue.ForeColor = TextPrimary;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string key, Label value)
    {
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var keyLabel = new Label
        {
            Text = key,
            AutoSize = true,
            ForeColor = TextMuted,
            BackColor = Color.Transparent,
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
        ForeColor = TextPrimary,
        BackColor = Color.Transparent,
        Anchor = AnchorStyles.Left,
    };

    private static Button ActionButton(string text) => new()
    {
        Text = text,
        Width = 150,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(48, 54, 64),
        ForeColor = TextPrimary,
        FlatAppearance = { BorderColor = Color.FromArgb(70, 78, 90), BorderSize = 1 },
    };
}
