// ===========================
// Oxdaed.Agent.Updater (REWRITE)
// - flatten zip root folder (agent-x.y.z)
// - repair existing "agent-*" folders in target
// - keeps your run-copy approach
// ===========================
using System.Diagnostics;
using System.IO.Compression;
using System.ServiceProcess;
using System.Text.RegularExpressions;

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
            try { File.AppendAllText(logPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); }
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
            if (!parsed.RunCopy)
            {
                return LaunchRunCopyAndExit(parsed, Log);
            }

            // PHASE 2
            return ApplyUpdate(parsed, Log);
        }
        catch (Exception ex)
        {
            try { Log($"FATAL: {ex}"); } catch { }
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

            File.Copy(currentExe, runExe, overwrite: true);

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
            return 5;
        }
    }

    private static int ApplyUpdate(Opts parsed, Action<string> log)
    {
        var zipPath = parsed.Zip;
        var serviceName = parsed.Service;
        var targetDir = parsed.Target;

        var serviceExists = ServiceExists(serviceName);

        // 1) Stop service if exists
        if (serviceExists)
        {
            StopService(serviceName, log);
            Thread.Sleep(900);
        }
        else
        {
            log($"Service '{serviceName}' not installed. Dev mode update.");
            Thread.Sleep(400);
        }

        // 2) Apply update (extract -> flatten -> copy)
        ExtractZipToTarget_Flatten(zipPath, targetDir, log);

        // 2.1) Repair: если раньше появлялись target\agent-*\*.exe — поднимем в корень
        RepairTargetIfNested(targetDir, log);

        // 3) Start service OR run exe (dev)
        if (serviceExists)
            StartService(serviceName, log);
        else
            RunExeIfProvided(parsed.Run, log);

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
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

    private static void StopService(string serviceName, Action<string> log)
    {
        using var sc = new ServiceController(serviceName);
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
    // Update apply: extract + flatten + copy
    // --------------------------
    private static void ExtractZipToTarget_Flatten(string zipPath, string targetDir, Action<string> log)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "oxdaed_update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var selfExe = Process.GetCurrentProcess().MainModule!.FileName!;
        var selfExeFull = Path.GetFullPath(selfExe);

        try
        {
            log($"Extracting zip to temp: {tempDir}");
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            // ВАЖНО: flatten root (если в tempDir нет файлов, но есть ровно 1 папка)
            var root = GetExtractRoot(tempDir);
            log($"Extract root: {root}");

            foreach (var srcFile in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, srcFile);
                var dstFile = Path.Combine(targetDir, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);

                // Никогда не перезаписываем текущий исполняемый файл run-копии
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

    private static string GetExtractRoot(string tempDir)
    {
        try
        {
            var files = Directory.GetFiles(tempDir, "*", SearchOption.TopDirectoryOnly);
            var dirs = Directory.GetDirectories(tempDir, "*", SearchOption.TopDirectoryOnly);

            if (files.Length == 0 && dirs.Length == 1)
                return dirs[0];

            return tempDir;
        }
        catch
        {
            return tempDir;
        }
    }

    // --------------------------
    // Repair target if nested agent-*\ files exist
    // --------------------------
    private static void RepairTargetIfNested(string targetDir, Action<string> log)
    {
        try
        {
            // если вдруг после старых обновлений в корне нет сервисного exe, но есть в agent-*\*
            var expected = Path.Combine(targetDir, "Oxdaed.Agent.Service.exe");
            if (File.Exists(expected)) return;

            // ищем самый свежий вложенный сервисный exe
            var nested = Directory.EnumerateFiles(targetDir, "Oxdaed.Agent.Service.exe", SearchOption.AllDirectories)
                .Where(p => !Path.GetFullPath(p).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            if (nested is null) return;

            log($"REPAIR: found nested service exe: {nested.FullName}");
            log($"REPAIR: promoting to: {expected}");

            CopyWithRetryAtomic(nested.FullName, expected, log);

            // можно аналогично поднять Updater.exe, если он лежит внутри agent-*\:
            var updExpected = Path.Combine(targetDir, "Oxdaed.Agent.Updater.exe");
            var updNested = Directory.EnumerateFiles(targetDir, "Oxdaed.Agent.Updater.exe", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            if (updNested != null && !File.Exists(updExpected))
            {
                log($"REPAIR: promoting updater exe: {updNested.FullName} -> {updExpected}");
                CopyWithRetryAtomic(updNested.FullName, updExpected, log);
            }

            // по желанию: удалить папки agent-* (осторожно!)
            // CleanupAgentVersionFolders(targetDir, log);
        }
        catch (Exception ex)
        {
            log($"REPAIR ERROR: {ex.Message}");
        }
    }

    private static void CleanupAgentVersionFolders(string targetDir, Action<string> log)
    {
        try
        {
            var rx = new Regex(@"^agent-\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.IgnoreCase);
            foreach (var d in Directory.GetDirectories(targetDir))
            {
                var name = Path.GetFileName(d);
                if (!rx.IsMatch(name)) continue;

                try
                {
                    log($"CLEANUP: deleting folder {d}");
                    Directory.Delete(d, recursive: true);
                }
                catch (Exception ex)
                {
                    log($"CLEANUP ERROR: {d}: {ex.Message}");
                }
            }
        }
        catch { }
    }

    // --------------------------
    // Copy helper
    // --------------------------
    private static void CopyWithRetryAtomic(string src, string dst, Action<string> log)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                EnsureWritable(dst);

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
        try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); }
        catch { }
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