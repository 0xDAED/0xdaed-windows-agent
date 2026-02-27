using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;



static bool IsAdmin()
{
    using var id = WindowsIdentity.GetCurrent();
    var p = new WindowsPrincipal(id);
    return p.IsInRole(WindowsBuiltInRole.Administrator);
}

static void RelaunchAsAdmin(string[] args)
{
    var exe = Process.GetCurrentProcess().MainModule!.FileName!;
    var psi = new ProcessStartInfo(exe)
    {
        UseShellExecute = true,
        Verb = "runas",
        Arguments = string.Join(" ", args.Select(a => $"\"{a}\""))
    };
    Log.WriteLine("Requesting UAC elevation (runas)...");
    Process.Start(psi);
}

static byte[] ReadResource(string endsWithName)
{
    var asm = Assembly.GetExecutingAssembly();
    var names = asm.GetManifestResourceNames();

    Log.WriteLine("Embedded resources:");
    foreach (var n in names) Log.WriteLine("  - " + n);

    var name = names.FirstOrDefault(n => n.EndsWith(endsWithName, StringComparison.OrdinalIgnoreCase));
    if (name is null) throw new Exception($"Embedded resource not found: {endsWithName}");

    using var s = asm.GetManifestResourceStream(name);
    if (s is null) throw new Exception($"Resource stream is null: {name}");

    using var ms = new MemoryStream();
    s.CopyTo(ms);
    Log.WriteLine($"ReadResource OK: {endsWithName} ({ms.Length} bytes)");
    return ms.ToArray();
}

static (int code, string stdout, string stderr) RunCapture(string file, string args)
{
    Log.WriteLine($">> RUN: {file} {args}");
    var psi = new ProcessStartInfo(file, args)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var p = Process.Start(psi);
    if (p == null) return (-1, "", "Process.Start returned null");

    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();

    stdout = (stdout ?? "").Trim();
    stderr = (stderr ?? "").Trim();

    if (stdout.Length > 0) Log.WriteLine("STDOUT: " + stdout.Replace("\r", "").Replace("\n", " | "));
    if (stderr.Length > 0) Log.WriteLine("STDERR: " + stderr.Replace("\r", "").Replace("\n", " | "));
    Log.WriteLine($"<< EXIT: {p.ExitCode}");

    return (p.ExitCode, stdout, stderr);
}

static int Run(string file, string args) => RunCapture(file, args).code;

static bool ServiceExists(string name) => Run("sc.exe", $"query \"{name}\"") == 0;

static bool LocalUserExists(string username)
    => Run("cmd.exe", $"/c net user \"{username}\" >nul 2>&1") == 0;

static string GetLocalAdminsGroupName()
{
    var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    var nt = (NTAccount)sid.Translate(typeof(NTAccount)); // "BUILTIN\Администраторы" / "BUILTIN\Administrators"
    var full = nt.Value;
    var idx = full.IndexOf('\\');
    var name = idx >= 0 ? full[(idx + 1)..] : full;
    Log.WriteLine($"Admins group resolved: {full} -> {name}");
    return name;
}

static bool IsUserInLocalGroup(string groupName, string username)
    => Run("cmd.exe", $"/c net localgroup \"{groupName}\" | findstr /I /C:\"{username}\" >nul") == 0;

static string GenPassword(int bytes = 24)
{
    var b = RandomNumberGenerator.GetBytes(bytes);
    return Convert.ToBase64String(b).Replace("+", "A").Replace("/", "B") + "!a1";
}

static void EnsureLocalAdminUser(string username, string password)
{
    var adminsGroup = GetLocalAdminsGroupName();

    if (!LocalUserExists(username))
    {
        Log.WriteLine($"Creating local user: {username}");
        var code = Run("cmd.exe", $"/c net user \"{username}\" \"{password}\" /add /y");
        if (code != 0) throw new Exception("Failed to create local user for service.");
    }
    else
    {
        Log.WriteLine($"Local user exists: {username} (skip create)");
    }

    // всегда задаём пароль, чтобы совпал с sc config password
    Log.WriteLine($"Setting password for {username}");
    {
        var code = Run("cmd.exe", $"/c net user \"{username}\" \"{password}\"");
        if (code != 0) throw new Exception("Failed to set password for local user.");
    }

    if (!IsUserInLocalGroup(adminsGroup, username))
    {
        Log.WriteLine($"Adding {username} to local group: {adminsGroup}");
        var code = Run("cmd.exe", $"/c net localgroup \"{adminsGroup}\" \"{username}\" /add");
        if (code != 0) throw new Exception("Failed to add service user to local Administrators.");
    }
    else
    {
        Log.WriteLine($"{username} already in group {adminsGroup} (skip add)");
    }
}



