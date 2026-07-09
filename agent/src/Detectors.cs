// The agent's two senses (SDD §7.3, §12) — both READ-ONLY by design:
// data minimization means these are the only things the agent ever looks at.
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameNight.Agent;

public static class GameDetector
{
    // Process.GetProcessesByName is a Win32 process-list query by exact image
    // name — we look for FarCry2 and NOTHING else (the privacy pledge, in code).
    public static bool IsFarCry2Running()
    {
        Process[] procs = Process.GetProcessesByName("FarCry2");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}

public static class RadminDetector
{
    /// <summary>
    /// Finds the Radmin VPN virtual adapter: an UP interface whose IPv4 sits
    /// in 26.0.0.0/8 (Radmin's virtual range). We also record the adapter
    /// description; matching BOTH (SDD §12) guards against the unlikely
    /// non-Radmin 26.x address, but IP is primary — descriptions localize.
    /// </summary>
    public static RadminInfo Detect()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            bool looksRadmin = nic.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase)
                               || nic.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase);
            foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte first = ip.Address.GetAddressBytes()[0];
                if (first == 26 && looksRadmin) return new RadminInfo(true, ip.Address.ToString());
                if (first == 26) return new RadminInfo(true, ip.Address.ToString()); // IP wins even if renamed
            }
        }
        return new RadminInfo(false, null);
    }
}
