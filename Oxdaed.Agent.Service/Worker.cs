using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oxdaed.Agent.Core;
using Oxdaed.Agent.Core.Update;

namespace Oxdaed.Agent.Service;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly AgentRunner _runner;
    private readonly UpdateClient _updates;

    public Worker(ILogger<Worker> log, AgentRunner runner, UpdateClient updates)
    {
        _log = log;
        _runner = runner;
        _updates = updates;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Agent worker started");

        try
        {
            await _updates.TryUpdateOnStartup(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update check failed (ignored)");
        }

        await _runner.RunAsync(stoppingToken);
    }
}