// C# mirror of server/src/protocol/messages.ts (SDD §11).
// If one side changes, change the other.
using System.Text.Json.Serialization;

namespace GameNight.Agent;

public static class AgentInfo
{
    public const string Version = "0.2.0";
}

// ---- agent → server ----
public record HelloMsg([property: JsonPropertyName("t")] string T,
                       [property: JsonPropertyName("token")] string Token,
                       [property: JsonPropertyName("agentVersion")] string AgentVersion)
{
    public static HelloMsg Create(string token) => new("hello", token, AgentInfo.Version);
}

public record RadminInfo([property: JsonPropertyName("connected")] bool Connected,
                         [property: JsonPropertyName("ip")] string? Ip);

public record StateMsg([property: JsonPropertyName("t")] string T,
                       [property: JsonPropertyName("state")] string State,
                       [property: JsonPropertyName("radmin")] RadminInfo Radmin)
{
    public static StateMsg Create(string state, RadminInfo radmin) => new("state", state, radmin);
}

public record HeartbeatMsg([property: JsonPropertyName("t")] string T)
{
    public static readonly HeartbeatMsg Instance = new("hb");
}

// ---- REST: device claim ----
public record ClaimRequest([property: JsonPropertyName("code")] string Code,
                           [property: JsonPropertyName("name")] string Name);
public record ClaimResponse([property: JsonPropertyName("deviceId")] string? DeviceId,
                            [property: JsonPropertyName("token")] string? Token,
                            [property: JsonPropertyName("error")] string? Error);
