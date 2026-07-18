// DTOs matching gamenight/voice-server Socket.IO signaling.
using System.Text.Json.Serialization;

namespace GameNight.Agent.Voice;

public sealed class VoicePeerInfo
{
    [JsonPropertyName("socketId")] public string SocketId { get; set; } = "";
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
}

public sealed class RoomJoinAck
{
    [JsonPropertyName("peers")] public List<VoicePeerInfo>? Peers { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class SdpPayload
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("sdp")] public string Sdp { get; set; } = "";
}

public sealed class IceCandidatePayload
{
    [JsonPropertyName("candidate")] public string? Candidate { get; set; }
    [JsonPropertyName("sdpMid")] public string? SdpMid { get; set; }
    [JsonPropertyName("sdpMLineIndex")] public int? SdpMLineIndex { get; set; }
}
