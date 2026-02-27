using Oxdaed.Agent.Api;
using Oxdaed.Agent.Api.Dto;
using Oxdaed.Agent.SystemInfo;
using Oxdaed.Config;
using System.Diagnostics;

namespace Oxdaed.Agent.Core;

public sealed class AgentRunner
{
    private readonly AgentConfig _cfg;
    private readonly AgentApiClient _api;
    private readonly CommandDispatcher _dispatcher = new();

    private long _hbSeq, _metSeq, _procSeq;

    public AgentRunner(AgentConfig cfg, AgentApiClient api)
    {
        _cfg = cfg;
        _api = api;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await PingOpenApi(ct);

        _ = RunLoop("heartbeat", _cfg.HeartbeatEvery, () => SendHeartbeat(ct), ct);
        _ = RunLoop("metrics", _cfg.MetricsEvery, () => SendMetrics(ct), ct);
        _ = RunLoop("processes", _cfg.ProcsEvery, () => SendProcesses(ct), ct);

        // держим сервис живым
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task PingOpenApi(CancellationToken ct)
    {
        try
        {
            var txt = await _api.GetText($"{_cfg.HostUrl}/openapi.json", ct);
            Console.WriteLine($"OPENAPI OK ({txt.Length} bytes)\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OPENAPI CHECK FAILED: {ex.Message}\n");
        }
    }

    private static async Task RunLoop(string name, TimeSpan every, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200, 800)), ct);

        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try { await action(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {name} ERROR: {ex.Message}"); }

            var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
            var delay = every - elapsed;
            if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
            await Task.Delay(delay, ct);
        }
    }

    private async Task SendHeartbeat(CancellationToken ct)
    {
        var (ip, mac) = NetInfo.GetIpAndMac();

        var hb = new AgentHeartbeatIn
        {
            PcId = _cfg.PcId.ToString(),
            Seq = Interlocked.Increment(ref _hbSeq),
            Hostname = Environment.MachineName,
            OsName = "Windows",
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsBuild = Environment.OSVersion.Version.Build.ToString(),
            Username = Environment.UserName,
            Ip = ip ?? "",
            Mac = mac ?? "",
            AgentVersion = "0.1.0"
        };

        var outp = await _api.PostJson<AgentHeartbeatIn, AgentHeartbeatOut>(_cfg.HeartbeatUrl, hb, ct);

        if (outp?.Commands is not { Count: > 0 })
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] heartbeat OK");
            return;
        }

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] heartbeat OK | commands: {outp?.Commands.Count}");

        foreach (var cmd in outp.Commands)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // 1) ACK
                    await _api.PostJson(_cfg.CommandAckUrl, new AgentCommandAckIn
                    {
                        PcId = _cfg.PcId.ToString(),
                        CommandId = cmd.Id,
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }, ct);

                    // 2) EXEC
                    var result = await _dispatcher.ExecuteAsync(cmd, ct);




                    // 3) RESULT
                    await SendCommandResult(cmd.Id, result, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] cmd {cmd.Id} ERROR: {ex.Message}");
                }
            }, ct);
        }
    }

    private async Task SendCommandResult(string commandId, CommandResult res, CancellationToken ct)
    {
        var payload = new Api.Dto.AgentCommandResultIn
        {
            PcId = _cfg.PcId.ToString(),
            CommandId = commandId,
            Status = res.Ok ? "completed" : "failed",
            ExitCode = res.ExitCode,
            Stdout = Trunc(res.Stdout, 6000),
            Stderr = Trunc(res.Stderr, 6000),
            FinishedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await _api.PostJson(_cfg.CommandResultUrl, payload, ct);
    }

    private async Task SendMetrics(CancellationToken ct)
    {
        var cpu = Metrics.TryGetCpuPercent();
        var ram = Metrics.GetRamPercent();
        var disk = Metrics.GetDiskPercent("C:\\");

        var met = new AgentMetricsIn
        {
            PcId = _cfg.PcId.ToString(),
            Seq = Interlocked.Increment(ref _metSeq),
            Cpu = Clamp(cpu),
            Ram = Clamp(ram),
            Disk = Clamp(disk)
        };

        await _api.PostJson(_cfg.MetricsUrl, met, ct);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] metrics OK | cpu={met.Cpu} ram={met.Ram} disk={met.Disk}");
    }

    private async Task SendProcesses(CancellationToken ct)
    {
        var items = ProcessSnapshot.Take(300);

        var procs = new AgentProcessesIn
        {
            PcId = _cfg.PcId.ToString(),
            Seq = Interlocked.Increment(ref _procSeq),
            Items = items
        };

        await _api.PostJson(_cfg.ProcessesUrl, procs, ct);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] processes OK | items={items.Count}");
    }

    private static int Clamp(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        if (v < 0) return 0;
        if (v > 100) return 100;
        return (int)Math.Round(v);
    }

    private static string? Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
