// Prefer Radmin VPN (26.x) host ICE so RTP stays on the VPN mesh (true P2P).
using System.Net;
using SIPSorcery.Net;

namespace GameNight.Agent.Voice;

public static class RadminIce
{
    /// <summary>True when Windows has a live 26.x address (Radmin tunnel).</summary>
    public static bool TryGetBindAddress(out IPAddress address)
    {
        RadminInfo info = RadminDetector.Detect();
        if (info.Connected
            && !string.IsNullOrWhiteSpace(info.Ip)
            && IPAddress.TryParse(info.Ip, out IPAddress? parsed)
            && parsed is not null
            && IsRadminV4(parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    public static bool IsRadminV4(IPAddress ip) =>
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && ip.GetAddressBytes()[0] == 26;

    /// <summary>Keep only host candidates whose address is on 26.0.0.0/8.</summary>
    public static bool IsRadminHostCandidate(RTCIceCandidate? cand)
    {
        if (cand is null) return false;
        string raw = cand.candidate ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // typ host … or address field
        if (raw.Contains("typ relay", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("typ srflx", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("typ prflx", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(cand.address)
            && IPAddress.TryParse(cand.address, out IPAddress? addr)
            && addr is not null)
            return IsRadminV4(addr);

        // candidate: foundation component protocol priority ip port typ …
        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (IPAddress.TryParse(parts[i], out IPAddress? ip) && ip is not null && IsRadminV4(ip))
                return true;
        }

        return false;
    }

    public static bool IsRadminCandidateString(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Contains("typ relay", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("typ srflx", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("typ prflx", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (IPAddress.TryParse(part, out IPAddress? ip) && ip is not null && IsRadminV4(ip))
                return true;
        }

        return false;
    }
}
