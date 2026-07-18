// Voice room codes: team presets UFLL / APR, or Custom (any code, everyone).
namespace GameNight.Agent.Voice;

public static class VoiceRooms
{
    public const string Ufll = "UFLL";
    public const string Apr = "APR";
    public const string Custom = "Custom";

    public static readonly string[] Choices = [Ufll, Apr, Custom];

    public const string Hint = "Select your Team as Room Code (UFLL or APR), or Custom";

    public static bool IsTeamRoom(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        string key = roomId.Trim().ToUpperInvariant();
        return key == Ufll || key == Apr;
    }

    /// <summary>
    /// Team picks → uppercase UFLL/APR. Custom → trimmed user code (1–32 chars).
    /// </summary>
    public static bool TryNormalize(string? roomId, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(roomId))
        {
            error = Hint;
            return false;
        }

        string trimmed = roomId.Trim();
        string upper = trimmed.ToUpperInvariant();

        if (upper is Ufll or Apr)
        {
            normalized = upper;
            return true;
        }

        if (trimmed.Length > 32)
        {
            error = "Room code max length is 32.";
            return false;
        }

        normalized = trimmed;
        return true;
    }
}
