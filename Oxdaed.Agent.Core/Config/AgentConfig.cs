namespace Oxdaed.Config;

public sealed class AgentConfig
{

    public string HostUrl { get; set; } = "https://void-rp.ru/computer/";
    public string ApiBaseUrl { get; set; } = "https://void-rp.ru/computer/api/v1";

    public Guid PcId { get; set; }

    public TimeSpan HeartbeatEvery { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan MetricsEvery { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ProcsEvery { get; set; } = TimeSpan.FromSeconds(5);

    public string? ApiKey { get; set; }

    public string HeartbeatUrl => $"{ApiBaseUrl}/agent/heartbeat";
    public string MetricsUrl => $"{ApiBaseUrl}/agent/metrics";
    public string ProcessesUrl => $"{ApiBaseUrl}/agent/processes";

    public string CommandAckUrl => $"{ApiBaseUrl}/agent/command_ack";
    public string CommandResultUrl => $"{ApiBaseUrl}/agent/command_result";
}
