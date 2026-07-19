// Voice tab — sleek join UI with VoIP / Radmin / Custom server path.
namespace GameNight.Agent.Voice;

public sealed class VoiceTabPanel : UserControl
{
    private static readonly Color Bg = Color.FromArgb(12, 14, 18);
    private static readonly Color Surface = Color.FromArgb(22, 26, 34);
    private static readonly Color SurfaceRaised = Color.FromArgb(30, 36, 46);
    private static readonly Color Border = Color.FromArgb(48, 56, 70);
    private static readonly Color Accent = Color.FromArgb(56, 168, 212);
    private static readonly Color TextPrimary = Color.FromArgb(236, 239, 244);
    private static readonly Color TextMuted = Color.FromArgb(148, 156, 170);
    private static readonly Color Ok = Color.FromArgb(52, 199, 140);
    private static readonly Color Bad = Color.FromArgb(232, 88, 96);
    private static readonly Color Warn = Color.FromArgb(230, 180, 80);

    private readonly AgentConfig _config;
    private readonly Panel _connectPanel = new() { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(20, 18, 20, 18) };
    private readonly Panel _roomPanel = new() { Dock = DockStyle.Fill, BackColor = Bg, Visible = false };

    private readonly TextBox _name = Field();
    private readonly ComboBox _roomChoice = ChoiceCombo(VoiceRooms.Choices);
    private readonly TextBox _customRoom = Field();
    private readonly Label _roomHint = MutedLabel(VoiceRooms.Hint, 380);

    private readonly ComboBox _serverMode = ChoiceCombo(VoiceServers.Choices);
    private readonly TextBox _customServer = Field();
    private readonly Label _serverHint = MutedLabel("", 380);

    private readonly CheckBox _ptt = SoftCheck("Push-to-talk");
    private readonly Label _pttKeyLabel = MutedLabel("Key: 2", 200);
    private readonly Button _pttBindBtn = ActionButton("Press to bind…");
    private readonly Panel _pttBindRow = new() { AutoSize = true, BackColor = Bg };
    private readonly CheckBox _shareMic = SoftCheck("Share mic with other apps");
    private bool _bindingPttKey;
    private readonly TrackBar _sensitivityConnect = SensitivityBar();
    private readonly Label _sensitivityConnectVal = ValueLabel("55");
    private readonly TrackBar _sensitivityRoom = SensitivityBar();
    private readonly Label _sensitivityRoomVal = ValueLabel("55%");
    private readonly Button _join = PrimaryButton("Join room");
    private readonly Label _error = new()
    {
        ForeColor = Bad,
        AutoSize = true,
        MaximumSize = new Size(380, 0),
        BackColor = Color.Transparent,
    };

    private readonly Label _roomTitle = new()
    {
        Font = new Font("Segoe UI Semibold", 13f),
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
        Padding = new Padding(14),
    };
    private readonly Label _localStatus = new()
    {
        ForeColor = TextMuted,
        AutoSize = true,
        BackColor = Color.Transparent,
    };
    private readonly Button _muteBtn = ActionButton("Mute");
    private readonly Button _pttBtn = ActionButton("HOLD");
    private readonly Button _leaveBtn = DangerButton("Leave");

    private VoiceEngine? _engine;
    private bool _pttMode;
    private bool _pttDown;
    private PttHotkey? _pttHotkey;
    private readonly Dictionary<string, PeerMediaLinkState> _linkStates = new(StringComparer.Ordinal);

