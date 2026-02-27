using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Oxdaed.Agent.Core;

public static class CommandHandlers
{
    // ====== Block policy storage ======
    private static readonly string PolicyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Oxdaed");

    private static readonly string BlockedFile =
        Path.Combine(PolicyDir, "blocked-processes.json");

    private static readonly SemaphoreSlim BlockedLock = new(1, 1);
    private static HashSet<string>? _blockedCache;

    // Не даём блокировать критические/system процессы
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "services", "lsass", "smss",
        "svchost", "system", "idle", "registry"
    };

    // ⚠️ Рекомендую: allowlist вынести в конфиг.
    // Если хочешь “можно блокировать всё” — просто очисти список и условие ниже убери.
    private static readonly HashSet<string> AllowedToBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        // пример-заглушка, лучше из конфига
        // "chrome", "discord", "steam"
    };

    private static string NormalizeProcName(string name)
    {
        name = (name ?? "").Trim();

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return name.ToLowerInvariant();
    }

    private static bool CanBeBlocked(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return false;
        if (ProtectedNames.Contains(normalizedName)) return false;

        // если allowlist задан — блокируем только разрешённое
        if (AllowedToBlock.Count > 0 && !AllowedToBlock.Contains(normalizedName))
            return false;

        return true;
    }

    private static async Task<HashSet<string>> LoadBlockedAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(PolicyDir);

        if (_blockedCache != null)
            return _blockedCache;

        if (!File.Exists(BlockedFile))
            return _blockedCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(BlockedFile, ct);
        var arr = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();

        _blockedCache = new HashSet<string>(
            arr.Select(NormalizeProcName).Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase
        );

        return _blockedCache;
    }

    private static async Task SaveBlockedAsync(HashSet<string> set, CancellationToken ct)
    {
        Directory.CreateDirectory(PolicyDir);

        var arr = set
            .Select(NormalizeProcName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        var json = JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(BlockedFile, json, ct);
    }

    // ====== Public API for snapshot/UI ======
    public static bool IsBlocked(string processName)
    {
        try
        {
            var n = NormalizeProcName(processName);
            return _blockedCache != null && _blockedCache.Contains(n);
        }
        catch { return false; }
    }

    public static async Task WarmupBlockedCacheAsync(CancellationToken ct)
    {
        await BlockedLock.WaitAsync(ct);
        try { _ = await LoadBlockedAsync(ct); }
        finally { BlockedLock.Release(); }
    }

    // ====== Commands: block/unblock ======
    public static async Task<CommandResult> BlockProcessNameAsync(string name, CancellationToken ct)
    {
        var n = NormalizeProcName(name);

        if (!CanBeBlocked(n))
            return CommandResult.Fail(1, null, $"Blocking is not allowed for: {name}");

        await BlockedLock.WaitAsync(ct);
        try
        {
            var set = await LoadBlockedAsync(ct);
            set.Add(n);
            await SaveBlockedAsync(set, ct);
            return CommandResult.Success(0, $"Blocked process name: {n}");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(1, null, ex.Message);
        }
        finally
        {
            BlockedLock.Release();
        }
    }

    public static async Task<CommandResult> UnblockProcessNameAsync(string name, CancellationToken ct)
    {
        var n = NormalizeProcName(name);

        await BlockedLock.WaitAsync(ct);
        try
        {
            var set = await LoadBlockedAsync(ct);
            var removed = set.Remove(n);
            await SaveBlockedAsync(set, ct);
            return CommandResult.Success(0, removed ? $"Unblocked: {n}" : $"Was not blocked: {n}");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(1, null, ex.Message);
        }
        finally
        {
            BlockedLock.Release();
        }
    }

    // ====== Enforcement: kill blocked processes in loop ======
    public static async Task EnforceBlockedProcessesAsync(CancellationToken ct)
    {
        HashSet<string> blocked;

        await BlockedLock.WaitAsync(ct);
        try
        {
            blocked = new HashSet<string>(await LoadBlockedAsync(ct), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            BlockedLock.Release();
        }

        if (blocked.Count == 0) return;

        foreach (var p in Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var pname = NormalizeProcName(p.ProcessName);

                if (!blocked.Contains(pname)) continue;
                if (ProtectedNames.Contains(pname)) continue;

                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore: access denied / already exited / protected process
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    // ====== Existing commands ======
    public static async Task<CommandResult> RunShellPowerShellAsync(string script, int timeoutSec, CancellationToken ct)
    {
        Console.WriteLine(script);

        if (string.IsNullOrWhiteSpace(script))
            return CommandResult.Fail(1, null, "Empty script");

        var exe = FindPwshOrPowershell();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -Command {WrapPsCommand(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 3, 3600)));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return CommandResult.Fail(-1, stdout.ToString(), "Timeout or cancelled");
        }

        return proc.ExitCode == 0
            ? CommandResult.Success(proc.ExitCode, stdout.ToString(), stderr.ToString())
            : CommandResult.Fail(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static Task<CommandResult> RebootAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/r /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return Task.FromResult(CommandResult.Success(0, "Reboot initiated"));
    }

    public static Task<CommandResult> ShutdownAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/s /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return Task.FromResult(CommandResult.Success(0, "Shutdown initiated"));
    }

    public static Task<CommandResult> SleepAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = "powrprof.dll,SetSuspendState 0,1,0",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return Task.FromResult(CommandResult.Success(0, "Sleep initiated"));
    }

    public static Task<CommandResult> KillProcessAsync(int pid)
    {
        try
        {
            if (pid <= 0) return Task.FromResult(CommandResult.Fail(1, null, "Invalid pid"));

            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return Task.FromResult(CommandResult.Success(0, $"Killed pid={pid}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Fail(1, null, ex.Message));
        }
    }

    public static async Task<CommandResult> GetBlockedListAsync(CancellationToken ct)
    {
        await BlockedLock.WaitAsync(ct);
        try
        {
            var set = await LoadBlockedAsync(ct); 
            var arr = set
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();

            var json = JsonSerializer.Serialize(arr);
            return CommandResult.Success(0, json, null);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(1, null, ex.Message);
        }
        finally
        {
            BlockedLock.Release();
        }
    }

    private static string FindPwshOrPowershell()
    {
        var pwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe"
        );

        if (File.Exists(pwsh)) return pwsh;
        return "powershell.exe";
    }

    private static string WrapPsCommand(string s)
    {
        var esc = (s ?? "").Replace("\"", "`\"");
        return "\"" + esc + "\"";
    }
}