using System.Diagnostics;
using System.IO.Compression;
using System.ServiceProcess;

internal static class Program
{
    private sealed record Opts(
        string Zip,
        string Service,
        string Target,
        string? Run,
        bool RunCopy
    );

    public static int Main(string[] args)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Oxdaed", "updates", "updater.log"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        void Log(string msg)
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
            catch { /* ignore */ }
        }

        try
        {
            var parsed = ParseArgs(args);
            if (parsed is null)
            {
                Log("ERROR: missing args. Need --zip --service --target (optional --run).");
                return 2;
            }

            Log($"START args: zip={parsed.Zip} service={parsed.Service} target={parsed.Target} run={parsed.Run ?? "(null)"} runCopy={parsed.RunCopy}");

            if (!File.Exists(parsed.Zip))
            {
                Log($"ERROR: zip not found: {parsed.Zip}");
                return 3;
            }

            if (!Directory.Exists(parsed.Target))
            {
                Log($"ERROR: target dir not found: {parsed.Target}");
                return 4;
            }

            // PHASE 1: если это не run-copy, запускаем копию себя из TEMP и выходим.
            // Так мы сможем обновить оригинальный Updater.exe в targetDir.
            if (!parsed.RunCopy)
            {
                return LaunchRunCopyAndExit(parsed, Log);
            }

            // PHASE 2: фактическое применение обновления
            return ApplyUpdate(parsed, Log);
        }
        catch (Exception ex)
        {
            try { Log($"FATAL: {ex}"); } catch { /* ignore */ }
            return 1;
        }
    }

    // --------------------------
    // Phase orchestration
    // --------------------------
    private static int LaunchRunCopyAndExit(Opts parsed, Action<string> log)
    {
        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule!.FileName!;
            var tempRunDir = Path.Combine(Path.GetTempPath(), "OxdaedUpdaterRun");
            Directory.CreateDirectory(tempRunDir);

            var runExe = Path.Combine(tempRunDir, "Oxdaed.Agent.Updater.run.exe");

            // Перекладываем себя в TEMP (run-копия)
            File.Copy(currentExe, runExe, overwrite: true);

            // Собираем аргументы заново (без лишнего) + --run-copy
            var newArgs =
                $"--run-copy --zip \"{parsed.Zip}\" --service \"{parsed.Service}\" --target \"{parsed.Target}\"" +
                (string.IsNullOrWhiteSpace(parsed.Run) ? "" : $" --run \"{parsed.Run}\"");

            log($"PHASE1: launching run-copy: {runExe} {newArgs}");

            Process.Start(new ProcessStartInfo
            {
                FileName = runExe,
                Arguments = newArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempRunDir
            });

            log("PHASE1: exit OK (run-copy started).");
            return 0;
        }
        catch (Exception ex)
        {
            log($"PHASE1 ERROR: {ex}");
            // Лучше фейлить, чем делать полуобновление, потому что self-replace может сломаться.
            return 5;
        }
    }

    private static int ApplyUpdate(Opts parsed, Action<string> log)
    {
        var zipPath = parsed.Zip;
        var serviceName = parsed.Service;
        var targetDir = parsed.Target;

        // 1) Stop service if exists
        var serviceExists = ServiceExists(serviceName);
        if (serviceExists)
        {
            StopService(serviceName, log);
            // Дадим ОС отпустить хендлы
            Thread.Sleep(800);
        }
        else
        {
            log($"Service '{serviceName}' not installed. Dev mode update.");
            Thread.Sleep(400);
        }

        // 2) Extract zip -> temp, then copy to target
        ExtractZipToTarget(zipPath, targetDir, log);

        // 3) Start service OR run exe (dev)
        if (serviceExists)
        {
            StartService(serviceName, log);
        }
        else
        {
            RunExeIfProvided(parsed.Run, log);
        }

        log("DONE OK");
        return 0;
    }

    // --------------------------
    // Args
    // --------------------------
    private static Opts? ParseArgs(string[] args)
    {
        string? zip = null, service = null, target = null, run = null;
        bool runCopy = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a.Equals("--zip", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) zip = args[++i];
            else if (a.Equals("--service", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) service = args[++i];
            else if (a.Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) target = args[++i];
            else if (a.Equals("--run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) run = args[++i];
            else if (a.Equals("--run-copy", StringComparison.OrdinalIgnoreCase)) runCopy = true;
        }

        if (string.IsNullOrWhiteSpace(zip) ||
            string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(target))
            return null;

        return new Opts(zip!, service!, target!, run, runCopy);
    }

    // --------------------------
    // Windows Service helpers
    // --------------------------
    private static bool ServiceExists(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            _ = sc.Status; // throws if not installed
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StopService(string serviceName, Action<string> log)
    {
        using var sc = new ServiceController(serviceName);

        // Refresh status
        sc.Refresh();

        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            log($"Service '{serviceName}' already stopped.");
            return;
        }

        log($"Stopping service '{serviceName}'...");
        sc.Stop();

        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        log($"Service '{serviceName}' stopped.");
    }

    private static void StartService(string serviceName, Action<string> log)
    {
        using var sc = new ServiceController(serviceName);

        sc.Refresh();

        if (sc.Status == ServiceControllerStatus.Running)
        {
            log($"Service '{serviceName}' already running.");
            return;
        }

        log($"Starting service '{serviceName}'...");
        sc.Start();

        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        log($"Service '{serviceName}' running.");
    }

    // --------------------------
    // Update apply: extract + copy
    // --------------------------
    private static void ExtractZipToTarget(string zipPath, string targetDir, Action<string> log)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "oxdaed_update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // run-копия exe (мы не должны случайно перезаписать то, что сейчас исполняется)
        var selfExe = Process.GetCurrentProcess().MainModule!.FileName!;
        var selfExeFull = Path.GetFullPath(selfExe);

        try
        {
            log($"Extracting zip to temp: {tempDir}");
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            // копируем всё из tempDir в targetDir
            foreach (var srcFile in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(tempDir, srcFile);
                var dstFile = Path.Combine(targetDir, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);

                // Никогда не пытаемся перезаписать текущий исполняемый файл (run-копию)
                if (Path.GetFullPath(dstFile).Equals(selfExeFull, StringComparison.OrdinalIgnoreCase))
                {
                    log($"Skipping overwrite of current running exe: {dstFile}");
                    continue;
                }

                CopyWithRetryAtomic(srcFile, dstFile, log);
            }

            log("Files copied to target.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void CopyWithRetryAtomic(string src, string dst, Action<string> log)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                EnsureWritable(dst);

                // Пишем сначала во временный файл рядом, затем Replace (атомарнее)
                var tmp = dst + ".tmp";
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

                File.Copy(src, tmp, overwrite: true);

                if (File.Exists(dst))
                {
                    var bak = dst + ".bak";
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }

                    File.Replace(tmp, dst, bak, ignoreMetadataErrors: true);

                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                }
                else
                {
                    File.Move(tmp, dst);
                }

                return;
            }
            catch (Exception ex)
            {
                log($"Copy failed (attempt {attempt}) {Path.GetFileName(src)} -> {dst}: {ex.Message}");
                Thread.Sleep(600);
            }
        }

        throw new IOException($"Failed to copy after retries: {src} -> {dst}");
    }

    private static void EnsureWritable(string path)
    {
        try
        {
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);
        }
        catch { /* ignore */ }
    }

    // --------------------------
    // Dev run
    // --------------------------
    private static void RunExeIfProvided(string? exePath, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            log("No --run provided, nothing to start in dev mode.");
            return;
        }

        if (!File.Exists(exePath))
        {
            log($"Run target not found: {exePath}");
            return;
        }

        log($"Starting updated exe (dev mode): {exePath}");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            log($"Failed to start exe: {ex}");
        }
    }
}