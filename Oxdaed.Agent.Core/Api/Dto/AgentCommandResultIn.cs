using System.Text.Json.Serialization;

namespace Oxdaed.Agent.Api.Dto;

public sealed class AgentCommandResultIn
{
    [JsonPropertyName("pc_id")]
    public string PcId { get; set; } = "";

    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "completed";

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; } = 0;

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; set; }

    [JsonPropertyName("finished_at_ts")]
    public long? FinishedAtTs { get; set; }
}
