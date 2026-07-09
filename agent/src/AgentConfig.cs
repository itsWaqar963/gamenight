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

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameNight");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

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
        Directory.CreateDirectory(Dir);
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
