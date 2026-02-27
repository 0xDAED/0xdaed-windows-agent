using Oxdaed.Agent.Core;
using System.Diagnostics;

namespace Oxdaed.Agent.SystemInfo;

public static class ProcessSnapshot
{
    public static List<Dictionary<string, object?>> Take(int limit = 300)
    {
        var list = new List<Dictionary<string, object?>>(limit);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var nameExe = p.ProcessName + ".exe";
                list.Add(new Dictionary<string, object?>
                {
                    ["pid"] = p.Id,
                    ["name"] = p.ProcessName + ".exe",
                    ["working_set"] = p.WorkingSet64,
                    ["blocked"] = CommandHandlers.IsBlocked(nameExe),
                });
                if (list.Count >= limit) break;
            }
            catch { }
        }

        return list;
    }
}