static void GrantLogonAsService_Lsa(string username)
{
    Log.WriteLine("Granting 'Log on as a service' via LSA...");

    // SID пользователя (локальный)
    var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, username)
        .Translate(typeof(SecurityIdentifier));

    // LsaAddAccountRights идемпотентен: если право уже есть, просто ок
    Lsa.AddLogonAsServiceRight(sid);

    Log.WriteLine("SeServiceLogonRight granted (LSA).");
}

// ===================== Diagnostics =====================

static void PrintServiceDiagnostics(string serviceName)
{
    Log.WriteLine("=== SC QUERY ===");
    Run("sc.exe", $"query \"{serviceName}\"");

    Log.WriteLine("=== SC QC ===");
    Run("sc.exe", $"qc \"{serviceName}\"");

    Log.WriteLine("=== Last SCM events (System log) ===");
    try
    {
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[Provider[@Name='Service Control Manager'] and (EventID=7038 or EventID=7000 or EventID=7009 or EventID=7011 or EventID=7024 or EventID=7041)]]");

        using var reader = new EventLogReader(query);
        int shown = 0;
        for (EventRecord? ev = reader.ReadEvent(); ev != null && shown < 20; ev = reader.ReadEvent())
        {
            var msg = ev.FormatDescription() ?? "";
            if (msg.Contains(serviceName, StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Oxdaed", StringComparison.OrdinalIgnoreCase))
            {
                Log.WriteLine($"[{ev.TimeCreated}] ID={ev.Id} {msg.Replace("\r", "").Replace("\n", " ")}");
                shown++;
            }
        }
        if (shown == 0) Log.WriteLine("(No matching SCM events found)");
    }
    catch (Exception ex)
    {
        Log.WriteLine($"(Could not read System event log: {ex.Message})");
    }
}

static void EnsureServiceStarted(string serviceName)
{
    var (code, _, _) = RunCapture("sc.exe", $"start \"{serviceName}\"");
    if (code == 0) return;

    Log.WriteLine($"ERROR: sc start failed (code {code}).");
    PrintServiceDiagnostics(serviceName);
    throw new Exception("Service failed to start (see diagnostics).");
}

// ===================== MAIN =====================

try
{
    Log.Init();

    var serviceName = "OxdaedAgent";
    var installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OxdaedAgent"
    );

    if (!IsAdmin())
    {
        Log.WriteLine("Need admin rights. Requesting UAC...");
        RelaunchAsAdmin(args);
        return;
    }

    Log.WriteLine($"Install dir: {installDir}");
    Directory.CreateDirectory(installDir);

    var serviceExePath = Path.Combine(installDir, "Oxdaed.Agent.Service.exe");
    var updaterExePath = Path.Combine(installDir, "Oxdaed.Agent.Updater.exe");

    Log.WriteLine("Extracting payload...");
    File.WriteAllBytes(serviceExePath, ReadResource("Oxdaed.Agent.Service.exe"));
    File.WriteAllBytes(updaterExePath, ReadResource("Oxdaed.Agent.Updater.exe"));

    // сервисный аккаунт
    var svcUser = "OxdaedSvc";
    var svcPass = GenPassword();
    Log.WriteLine($"Service account: .\\{svcUser}");

    EnsureLocalAdminUser(svcUser, svcPass);

    // ✅ вместо secedit — LSA API
    GrantLogonAsService_Lsa(svcUser);

    // create / update service
    if (ServiceExists(serviceName))
    {
        Log.WriteLine("Service exists: updating...");
        Run("sc.exe", $"stop \"{serviceName}\"");
        Run("sc.exe", $"config \"{serviceName}\" binPath= \"{serviceExePath}\" start= auto");
    }
    else
    {
        Log.WriteLine("Creating service...");
        var code = Run("sc.exe",
            $"create \"{serviceName}\" binPath= \"{serviceExePath}\" start= auto DisplayName= \"Oxdaed Agent\"");
        if (code != 0) throw new Exception("Failed to create service");
    }

    // assign account
    var account = $".\\{svcUser}";
    Log.WriteLine($"Configuring service account: {account}");
    var credCode = Run("sc.exe", $"config \"{serviceName}\" obj= \"{account}\" password= \"{svcPass}\"");
    if (credCode != 0) throw new Exception("Failed to set service account.");

    // restart policy
    Run("sc.exe", $"failure \"{serviceName}\" reset= 0 actions= restart/5000/restart/5000/restart/5000");
    Run("sc.exe", $"failureflag \"{serviceName}\" 1");

    // start
    EnsureServiceStarted(serviceName);

    Log.WriteLine("SUCCESS: Installed and started.");
    Log.WriteLine($"Log file: {Log.LogPath}");
}
catch (Exception ex)
{
    Log.WriteException(ex, "MAIN");
    Log.WriteLine("FAILED. Check log file:");
    Log.WriteLine(Log.LogPath);

    try
    {
        Log.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
    catch { }
}


static class Log
{
    private static readonly object _lock = new();
    public static string LogPath { get; private set; } = "";

    public static void Init()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OxdaedAgent"
        );
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "install.log");

        WriteLine("==== Oxdaed installer started ====");
        WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        WriteLine($"OS: {Environment.OSVersion}");
        WriteLine($".NET: {Environment.Version}");
        WriteLine($"User: {Environment.UserDomainName}\\{Environment.UserName}");
        WriteLine($"Is64BitProcess: {Environment.Is64BitProcess}");
        WriteLine($"Exe: {Process.GetCurrentProcess().MainModule?.FileName}");
        WriteLine("");
    }

    public static void WriteLine(string msg)
    {
        lock (_lock)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public static void WriteException(Exception ex, string context)
    {
        WriteLine($"!! EXCEPTION in {context}: {ex.GetType().Name}: {ex.Message}");
        WriteLine(ex.StackTrace ?? "(no stack)");
        if (ex.InnerException != null)
        {
            WriteLine($"-- Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            WriteLine(ex.InnerException.StackTrace ?? "(no inner stack)");
        }
    }
}


// ===================== LSA: Grant SeServiceLogonRight =====================

static class Lsa
{
    private const int POLICY_ALL_ACCESS = 0x00F0FFF;
    private const int STATUS_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;

        public static LSA_UNICODE_STRING FromString(string s)
        {
            // LSA expects length in bytes, not chars
            var bytes = Encoding.Unicode.GetBytes(s);
            var mem = Marshal.StringToHGlobalUni(s);
            return new LSA_UNICODE_STRING
            {
                Length = (ushort)bytes.Length,
                MaximumLength = (ushort)(bytes.Length + 2),
                Buffer = mem
            };
        }

        public void Free()
        {
            if (Buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Buffer);
                Buffer = IntPtr.Zero;
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int LsaOpenPolicy(
        IntPtr SystemName,
        ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
        int DesiredAccess,
        out IntPtr PolicyHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaClose(IntPtr ObjectHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(int status);

    [DllImport("advapi32.dll")]
    private static extern int LsaAddAccountRights(
        IntPtr PolicyHandle,
        IntPtr AccountSid,
        LSA_UNICODE_STRING[] UserRights,
        int CountOfRights);

    public static void AddLogonAsServiceRight(SecurityIdentifier sid)
    {
        var oa = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref oa, POLICY_ALL_ACCESS, out var handle);
        if (status != STATUS_SUCCESS)
            throw new Exception($"LsaOpenPolicy failed: winerr={LsaNtStatusToWinError(status)} status=0x{status:X}");

        try
        {
            var right = LSA_UNICODE_STRING.FromString("SeServiceLogonRight");
            try
            {
                var sidBytes = new byte[sid.BinaryLength];
                sid.GetBinaryForm(sidBytes, 0);
                var sidPtr = Marshal.AllocHGlobal(sidBytes.Length);
                try
                {
                    Marshal.Copy(sidBytes, 0, sidPtr, sidBytes.Length);

                    status = LsaAddAccountRights(handle, sidPtr, new[] { right }, 1);
                    if (status != STATUS_SUCCESS)
                        throw new Exception($"LsaAddAccountRights failed: winerr={LsaNtStatusToWinError(status)} status=0x{status:X}");
                }
                finally
                {
                    Marshal.FreeHGlobal(sidPtr);
                }
            }
            finally
            {
                right.Free();
            }
        }
        finally
        {
            LsaClose(handle);
        }
    }
}