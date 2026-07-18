// Voice tab — team rooms UFLL/APR or Custom (anyone), mute / PTT, peer list.
namespace GameNight.Agent.Voice;

public sealed class VoiceTabPanel : UserControl
{
    private static readonly Color Bg = Color.FromArgb(15, 17, 23);
    private static readonly Color Surface = Color.FromArgb(26, 29, 39);
    private static readonly Color Accent = Color.FromArgb(88, 101, 242);
    private static readonly Color TextPrimary = Color.FromArgb(227, 229, 234);
    private static readonly Color TextMuted = Color.FromArgb(142, 146, 151);
    private static readonly Color Ok = Color.FromArgb(35, 209, 139);
    private static readonly Color Bad = Color.FromArgb(237, 66, 69);

    private readonly AgentConfig _config;
    private readonly Panel _connectPanel = new() { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(16) };
    private readonly Panel _roomPanel = new() { Dock = DockStyle.Fill, BackColor = Bg, Visible = false };

    private readonly TextBox _name = Field();
    private readonly ComboBox _roomChoice = RoomChoiceCombo();
    private readonly TextBox _customRoom = Field();
    private readonly Label _roomHint = new()
    {
        Text = VoiceRooms.Hint,
        ForeColor = TextMuted,
        AutoSize = true,
        MaximumSize = new Size(360, 0),
        Margin = new Padding(0, 2, 0, 8),
        BackColor = Color.Transparent,
    };
    private readonly TextBox _server = Field();
    private readonly CheckBox _ptt = new()
    {
        Text = "Push-to-talk (hold 2)",
        ForeColor = TextPrimary,
        AutoSize = true,
        BackColor = Bg,
    };
    private readonly TrackBar _sensitivityConnect = SensitivityBar();
    private readonly Label _sensitivityConnectVal = new()
    {
        ForeColor = TextMuted,
        AutoSize = true,
        BackColor = Color.Transparent,
        Text = "55",
    };
    private readonly TrackBar _sensitivityRoom = SensitivityBar();
    private readonly Label _sensitivityRoomVal = new()
    {
        ForeColor = TextMuted,
        AutoSize = true,
        Width = 36,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.Transparent,
        Text = "55%",
    };
    private readonly Button _join = PrimaryButton("Join");
    private readonly Label _error = new()
    {
        ForeColor = Bad,
        AutoSize = true,
        MaximumSize = new Size(360, 0),
        BackColor = Color.Transparent,
    };