    public PttHotkey? PttHotkey
    {
        get => _pttHotkey;
        set
        {
            _pttHotkey = value;
            if (_pttHotkey is not null)
            {
                int vk = _config.VoicePttKeyVk > 0 ? _config.VoicePttKeyVk : global::GameNight.Agent.Voice.PttHotkey.DefaultVk;
                _pttHotkey.SetBoundKey(vk);
                RefreshPttKeyUi();
            }
        }
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

        ApplyServerModeFromConfig(config);
        _ptt.Checked = config.VoicePushToTalk;
        _shareMic.Checked = config.VoiceShareMic;
        int sens = Math.Clamp(config.VoiceMicSensitivity <= 0 ? 55 : config.VoiceMicSensitivity, 1, 100);
        _sensitivityConnect.Value = sens;
        _sensitivityRoom.Value = sens;
        _sensitivityConnectVal.Text = sens.ToString();
        _sensitivityRoomVal.Text = sens + "%";
        RefreshRoomVisibility();
        RefreshServerVisibility();
        RefreshPttBindVisibility();
        RefreshPttKeyUi();

        _roomChoice.SelectedIndexChanged += (_, _) => RefreshRoomVisibility();
        _serverMode.SelectedIndexChanged += (_, _) => RefreshServerVisibility();
        _ptt.CheckedChanged += (_, _) =>
        {
            RefreshPttBindVisibility();
            RefreshPttKeyUi();
        };
        _pttBindBtn.Click += (_, _) => BeginPttBind();
        _pttBindBtn.KeyDown += OnPttBindKeyDown;
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

    private void ApplyServerModeFromConfig(AgentConfig config)
    {
        string mode = (config.VoiceServerMode ?? "voip").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "radmin":
                _serverMode.SelectedItem = VoiceServers.Radmin;
                _customServer.Text = config.VoiceServerUrl ?? VoiceServers.ProductionUrl;
                break;
            case "custom":
                _serverMode.SelectedItem = VoiceServers.Custom;
                _customServer.Text = string.IsNullOrWhiteSpace(config.VoiceServerUrl)
                    ? VoiceServers.ProductionUrl
                    : config.VoiceServerUrl;
                break;
            default:
                _serverMode.SelectedItem = VoiceServers.Voip;
                _customServer.Text = VoiceServers.ProductionUrl;
                break;
        }
    }

    private void RefreshRoomVisibility()
    {
        bool custom = string.Equals(
            _roomChoice.SelectedItem?.ToString(),
            VoiceRooms.Custom,
            StringComparison.OrdinalIgnoreCase);
        _customRoom.Visible = custom;
        _roomHint.Text = custom
            ? "Friends must use the same custom room code."
            : VoiceRooms.Hint;
    }

    private void RefreshServerVisibility()
    {
        string? mode = _serverMode.SelectedItem?.ToString();
        bool custom = VoiceServers.IsCustom(mode);
        bool radmin = VoiceServers.IsRadmin(mode);
        _customServer.Visible = custom;
        if (radmin)
        {
            _serverHint.Text = "Signaling via GameNight VoIP · media over Radmin 26.x (P2P).";
            _serverHint.ForeColor = Accent;
        }
        else if (custom)
        {
            _serverHint.Text = "Paste any Socket.IO voice signaling URL.";
            _serverHint.ForeColor = TextMuted;
        }
        else
        {
            _serverHint.Text = VoiceServers.ProductionUrl + " · both peers must pick VoIP (or both Radmin).";
            _serverHint.ForeColor = TextMuted;
        }
    }

    private void RefreshPttBindVisibility()
    {
        _pttBindRow.Visible = _ptt.Checked;
        if (!_ptt.Checked && _bindingPttKey)
            CancelPttBind();
    }

    private void RefreshPttKeyUi()
    {
        int vk = _pttHotkey?.BoundVk
            ?? (_config.VoicePttKeyVk > 0 ? _config.VoicePttKeyVk : global::GameNight.Agent.Voice.PttHotkey.DefaultVk);
        string name = !string.IsNullOrWhiteSpace(_config.VoicePttKeyName)
            ? _config.VoicePttKeyName!
            : global::GameNight.Agent.Voice.PttHotkey.DescribeVk(vk);
        _pttKeyLabel.Text = _bindingPttKey ? "Press a key… (Esc cancels)" : $"Key: {name}";
        _ptt.Text = _ptt.Checked ? $"Push-to-talk (hold {name})" : "Push-to-talk";
        _pttBindBtn.Text = _bindingPttKey ? "Listening…" : "Press to bind…";
        _pttBindBtn.BackColor = _bindingPttKey ? Warn : SurfaceRaised;
    }

    private void BeginPttBind()
    {
        if (!_ptt.Checked) return;
        _bindingPttKey = true;
        RefreshPttKeyUi();
        _pttBindBtn.Focus();
    }

    private void CancelPttBind()
    {
        _bindingPttKey = false;
        RefreshPttKeyUi();
    }

