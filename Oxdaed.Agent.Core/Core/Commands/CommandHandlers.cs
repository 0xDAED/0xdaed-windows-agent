using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Oxdaed.Agent.Core;

public static class CommandHandlers
{
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

            var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return Task.FromResult(CommandResult.Success(0, $"Killed pid={pid}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Fail(1, null, ex.Message));
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
        // wraps into "...." and escapes quotes for PowerShell parsing
        // replace " with `"
        var esc = s.Replace("\"", "`\"");
        return "\"" + esc + "\"";
    }
}
