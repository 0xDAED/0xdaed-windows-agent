using System.Text.Json.Serialization;

namespace Oxdaed.Agent.Api.Dto;

public sealed class AgentCommandAckIn
{
    [JsonPropertyName("pc_id")]
    public string PcId { get; set; } = "";

    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = "";

    // optional
    [JsonPropertyName("ts")]
    public long? Ts { get; set; }
}