    private void OnPttBindKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_bindingPttKey) return;
        e.SuppressKeyPress = true;
        e.Handled = true;

        if (e.KeyCode == Keys.Escape)
        {
            CancelPttBind();
            return;
        }

        if (IsModifierOnly(e.KeyCode))
            return;

        int vk = (int)e.KeyCode;
        // Prefer the raw VK for digit/letter keys.
        if (e.KeyValue > 0)
            vk = e.KeyValue;

        string name = global::GameNight.Agent.Voice.PttHotkey.DescribeVk(vk);
        _config.VoicePttKeyVk = vk;
        _config.VoicePttKeyName = name;
        _config.Save();
        _pttHotkey?.SetBoundKey(vk);
        _bindingPttKey = false;
        RefreshPttKeyUi();
    }

    private static bool IsModifierOnly(Keys key) =>
        key is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin
            or Keys.None;

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

        stack.Controls.Add(Title("Voice"));
        stack.Controls.Add(MutedLabel("Pick a path, room, and join. Clean in, talk out.", 380));

        stack.Controls.Add(SectionLabel("Identity"));
        stack.Controls.Add(LabelOf("Display name"));
        stack.Controls.Add(_name);

        stack.Controls.Add(SectionLabel("Room"));
        stack.Controls.Add(LabelOf("Room code"));
        stack.Controls.Add(_roomChoice);
        stack.Controls.Add(_roomHint);
        stack.Controls.Add(_customRoom);

        stack.Controls.Add(SectionLabel("Voice server"));
        stack.Controls.Add(LabelOf("Path"));
        stack.Controls.Add(_serverMode);
        stack.Controls.Add(_serverHint);
        stack.Controls.Add(_customServer);

        stack.Controls.Add(SectionLabel("Mic"));
        stack.Controls.Add(_ptt);
        _pttBindRow.Controls.Clear();
        var bindFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Bg,
            Margin = new Padding(0, 0, 0, 4),
        };
        _pttKeyLabel.Margin = new Padding(0, 8, 8, 0);
        _pttBindBtn.Width = 140;
        _pttBindBtn.Margin = new Padding(0, 0, 0, 0);
        bindFlow.Controls.Add(_pttKeyLabel);
        bindFlow.Controls.Add(_pttBindBtn);
        _pttBindRow.Controls.Add(bindFlow);
        stack.Controls.Add(_pttBindRow);
        stack.Controls.Add(_shareMic);
        stack.Controls.Add(LabelOf("Sensitivity"));
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
        stack.Controls.Add(MutedLabel("Higher detects quieter speech for Speaking.", 380));

        stack.Controls.Add(_join);
        stack.Controls.Add(_error);
        _connectPanel.Controls.Add(stack);
    }

    private void BuildRoom()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = Surface,
            Padding = new Padding(14, 10, 14, 8),
        };
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

        var localBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 92,
            BackColor = Surface,
            Padding = new Padding(12, 8, 12, 8),
        };
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
            Text = "Sensitivity",
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

    private (string serverUrl, bool talkViaRadmin, string modeKey) ResolveServerSelection()
    {
        string? label = _serverMode.SelectedItem?.ToString();
        if (VoiceServers.IsRadmin(label))
            return (VoiceServers.ProductionUrl, true, "radmin");
        if (VoiceServers.IsCustom(label))
        {
            string custom = _customServer.Text.Trim();
            return (custom, false, "custom");
        }
        return (VoiceServers.ProductionUrl, false, "voip");
    }

    private async Task JoinAsync()
    {
        _error.Text = "";
        string name = _name.Text.Trim();
        var (server, talkViaRadmin, modeKey) = ResolveServerSelection();
        if (string.IsNullOrEmpty(server))
        {
            _error.Text = "Enter a custom voice server URL.";
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
        _config.VoiceServerMode = modeKey;
        _config.VoicePushToTalk = _ptt.Checked;
        _config.VoiceShareMic = _shareMic.Checked;
        _config.VoiceMicSensitivity = _sensitivityConnect.Value;
        if (_config.VoicePttKeyVk <= 0)
            _config.VoicePttKeyVk = global::GameNight.Agent.Voice.PttHotkey.DefaultVk;
        _config.Save();

        _join.Enabled = false;
        _join.Text = "Connecting…";
        _pttMode = _ptt.Checked;

        try
        {
            _engine?.Dispose();
            _engine = new VoiceEngine();
            WireEngine(_engine);
            await _engine.ConnectAsync(
                server,
                room,
                name,
                _pttMode,
                _sensitivityConnect.Value,
                _shareMic.Checked,
                talkViaRadmin);

            _connectPanel.Visible = false;
            _roomPanel.Visible = true;
            _roomTitle.Text = $"# {room}";
            string path = talkViaRadmin ? "Radmin P2P" : "VoIP";
            _roomSub.Text = $"connected · {path}";
            _localStatus.Text = _pttMode ? $"{name} · push-to-talk" : $"{name} · open mic";
            _muteBtn.Visible = !_pttMode;
            _pttBtn.Visible = _pttMode;
            _peerList.Controls.Clear();
            _linkStates.Clear();
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
            _join.Text = "Join room";
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
        _linkStates.Clear();
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
        engine.PeerLeft += peer => BeginInvoke(() =>
        {
            _linkStates.Remove(peer.SocketId);
            RemovePeerCard(peer.SocketId);
        });
        engine.PeerSpeaking += (peer, speaking) => BeginInvoke(() => UpsertPeerCard(peer, speaking));
        engine.PeerMediaState += (peer, state) => BeginInvoke(() =>
        {
            _linkStates[peer.SocketId] = state;
            UpsertPeerCard(peer, speaking: false, forceStatus: true);
        });
        engine.LocalSpeaking += speaking => BeginInvoke(() => SetCardSpeaking("local", speaking));
        engine.MutedChanged += muted => BeginInvoke(() =>
        {
            _muteBtn.Text = muted ? "Unmute" : "Mute";
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
        _pttBtn.Text = "Speaking";
        _localStatus.Text = $"{_engine.DisplayName} · speaking…";
        SetCardSpeaking("local", true);
    }

    private async Task StopPttAsync()
    {
        if (!_pttMode || !_pttDown || _engine is null) return;
        _pttDown = false;
        await _engine.SetPttAsync(false);
        _pttBtn.BackColor = SurfaceRaised;
        _pttBtn.Text = "HOLD";
        _localStatus.Text = $"{_engine.DisplayName} · push-to-talk";
        SetCardSpeaking("local", false);
    }

    private void AddSelfCard(string name)
    {
        _peerList.Controls.Add(PeerCard("local", name + " (you)", false));
    }

    private void UpsertPeerCard(VoicePeerInfo peer, bool speaking, bool forceStatus = false)
    {
        Control? existing = _peerList.Controls.Find("peer-" + peer.SocketId, false).FirstOrDefault();
        if (existing is not null)
        {
            if (speaking || !forceStatus)
                SetCardSpeaking(peer.SocketId, speaking);
            else
                ApplyLinkStatus(peer.SocketId);
            return;
        }
        _peerList.Controls.Add(PeerCard(peer.SocketId, peer.DisplayName, speaking));
        ApplyLinkStatus(peer.SocketId);
        int others = _peerList.Controls.Count - 1;
        string path = _engine?.TalkViaRadmin == true ? "Radmin" : "VoIP";
        _roomSub.Text = others <= 0 ? $"just you · {path}" : $"{others + 1} in room · {path}";
    }

    private void ApplyLinkStatus(string socketId)
    {
        if (socketId == "local") return;
        Control? card = _peerList.Controls.Find("peer-" + socketId, false).FirstOrDefault();
        if (card is null) return;
        if (card.Tag is true) return; // speaking wins

        PeerMediaLinkState link = _linkStates.TryGetValue(socketId, out var s)
            ? s
            : PeerMediaLinkState.Linking;
        var status = card.Controls.Find("status", false).OfType<Label>().FirstOrDefault();
        if (status is null) return;

        switch (link)
        {
            case PeerMediaLinkState.Linked:
                status.Text = "Linked";
                status.ForeColor = Ok;
                break;
            case PeerMediaLinkState.Failed:
                status.Text = "Failed";
                status.ForeColor = Bad;
                break;
            case PeerMediaLinkState.Linking:
                status.Text = "Linking…";
                status.ForeColor = Warn;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(link), link, null);
        }
    }

    private void SetCardSpeaking(string socketId, bool speaking)
    {
        Control? card = _peerList.Controls.Find("peer-" + socketId, false).FirstOrDefault();
        if (card is null) return;

        card.BackColor = speaking ? Color.FromArgb(24, 42, 36) : Surface;
        card.Tag = speaking;

        var status = card.Controls.Find("status", false).OfType<Label>().FirstOrDefault();
        if (status is not null)
        {
            if (speaking)
            {
                status.Text = "Speaking";
                status.ForeColor = Ok;
            }
            else if (socketId == "local")
            {
                status.Text = "You";
                status.ForeColor = TextMuted;
            }
            else
            {
                ApplyLinkStatus(socketId);
            }
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
        string path = _engine?.TalkViaRadmin == true ? "Radmin" : "VoIP";
        _roomSub.Text = others <= 0 ? $"just you · {path}" : $"{others + 1} in room · {path}";
    }

    private Panel PeerCard(string socketId, string name, bool speaking)
    {
        bool isLocal = socketId == "local";
        var card = new Panel
        {
            Name = "peer-" + socketId,
            Width = 380,
            Height = isLocal ? 56 : 78,
            BackColor = speaking ? Color.FromArgb(24, 42, 36) : Surface,
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
                using var pen = new Pen(Ok, 2.5f);
                e.Graphics.DrawEllipse(pen, 1, 1, 38, 38);
            }
            using var fill = new SolidBrush(glow ? Color.FromArgb(36, 86, 68) : SurfaceRaised);
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
            MaximumSize = new Size(220, 0),
        };
        var status = new Label
        {
            Name = "status",
            Text = isLocal ? "You" : (speaking ? "Speaking" : "Linking…"),
            ForeColor = speaking ? Ok : (isLocal ? TextMuted : Warn),
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
            Location = new Point(288, 8),
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
                Text = "Vol",
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
                Width = 170,
                Height = 28,
                Location = new Point(90, 42),
                BackColor = Surface,
            };
            var volVal = new Label
            {
                Name = "volumeVal",
                Text = "100%",
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(266, 48),
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
        Width = 240,
        Height = 28,
        BackColor = Surface,
        Margin = new Padding(0, 0, 8, 0),
    };

    private static Label Title(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 16f),
        ForeColor = TextPrimary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
        BackColor = Color.Transparent,
    };

    private static Label SectionLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Font = new Font("Segoe UI Semibold", 8f),
        ForeColor = Accent,
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 4),
        BackColor = Color.Transparent,
    };

    private static Label MutedLabel(string text, int maxWidth) => new()
    {
        Text = text,
        ForeColor = TextMuted,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 0, 0, 10),
        BackColor = Color.Transparent,
    };

    private static Label ValueLabel(string text) => new()
    {
        Text = text,
        ForeColor = TextMuted,
        AutoSize = true,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 4, 0, 0),
    };

    private static Label LabelOf(string text) => new()
    {
        Text = text,
        ForeColor = TextMuted,
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4),
        BackColor = Color.Transparent,
    };

    private static CheckBox SoftCheck(string text) => new()
    {
        Text = text,
        ForeColor = TextPrimary,
        AutoSize = true,
        BackColor = Bg,
        Margin = new Padding(0, 2, 0, 4),
        FlatStyle = FlatStyle.Flat,
    };

    private static TextBox Field() => new()
    {
        Width = 380,
        Height = 30,
        BackColor = Surface,
        ForeColor = TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 6),
        Font = new Font("Segoe UI", 10f),
    };

    private static ComboBox ChoiceCombo(IEnumerable<string> items)
    {
        var box = new ComboBox
        {
            Width = 380,
            Height = 30,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 0, 0, 4),
            Font = new Font("Segoe UI", 10f),
        };
        box.Items.AddRange(items.Cast<object>().ToArray());
        if (box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }

    private static Button PrimaryButton(string text) => new()
    {
        Text = text,
        Width = 380,
        Height = 38,
        FlatStyle = FlatStyle.Flat,
        BackColor = Accent,
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 10f),
        Margin = new Padding(0, 16, 0, 8),
        FlatAppearance = { BorderSize = 0 },
        Cursor = Cursors.Hand,
    };

    private static Button ActionButton(string text) => new()
    {
        Text = text,
        Width = 96,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        BackColor = SurfaceRaised,
        ForeColor = TextPrimary,
        Margin = new Padding(8, 4, 0, 0),
        FlatAppearance = { BorderColor = Border, BorderSize = 1 },
        Cursor = Cursors.Hand,
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
        Cursor = Cursors.Hand,
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
