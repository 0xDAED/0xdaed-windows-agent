using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oxdaed.Agent.Api;

public sealed class AgentHeartbeatIn
{
    [JsonPropertyName("pc_id")] public string PcId { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }

    // meta -> Postgres
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("os_name")] public string OsName { get; set; } = "";
    [JsonPropertyName("os_version")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("os_build")] public string OsBuild { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("mac")] public string Mac { get; set; } = "";
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
}

public sealed class AgentHeartbeatOut
{
    [JsonPropertyName("server_ts")]
    public double ServerTs { get; set; }    

    [JsonPropertyName("commands")]
    public List<AgentCommandOut> Commands { get; set; } = new();
}

public sealed class AgentCommandOut
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    public Dictionary<string, JsonElement>? Payload { get; set; }
}

public sealed class AgentMetricsIn
{
    [JsonPropertyName("pc_id")] public string PcId { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("cpu")] public int Cpu { get; set; }
    [JsonPropertyName("ram")] public int Ram { get; set; }
    [JsonPropertyName("disk")] public int Disk { get; set; }
}

public sealed class AgentProcessesIn
{
    [JsonPropertyName("pc_id")] public string PcId { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("items")] public List<Dictionary<string, object?>> Items { get; set; } = new();
}

// отправка результата выполнения команды
public sealed class AgentCommandResultIn
{
    [JsonPropertyName("pc_id")] public string PcId { get; set; } = "";
    [JsonPropertyName("command_id")] public string CommandId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = ""; // completed/failed
    [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
    [JsonPropertyName("stdout")] public string? Stdout { get; set; }
    [JsonPropertyName("stderr")] public string? Stderr { get; set; }
    [JsonPropertyName("finished_at_ts")] public long FinishedAtTs { get; set; }
}
