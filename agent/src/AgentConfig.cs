// Config + token storage. The device token is encrypted with DPAPI
// (ProtectedData, CurrentUser scope): Windows itself holds the key, tied to
// this user account — the standard answer to "where do I put a local secret"
// (SDD §12). A copied config.json is useless on another machine or account.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameNight.Agent;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "https://gamenight-xbgu.onrender.com"; // overwritten at link time
    public string? TokenProtected { get; set; } // base64(DPAPI(token))

    // Voice tab (gamenight/voice-server — ADR-0012)
    public string? VoiceServerUrl { get; set; }
    /// <summary>voip | radmin | custom — selected from Voice server dropdown.</summary>
    public string VoiceServerMode { get; set; } = "voip";
    public string? VoiceDisplayName { get; set; }
    public string? VoiceLastRoom { get; set; }
    public bool VoicePushToTalk { get; set; }
    /// <summary>WASAPI shared mode so Discord / other apps can use the mic too.</summary>
    public bool VoiceShareMic { get; set; } = true;
    /// <summary>1–100; higher = more sensitive mic speaking detection.</summary>
    public int VoiceMicSensitivity { get; set; } = 55;
    /// <summary>Global PTT virtual-key code (default 0x32 = digit '2').</summary>
    public int VoicePttKeyVk { get; set; } = 0x32;
    /// <summary>Optional display label for the bound PTT key (UI).</summary>
    public string? VoicePttKeyName { get; set; }

    /// <summary>%LOCALAPPDATA%\GameNight — config, logs, pending updates.</summary>
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameNight");
    private static readonly string FilePath = Path.Combine(DataDir, "config.json");

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath)) ?? new AgentConfig();
        }
        catch { /* corrupt config = start fresh; the agent must never crash on bad disk state */ }
        return new AgentConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SetToken(string rawToken)
    {
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(rawToken), null, DataProtectionScope.CurrentUser);
        TokenProtected = Convert.ToBase64String(protectedBytes);
    }

    public string? GetToken()
    {
        if (TokenProtected is null) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch { return null; } // wrong user/machine or tampered → relink
    }
}