    private readonly Label _roomTitle = new()
    {
        Font = new Font("Segoe UI Semibold", 12f),
        ForeColor = TextPrimary,
        AutoSize = true,
        BackColor = Color.Transparent,
    };
    private readonly Label _roomSub = new()
    {
        ForeColor = TextMuted,
        AutoSize = true,
        BackColor = Color.Transparent,
        Text = "connecting…",
    };
    private readonly FlowLayoutPanel _peerList = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Bg,
        Padding = new Padding(12),
    };
    private readonly Label _localStatus = new()
    {
        ForeColor = TextMuted,
        AutoSize = true,
        BackColor = Color.Transparent,
    };
    private readonly Button _muteBtn = ActionButton("🎙 Mute");
    private readonly Button _pttBtn = ActionButton("🎙 HOLD");
    private readonly Button _leaveBtn = DangerButton("Leave");

    private VoiceEngine? _engine;
    private bool _pttMode;
    private bool _pttDown;
    private PttHotkey? _pttHotkey;

    /// <summary>Optional global hold-2 listener (non-consuming). Wired from Program.</summary>
    public PttHotkey? PttHotkey
    {
        get => _pttHotkey;
        set => _pttHotkey = value;
    }

    public VoiceTabPanel(AgentConfig config)
    {
        _config = config;
        Dock = DockStyle.Fill;
        BackColor = Bg;

        BuildConnect();
        BuildRoom();
        Controls.Add(_roomPanel);
        Controls.Add(_connectPanel);

        _name.Text = config.VoiceDisplayName ?? Environment.UserName;
        string last = config.VoiceLastRoom ?? VoiceRooms.Ufll;
        if (VoiceRooms.IsTeamRoom(last))
        {
            _roomChoice.SelectedItem = last.Trim().ToUpperInvariant();
            _customRoom.Visible = false;
        }
        else
        {
            _roomChoice.SelectedItem = VoiceRooms.Custom;
            _customRoom.Text = last;
            _customRoom.Visible = true;
        }
        _server.Text = config.VoiceServerUrl ?? "http://127.0.0.1:3001";
        _ptt.Checked = config.VoicePushToTalk;
        int sens = Math.Clamp(config.VoiceMicSensitivity <= 0 ? 55 : config.VoiceMicSensitivity, 1, 100);
        _sensitivityConnect.Value = sens;
        _sensitivityRoom.Value = sens;
        _sensitivityConnectVal.Text = sens.ToString();
        _sensitivityRoomVal.Text = sens + "%";
        RefreshCustomVisibility();

        _roomChoice.SelectedIndexChanged += (_, _) => RefreshCustomVisibility();
        _sensitivityConnect.ValueChanged += (_, _) =>
        {
            _sensitivityConnectVal.Text = _sensitivityConnect.Value.ToString();
            _sensitivityRoom.Value = _sensitivityConnect.Value;
        };
        _sensitivityRoom.ValueChanged += (_, _) =>
        {
            _sensitivityRoomVal.Text = _sensitivityRoom.Value + "%";
            _sensitivityConnect.Value = _sensitivityRoom.Value;
            _config.VoiceMicSensitivity = _sensitivityRoom.Value;
            _config.Save();
            _engine?.SetMicSensitivity(_sensitivityRoom.Value);
        };
        _join.Click += async (_, _) => await JoinAsync();
        _leaveBtn.Click += async (_, _) => await LeaveAsync();
        _muteBtn.Click += async (_, _) =>
        {
            if (_engine is not null) await _engine.ToggleMuteAsync();
        };
        _pttBtn.MouseDown += async (_, _) => await StartPttAsync();
        _pttBtn.MouseUp += async (_, _) => await StopPttAsync();
        _pttBtn.MouseLeave += async (_, _) => await StopPttAsync();
    }

    private void RefreshCustomVisibility()
    {
        bool custom = string.Equals(
            _roomChoice.SelectedItem?.ToString(),
            VoiceRooms.Custom,
            StringComparison.OrdinalIgnoreCase);
        _customRoom.Visible = custom;
        _roomHint.Text = custom
            ? "Enter any room code (friends must use the same Custom code)."
            : VoiceRooms.Hint;
    }

    public async Task TryHandlePttHotkeyAsync(bool down)
    {
        if (!_pttMode || _engine is null) return;
        if (ContainsFocus && IsTypingTarget(FindForm()?.ActiveControl))
            return;
        if (down) await StartPttAsync();
        else await StopPttAsync();
    }

    private static bool IsTypingTarget(Control? c) =>
        c is TextBox or ComboBox or RichTextBox;

    private void BuildConnect()
    {
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Bg,
        };
        stack.Controls.Add(Title("Join a Room"));
        stack.Controls.Add(Hint("Voice signaling server — pick UFLL, APR, or Custom."));
        stack.Controls.Add(LabelOf("Your name"));
        stack.Controls.Add(_name);
        stack.Controls.Add(LabelOf("Room code"));
        stack.Controls.Add(_roomChoice);
        stack.Controls.Add(_roomHint);
        stack.Controls.Add(_customRoom);
        stack.Controls.Add(LabelOf("Voice server URL"));
        stack.Controls.Add(_server);
        stack.Controls.Add(_ptt);
        stack.Controls.Add(LabelOf("Mic sensitivity"));
        var sensRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Bg,
            Margin = new Padding(0, 0, 0, 4),
        };
        sensRow.Controls.Add(_sensitivityConnect);
        sensRow.Controls.Add(_sensitivityConnectVal);
        stack.Controls.Add(sensRow);
        stack.Controls.Add(Hint("Higher sensitivity detects quieter speech for the Speaking indicator."));
        stack.Controls.Add(_join);
        stack.Controls.Add(_error);
        _connectPanel.Controls.Add(stack);
    }

    private void BuildRoom()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Surface, Padding = new Padding(12, 10, 12, 8) };
        var headerStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Surface,
        };
        headerStack.Controls.Add(_roomTitle);
        headerStack.Controls.Add(_roomSub);
        header.Controls.Add(headerStack);

        var localBar = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = Surface, Padding = new Padding(10, 8, 10, 8) };
        var col = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface,
        };
        col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        col.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Surface,
        };
        actions.Controls.Add(_localStatus);
        actions.Controls.Add(_muteBtn);
        actions.Controls.Add(_pttBtn);
        actions.Controls.Add(_leaveBtn);

        var sensRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Surface,
            Margin = new Padding(0, 6, 0, 0),
        };
        sensRow.Controls.Add(new Label
        {
            Text = "Mic sensitivity",
            ForeColor = TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0),
            BackColor = Color.Transparent,
        });
        sensRow.Controls.Add(_sensitivityRoom);
        sensRow.Controls.Add(_sensitivityRoomVal);

        col.Controls.Add(actions, 0, 0);
        col.Controls.Add(sensRow, 0, 1);
        localBar.Controls.Add(col);

        _roomPanel.Controls.Add(_peerList);
        _roomPanel.Controls.Add(localBar);
        _roomPanel.Controls.Add(header);
    }

    private async Task JoinAsync()
    {
        _error.Text = "";
        string name = _name.Text.Trim();
        string server = _server.Text.Trim();
        if (string.IsNullOrEmpty(server))
        {
            _error.Text = "Please enter a voice server URL.";
            return;
        }

        string choice = _roomChoice.SelectedItem?.ToString() ?? VoiceRooms.Ufll;
        string roomRaw = string.Equals(choice, VoiceRooms.Custom, StringComparison.OrdinalIgnoreCase)
            ? _customRoom.Text
            : choice;

        if (string.Equals(choice, VoiceRooms.Custom, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(_customRoom.Text))
        {
            _error.Text = "Enter a custom room code, or pick UFLL / APR.";
            return;
        }

        if (!VoiceRooms.TryNormalize(roomRaw, out string room, out string? roomErr))
        {
            _error.Text = roomErr ?? VoiceRooms.Hint;
            return;
        }

        _config.VoiceDisplayName = name;
        _config.VoiceLastRoom = room;
        _config.VoiceServerUrl = server;
        _config.VoicePushToTalk = _ptt.Checked;
        _config.VoiceMicSensitivity = _sensitivityConnect.Value;
        _config.Save();

        _join.Enabled = false;
        _join.Text = "Connecting…";
        _pttMode = _ptt.Checked;

        try
        {
            _engine?.Dispose();
            _engine = new VoiceEngine();
            WireEngine(_engine);
            await _engine.ConnectAsync(server, room, name, _pttMode, _sensitivityConnect.Value);

            _connectPanel.Visible = false;
            _roomPanel.Visible = true;
            _roomTitle.Text = $"# {room}";
            _roomSub.Text = "connected";
            _localStatus.Text = _pttMode ? $"{name} · push-to-talk" : $"{name} · open mic";
            _muteBtn.Visible = !_pttMode;
            _pttBtn.Visible = _pttMode;
            _peerList.Controls.Clear();
            AddSelfCard(name);
            _pttHotkey?.SetListening(_pttMode);
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
            _engine?.Dispose();
            _engine = null;
            _pttHotkey?.SetListening(false);
        }
        finally
        {
            _join.Enabled = true;
            _join.Text = "Join";
        }
    }

    private async Task LeaveAsync()
    {
        _leaveBtn.Enabled = false;

        var engine = _engine;
        _engine = null;
        _pttDown = false;
        _pttMode = false;
        _pttHotkey?.SetListening(false);
        _peerList.Controls.Clear();
        _roomPanel.Visible = false;
        _connectPanel.Visible = true;
        _roomSub.Text = "just you";

        if (engine is not null)
        {
            try
            {
                await Task.Run(async () =>
                {
                    await engine.DisconnectAsync().ConfigureAwait(false);
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AgentLog.Write("voice.log", $"leave UI: {ex.Message}");
            }
            finally
            {
                engine.Dispose();
            }
        }

        _leaveBtn.Enabled = true;
    }

    private void WireEngine(VoiceEngine engine)
    {
        engine.PeerJoined += peer => BeginInvoke(() => UpsertPeerCard(peer, speaking: false));
        engine.PeerLeft += peer => BeginInvoke(() => RemovePeerCard(peer.SocketId));
        engine.PeerSpeaking += (peer, speaking) => BeginInvoke(() => UpsertPeerCard(peer, speaking));
        engine.LocalSpeaking += speaking => BeginInvoke(() => SetCardSpeaking("local", speaking));
        engine.MutedChanged += muted => BeginInvoke(() =>
        {
            _muteBtn.Text = muted ? "🔇 Unmute" : "🎙 Mute";
            if (!_pttMode)
                _localStatus.Text = muted ? $"{engine.DisplayName} · muted" : $"{engine.DisplayName} · open mic";
            if (muted) SetCardSpeaking("local", false);
        });
        engine.Error += msg => BeginInvoke(() => _roomSub.Text = msg);
    }

    private async Task StartPttAsync()
    {
        if (!_pttMode || _pttDown || _engine is null) return;
        _pttDown = true;
        await _engine.SetPttAsync(true);
        _pttBtn.BackColor = Ok;
        _pttBtn.Text = "🎙 Speaking";
        _localStatus.Text = $"{_engine.DisplayName} · speaking…";
        SetCardSpeaking("local", true);
    }

    private async Task StopPttAsync()
    {
        if (!_pttMode || !_pttDown || _engine is null) return;
        _pttDown = false;
        await _engine.SetPttAsync(false);
        _pttBtn.BackColor = Color.FromArgb(48, 54, 64);
        _pttBtn.Text = "🎙 HOLD";
        _localStatus.Text = $"{_engine.DisplayName} · push-to-talk";
        SetCardSpeaking("local", false);
    }

    private void AddSelfCard(string name)
    {
        _peerList.Controls.Add(PeerCard("local", name + " (you)", false));
    }

    private void UpsertPeerCard(VoicePeerInfo peer, bool speaking)
    {
        Control? existing = _peerList.Controls.Find("peer-" + peer.SocketId, false).FirstOrDefault();
        if (existing is not null)
        {
            SetCardSpeaking(peer.SocketId, speaking);
            return;
        }
        _peerList.Controls.Add(PeerCard(peer.SocketId, peer.DisplayName, speaking));
        int others = _peerList.Controls.Count - 1;
        _roomSub.Text = others <= 0 ? "just you" : $"{others + 1} in room";
    }

    private void SetCardSpeaking(string socketId, bool speaking)
    {
        Control? card = _peerList.Controls.Find("peer-" + socketId, false).FirstOrDefault();
        if (card is null) return;

        card.BackColor = speaking ? Color.FromArgb(28, 48, 38) : Surface;
        card.Tag = speaking;

        var status = card.Controls.Find("status", false).OfType<Label>().FirstOrDefault();
        if (status is not null)
        {
            status.Text = speaking ? "Speaking" : "Connected";
            status.ForeColor = speaking ? Ok : TextMuted;
        }

        var badge = card.Controls.Find("badge", false).OfType<Label>().FirstOrDefault();
        if (badge is not null)
        {
            badge.Text = speaking ? "Speaking" : "";
            badge.Visible = speaking;
        }

        var avatar = card.Controls.Find("avatar", false).FirstOrDefault();
        avatar?.Invalidate();
    }

    private void RemovePeerCard(string socketId)
    {
        Control? existing = _peerList.Controls.Find("peer-" + socketId, false).FirstOrDefault();
        existing?.Dispose();
        int others = Math.Max(0, _peerList.Controls.Count - 1);
        _roomSub.Text = others <= 0 ? "just you" : $"{others + 1} in room";
    }

    private Panel PeerCard(string socketId, string name, bool speaking)
    {
        bool isLocal = socketId == "local";
        var card = new Panel
        {
            Name = "peer-" + socketId,
            Width = 360,
            Height = isLocal ? 56 : 78,
            BackColor = speaking ? Color.FromArgb(28, 48, 38) : Surface,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(8),
            Tag = speaking,
        };

        var avatar = new Panel
        {
            Name = "avatar",
            Size = new Size(40, 40),
            Location = new Point(8, 8),
            BackColor = Color.Transparent,
        };
        string initials = Initials(name);
        avatar.Paint += (_, e) =>
        {
            bool glow = card.Tag is true;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(2, 2, 36, 36);
            if (glow)
            {
                using var pen = new Pen(Ok, 3f);
                e.Graphics.DrawEllipse(pen, 1, 1, 38, 38);
                using var brushGlow = new SolidBrush(Color.FromArgb(55, Ok));
                e.Graphics.FillEllipse(brushGlow, 0, 0, 40, 40);
            }
            using var fill = new SolidBrush(glow ? Color.FromArgb(40, 90, 65) : Color.FromArgb(48, 54, 72));
            e.Graphics.FillEllipse(fill, rect);
            using var font = new Font("Segoe UI Semibold", 10f);
            var size = e.Graphics.MeasureString(initials, font);
            e.Graphics.DrawString(
                initials,
                font,
                Brushes.White,
                2 + (36 - size.Width) / 2,
                2 + (36 - size.Height) / 2);
        };

        var nameLabel = new Label
        {
            Text = name,
            ForeColor = TextPrimary,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10f),
            Location = new Point(56, 6),
            BackColor = Color.Transparent,
            MaximumSize = new Size(200, 0),
        };
        var status = new Label
        {
            Name = "status",
            Text = speaking ? "Speaking" : "Connected",
            ForeColor = speaking ? Ok : TextMuted,
            AutoSize = true,
            Location = new Point(56, 28),
            BackColor = Color.Transparent,
        };
        var badge = new Label
        {
            Name = "badge",
            Text = speaking ? "Speaking" : "",
            Visible = speaking,
            ForeColor = Color.White,
            BackColor = Ok,
            Font = new Font("Segoe UI Semibold", 8f),
            AutoSize = true,
            Location = new Point(268, 8),
            Padding = new Padding(6, 2, 6, 2),
        };

        card.Controls.Add(avatar);
        card.Controls.Add(nameLabel);
        card.Controls.Add(status);
        card.Controls.Add(badge);

        if (!isLocal)
        {
            var volLabel = new Label
            {
                Text = "Volume",
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(56, 48),
                BackColor = Color.Transparent,
            };
            var vol = new TrackBar
            {
                Name = "volume",
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None,
                Value = 100,
                Width = 160,
                Height = 28,
                Location = new Point(110, 42),
                BackColor = Surface,
            };
            var volVal = new Label
            {
                Name = "volumeVal",
                Text = "100%",
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(274, 48),
                BackColor = Color.Transparent,
            };
            vol.ValueChanged += (_, _) =>
            {
                volVal.Text = vol.Value + "%";
                _engine?.SetPeerVolume(socketId, vol.Value / 100f);
            };
            card.Controls.Add(volLabel);
            card.Controls.Add(vol);
            card.Controls.Add(volVal);
        }

        var pulse = new System.Windows.Forms.Timer { Interval = 450 };
        pulse.Tick += (_, _) =>
        {
            if (card.IsDisposed) { pulse.Stop(); pulse.Dispose(); return; }
            if (card.Tag is true) avatar.Invalidate();
        };
        pulse.Start();
        card.Disposed += (_, _) => { pulse.Stop(); pulse.Dispose(); };

        return card;
    }

    private static string Initials(string name)
    {
        var parts = name.Replace("(you)", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
    }

    private static TrackBar SensitivityBar() => new()
    {
        Minimum = 1,
        Maximum = 100,
        Value = 55,
        TickStyle = TickStyle.None,
        Width = 220,
        Height = 28,
        BackColor = Surface,
        Margin = new Padding(0, 0, 8, 0),
    };

    private static Label Title(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = TextPrimary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8),
        BackColor = Color.Transparent,
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        ForeColor = TextMuted,
        AutoSize = true,
        MaximumSize = new Size(360, 0),
        Margin = new Padding(0, 0, 0, 12),
        BackColor = Color.Transparent,
    };

    private static Label LabelOf(string text) => new()
    {
        Text = text,
        ForeColor = TextMuted,
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 4),
        BackColor = Color.Transparent,
    };

    private static TextBox Field() => new()
    {
        Width = 360,
        Height = 28,
        BackColor = Surface,
        ForeColor = TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 4),
    };

    private static ComboBox RoomChoiceCombo()
    {
        var box = new ComboBox
        {
            Width = 360,
            Height = 28,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 0, 0, 2),
        };
        box.Items.AddRange(VoiceRooms.Choices.Cast<object>().ToArray());
        box.SelectedItem = VoiceRooms.Ufll;
        return box;
    }

    private static Button PrimaryButton(string text) => new()
    {
        Text = text,
        Width = 360,
        Height = 36,
        FlatStyle = FlatStyle.Flat,
        BackColor = Accent,
        ForeColor = Color.White,
        Margin = new Padding(0, 16, 0, 8),
        FlatAppearance = { BorderSize = 0 },
    };

    private static Button ActionButton(string text) => new()
    {
        Text = text,
        Width = 100,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(48, 54, 64),
        ForeColor = TextPrimary,
        Margin = new Padding(8, 4, 0, 0),
        FlatAppearance = { BorderColor = Color.FromArgb(70, 78, 90), BorderSize = 1 },
    };

    private static Button DangerButton(string text) => new()
    {
        Text = text,
        Width = 80,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        BackColor = Bad,
        ForeColor = Color.White,
        Margin = new Padding(8, 4, 0, 0),
        FlatAppearance = { BorderSize = 0 },
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine?.Dispose();
            _engine = null;
        }
        base.Dispose(disposing);
    }
}
