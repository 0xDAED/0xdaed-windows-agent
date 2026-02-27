using Microsoft.Extensions.Options;
using Oxdaed.Agent.Api;
using Oxdaed.Agent.Core;
using Oxdaed.Agent.Core.Update;
using Oxdaed.Agent.Service;
using Oxdaed.Agent.Services;
using Oxdaed.Config;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "OxdaedAgent");

builder.Services.AddSingleton(new AgentConfig
{
    HostUrl = "https://void-rp.ru/computer/",
    ApiBaseUrl = "https://void-rp.ru/computer/api/v1",
    PcId = AgentIdentity.GetOrCreatePcId(),
    HeartbeatEvery = TimeSpan.FromSeconds(2),
    MetricsEvery = TimeSpan.FromSeconds(2),
    ProcsEvery = TimeSpan.FromSeconds(5),
    ApiKey = ""
});

// зависимости
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<AgentConfig>();
    return new AgentApiClient(cfg.ApiKey);
});

builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<UpdateClient>();

builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();