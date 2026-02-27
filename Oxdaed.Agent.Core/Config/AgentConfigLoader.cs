using Oxdaed.Config;
using System.Text.Json;
using Oxdaed.Agent.Services;

namespace Oxdaed.Agent.Core.Config;

public static class AgentConfigLoader
{
    public static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Oxdaed",
            "config",
            "agent.json"
        );

    public static AgentConfig LoadOrCreateDefault()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

        

        if (!File.Exists(ConfigPath))
        {
            var cfg = new AgentConfig
            {
                HostUrl = "https://void-rp.ru/computer/",
                ApiBaseUrl = "https://void-rp.ru/computer//api/v1",
                PcId = AgentIdentity.GetOrCreatePcId(),
                HeartbeatEvery = TimeSpan.FromSeconds(2),
                MetricsEvery = TimeSpan.FromSeconds(2),
                ProcsEvery = TimeSpan.FromSeconds(5),
                ApiKey = ""
            };

            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            return cfg;
        }

        var text = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AgentConfig>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
    }
}