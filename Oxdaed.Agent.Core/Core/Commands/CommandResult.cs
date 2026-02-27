namespace Oxdaed.Agent.Core;

public readonly record struct CommandResult(bool Ok, int ExitCode, string? Stdout, string? Stderr)
{
    public static CommandResult Success(int code, string? stdout = null, string? stderr = null)
        => new(true, code, stdout, stderr);

    public static CommandResult Fail(int code, string? stdout = null, string? stderr = null)
        => new(false, code, stdout, stderr);
}
