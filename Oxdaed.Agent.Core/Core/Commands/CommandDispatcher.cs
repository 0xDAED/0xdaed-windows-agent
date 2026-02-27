using Oxdaed.Agent.Api;
using System.Text.Json;

namespace Oxdaed.Agent.Core;

public sealed class CommandDispatcher
{
    public async Task<CommandResult> ExecuteAsync(AgentCommandOut cmd, CancellationToken ct)
    {
        var type = (cmd.Type ?? "").Trim().ToUpperInvariant();
        var payload = cmd.Payload;

        switch (type)
        {
            case "RUN_SHELL":
                {
                    var script = GetString(payload, "params") ?? "";
                    var timeout = GetInt(payload, "timeout_sec") ?? 60;
                    return await CommandHandlers.RunShellPowerShellAsync(script, timeout, ct);
                }

            case "REBOOT":
                return await CommandHandlers.RebootAsync();

            case "SHUTDOWN":
                return await CommandHandlers.ShutdownAsync();

            case "SLEEP":
                return await CommandHandlers.SleepAsync();

            case "KILL_PROCESS":
                {
                    var pid = GetInt(payload, "pid") ?? 0;
                    return await CommandHandlers.KillProcessAsync(pid);
                }

            case "BLOCK_PROCESS_NAME":
                {
                    var name = GetString(payload, "name") ?? "";
                    return await CommandHandlers.BlockProcessNameAsync(name, ct);
                }

            case "UNBLOCK_PROCESS_NAME":
                {
                    var name = GetString(payload, "name") ?? "";
                    return await CommandHandlers.UnblockProcessNameAsync(name, ct);
                }

            // request processes НЕ должен “просто вернуть текст”
            // правильнее: агент отправляет /agent/processes (у тебя это и так отдельным циклом идёт)
            case "REQUEST_PROCESSES":
                return CommandResult.Success(0, "Process refresh will be sent by processes loop");

            default:
                return CommandResult.Fail(1, null, $"Unknown command type: {cmd.Type}");
        }
    }

    private static string? GetString(Dictionary<string, JsonElement>? p, string key)
    {
        if (p == null) return null;
        if (!p.TryGetValue(key, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => v.ToString()
        };
    }

    private static int? GetInt(Dictionary<string, JsonElement>? p, string key)
    {
        if (p == null) return null;
        if (!p.TryGetValue(key, out var v)) return null;

        try
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
        }
        catch { /* ignore */ }

        return null;
    }
}