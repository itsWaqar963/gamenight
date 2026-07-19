// Preset voice signaling targets shown in the Voice tab dropdown.
namespace GameNight.Agent.Voice;

public static class VoiceServers
{
    public const string ProductionUrl = "https://voip-app-production-fc3c.up.railway.app";

    public const string Voip = "GameNight VoIP";
    public const string Radmin = "Radmin (P2P)";
    public const string Custom = "Custom…";

    public static readonly string[] Choices = [Voip, Radmin, Custom];

    public static bool IsRadmin(string? label) =>
        string.Equals(label, Radmin, StringComparison.OrdinalIgnoreCase);

    public static bool IsCustom(string? label) =>
        string.Equals(label, Custom, StringComparison.OrdinalIgnoreCase);

    public static bool IsVoip(string? label) =>
        string.Equals(label, Voip, StringComparison.OrdinalIgnoreCase);
}
