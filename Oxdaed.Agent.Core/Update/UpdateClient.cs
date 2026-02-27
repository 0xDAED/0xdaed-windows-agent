// ===========================
// UpdateClient.cs (REWRITE)
// ===========================
using Oxdaed.Agent.Api;
using Oxdaed.Config;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace Oxdaed.Agent.Core.Update;

public sealed class UpdateClient
{
    private readonly AgentConfig _cfg;
    private readonly AgentApiClient _api;

    // Один апдейт на машину (между процессами тоже)
    private static readonly Mutex UpdateMutex = new(false, "Global\\Oxdaed.Agent.Update");

    public UpdateClient(AgentConfig cfg, AgentApiClient api)
    {
        _cfg = cfg;
        _api = api;
    }

    public async Task TryUpdateOnStartup(CancellationToken ct)
    {
        var hasLock = false;

        try
        {
            // если уже кто-то обновляет — выходим
            hasLock = UpdateMutex.WaitOne(TimeSpan.FromSeconds(2));
            if (!hasLock) return;

            var manifestUrl = $"{_cfg.HostUrl.TrimEnd('/')}/agent/update/manifest.json";
            var manifest = await _api.GetJson<UpdateManifest>(manifestUrl, ct);
            if (manifest is null) return;

            var latest = (manifest.latest ?? "").Trim();
            var url = (manifest.url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(url)) return;

            var current = ThisVersion.Value;

            Console.WriteLine($"Update check: current={current} latest={latest}");

            if (string.Equals(latest, current, StringComparison.OrdinalIgnoreCase))
                return;

            var staging = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Oxdaed", "updates"
            );
            Directory.CreateDirectory(staging);

            var zipPath = Path.Combine(staging, $"agent-{latest}.zip");

            // Скачиваем безопасно: tmp + replace (чтобы не ловить "used by another process")
            await DownloadZipSafe(url, zipPath, ct);

            var updaterPath = Path.Combine(AppContext.BaseDirectory, "Oxdaed.Agent.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                Console.WriteLine($"Updater not found: {updaterPath}");
                return;
            }

            var serviceName = "OxdaedAgent";
            var targetDir = AppContext.BaseDirectory.TrimEnd('\\');
            var exePath = Path.Combine(targetDir, "Oxdaed.Agent.Service.exe");

            var args =
                $"--zip \"{zipPath}\" " +
                $"--service \"{serviceName}\" " +
                $"--target \"{targetDir}\" " +
                $"--run \"{exePath}\"";

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
        catch (Exception ex)
        {
            Console.WriteLine($"Update check failed (ignored): {ex.Message}");
        }
        finally
        {
            if (hasLock)
            {
                try { UpdateMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private async Task DownloadZipSafe(string url, string zipPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var tmpPath = zipPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            // Качаем во временный уникальный файл
            await _api.DownloadToFile(url, tmpPath, ct);

            // Затем атомарно подменяем zipPath
            ReplaceOrMoveWithRetry(tmpPath, zipPath, ct);
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    private static void ReplaceOrMoveWithRetry(string tmp, string dst, CancellationToken ct)
    {
        var delays = new[] { 50, 150, 300, 600, 1200, 2000 };
        Exception? last = null;

        for (int i = 0; i < delays.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(dst))
                {
                    // Replace может быть надёжнее, но если dst залочен — ретраим
                    File.Replace(tmp, dst, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, dst, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(delays[i]);
            }
        }

        throw new IOException($"Failed to write {dst} after retries", last);
    }
}

public sealed class UpdateManifest
{
    public string latest { get; set; } = "";
    public string url { get; set; } = "";
}

public static class ThisVersion
{
    // Важно: EntryAssembly = реальный запущенный EXE (а не сборка, где лежит ThisVersion)
    public static string Value
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            // если используешь InformationalVersion (рекомендую) — будет ровно как в манифесте
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info.Trim();

            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}