using Oxdaed.Agent.Api;
using Oxdaed.Config;
using System.Diagnostics;

namespace Oxdaed.Agent.Core.Update;

public sealed class UpdateClient
{
    private readonly AgentConfig _cfg;
    private readonly AgentApiClient _api;

    public UpdateClient(AgentConfig cfg, AgentApiClient api)
    {
        _cfg = cfg;
        _api = api;
    }

    public async Task TryUpdateOnStartup(CancellationToken ct)
    {
        // пример: manifest лежит по URL
        var manifestUrl = $"{_cfg.HostUrl.TrimEnd('/')}/agent/update/manifest.json";
        var manifest = await _api.GetJson<UpdateManifest>(manifestUrl, ct);
        if (manifest is null) return;


        var current = ThisVersion.Value;

        if (string.Equals(manifest.latest, current, StringComparison.OrdinalIgnoreCase))
            return;

        var staging = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Oxdaed", "updates"
        );
        Directory.CreateDirectory(staging);

        var zipPath = Path.Combine(staging, $"agent-{manifest.latest}.zip");
        await _api.DownloadToFile(manifest.url, zipPath, ct);

        var updaterPath = Path.Combine(AppContext.BaseDirectory, "Oxdaed.Agent.Updater.exe");
        var serviceName = "OxdaedAgent";
        var targetDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exePath = Path.Combine(targetDir, "Oxdaed.Agent.Service.exe");

        var args =
            $"--zip \"{zipPath}\" " +
            $"--service \"{serviceName}\" " +
            $"--target \"{targetDir}\" " +
            $"--run \"{exePath}\"";

        if (!File.Exists(updaterPath))
        {
            Console.WriteLine($"Updater not found: {updaterPath}");
            return;
        }

        var p = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = args,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (p is null) return;
        await Task.Delay(300, CancellationToken.None); 
        Environment.Exit(0);
    }
}

public sealed class UpdateManifest
{
    public string latest { get; set; } = "";
    public string url { get; set; } = "";
}

public static class ThisVersion
{
    public static string Value =>
        typeof(ThisVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}