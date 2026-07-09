// Entry point (SDD §12): single instance, link if needed, tray + detectors.
using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace GameNight.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
        using var tray = new TrayIcon(config.ServerUrl, link);
        link.Start();

        // Detector loop: poll game every 5s, adapters every 30s (+ instantly
        // on OS network-change events). Timers, never busy loops (SDD §12).
        bool paused = false;
        tray.PauseToggled += p => { paused = p; if (p) link.ReportState("idle", RadminDetector.Detect()); };

        RadminInfo radmin = RadminDetector.Detect();
        int tick = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            if (paused) return;
            if (tick++ % 6 == 0) radmin = RadminDetector.Detect(); // every 30s
            string state = GameDetector.IsFarCry2Running() ? "in_game" : "online";
            link.ReportState(state, radmin);
        };
        NetworkChange.NetworkAddressChanged += (_, _) => { radmin = RadminDetector.Detect(); };
        timer.Start();
        link.ReportState("online", radmin);

        Application.Run(); // message loop until tray → Quit
    }

    private static void RegisterAutostart()
    {
        // HKCU Run key: per-user autostart, no admin, removable in Task Manager
        // → Startup apps. A Windows *Service* was rejected in SDD §12.
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("GameNightAgent", $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex) { Debug.WriteLine($"[autostart] {ex.Message}"); }
    }
}
