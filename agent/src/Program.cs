// Entry point (SDD §12): single instance, link if needed, tray + status window.
using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace GameNight.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Self-update child process: must run BEFORE the single-instance mutex,
        // otherwise the still-running parent blocks us and we never swap.
        if (args.Length >= 4 && args[0] == Updater.ApplyUpdateFlag)
        {
            if (!int.TryParse(args[3], out int parentPid)) Environment.Exit(1);
            Environment.Exit(Updater.ApplyUpdate(args[1], args[2], parentPid));
            return;
        }

        // Single instance via named mutex: a second launch just exits.
        using var mutex = new Mutex(true, @"Local\GameNightAgent", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();

        var config = AgentConfig.Load();
        string? token = config.GetToken();

        if (token is null)
        {
            using var form = new LinkForm(config.ServerUrl);
            if (form.ShowDialog() != DialogResult.OK || form.Token is null) return; // user cancelled
            config.ServerUrl = form.ServerUrl;
            config.SetToken(form.Token);
            config.Save();
            token = form.Token;
        }

        RegisterAutostart();

        using var link = new ServerLink(config.ServerUrl, token);
        using var probes = new ProbeEngine();
        using var tray = new TrayIcon(config.ServerUrl, link);
        using var status = new AgentStatusForm(config.ServerUrl, config);

        // Phase 3: server sends who to probe → feed the probe engine.
        // Also keep the latest list for diagnostics (peer reachability).
        List<Peer> currentPeers = new();
        link.PeersReceived += peers => { currentPeers = peers; probes.SetPeers(peers); };
        // Phase 4: server sends toast notifications → show a Windows balloon.
        link.ToastReceived += (title, body) => tray.ShowToast(title, body);
        // Phase 5: server asks the agent to self-check → run diagnostics, report.
        link.DiagnoseRequested += () => _ = Task.Run(async () =>
        {
            var checks = await Diagnostics.RunAsync(currentPeers, config.ServerUrl);
            var dto = checks.ConvertAll(c => new DiagCheckDto(
                c.Id, c.Label, c.Status.ToString().ToLowerInvariant(), c.Detail, c.Fix));
            link.ReportDiagnostics(dto);
        });

        bool paused = false;
        void SetPaused(bool p)
        {
            paused = p;
            tray.SetPaused(p);
            status.SetPaused(p);
            if (p) link.ReportState("idle", RadminDetector.Detect());
        }

        void RequestUpdate(bool manual) =>
            _ = RunUpdateCheckAsync(config.ServerUrl, tray, status, manual);

        tray.UpdateCheckRequested += () => RequestUpdate(manual: true);
        status.UpdateCheckRequested += () => RequestUpdate(manual: true);
        tray.PauseToggled += SetPaused;
        status.PauseToggled += SetPaused;
        tray.OpenStatusRequested += status.ShowOrFocus;
        tray.OpenVoiceRequested += status.ShowVoiceTab;
        link.StatusChanged += s => status.SetConnectionStatus(s);

        using var pttHotkey = new Voice.PttHotkey();
        status.VoiceTab.PttHotkey = pttHotkey;
        pttHotkey.PttChanged += down =>
        {
            _ = status.VoiceTab.TryHandlePttHotkeyAsync(down);
        };

        link.Start();
        probes.Start();

        // Detector loop: poll game/adapters every 5s. Timers, never busy loops.
        RadminInfo radmin = RadminDetector.Detect();
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            if (paused) return;
            radmin = RadminDetector.Detect();
            string state = GameDetector.IsFarCry2Running() ? "in_game" : "online";
            link.ReportState(state, radmin);
            status.SetPresence(state, radmin);
        };

        var metricsTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        metricsTimer.Tick += (_, _) => { if (!paused) link.ReportMetrics(probes.Summarize()); };

        var updateTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        void ScheduleNextUpdateCheck(int ms)
        {
            updateTimer.Stop();
            updateTimer.Interval = ms;
            updateTimer.Start();
        }
        updateTimer.Tick += (_, _) =>
        {
            ScheduleNextUpdateCheck(6 * 60 * 60 * 1000); // 6h
            RequestUpdate(manual: false);
        };
        updateTimer.Start();

        NetworkChange.NetworkAddressChanged += (_, _) => { radmin = RadminDetector.Detect(); };
        timer.Start();
        metricsTimer.Start();
        link.ReportState("online", radmin);
        status.SetPresence("online", radmin);
        status.SetUpdateStatus("Idle — checks every 6 hours");
        status.Show(); // first-run visual feedback; close hides to tray

        // Housekeeping: trim logs older than 24h on launch, then once a day.
        _ = Task.Run(() =>
        {
            Housekeeping.Run();
            status.SetLogStatus(SummarizeLogDisk());
        });
        var houseTimer = new System.Windows.Forms.Timer { Interval = 24 * 60 * 60 * 1000 };
        houseTimer.Tick += (_, _) => _ = Task.Run(() =>
        {
            Housekeeping.Run();
            status.SetLogStatus(SummarizeLogDisk());
        });
        houseTimer.Start();

        Application.Run(); // message loop until tray → Quit
    }

    private static string SummarizeLogDisk()
    {
        try
        {
            long total = 0;
            foreach (string path in Directory.EnumerateFiles(AgentConfig.DataDir, "*.log"))
                total += new FileInfo(path).Length;
            if (total < 1024) return $"{total} B (keep 24h)";
            if (total < 1024 * 1024) return $"{total / 1024.0:0.#} KB (keep 24h)";
            return $"{total / (1024.0 * 1024.0):0.##} MB (keep 24h)";
        }
        catch { return "—"; }
    }

    private static async Task RunUpdateCheckAsync(
        string serverUrl, TrayIcon tray, AgentStatusForm status, bool manual)
    {
        UpdateResult result = await Updater.CheckAndApplyAsync(
            serverUrl,
            silentIfCurrent: !manual,
            onProgress: status.SetUpdateStatus);
        switch (result.Outcome)
        {
            case UpdateOutcome.Updated:
                status.SetUpdateStatus(result.Message);
                tray.ShowToast("GameNight update", result.Message);
                await Task.Delay(1500);
                Application.Exit();
                break;
            case UpdateOutcome.UpToDate:
                status.SetUpdateStatus(result.Message);
                if (manual) tray.ShowToast("GameNight update", result.Message);
                break;
            case UpdateOutcome.NotConfigured:
                status.SetUpdateStatus(result.Message);
                if (manual) tray.ShowToast("GameNight update", result.Message);
                break;
            case UpdateOutcome.Failed:
                status.SetUpdateStatus(result.Message);
                tray.ShowToast("GameNight update", result.Message);
                break;
            default:
            {
                UpdateOutcome unreachable = result.Outcome;
                throw new InvalidOperationException($"Unhandled update outcome: {unreachable}");
            }
        }
    }

    private static void RegisterAutostart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("GameNightAgent", $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex) { Debug.WriteLine($"[autostart] {ex.Message}"); }
    }
}
