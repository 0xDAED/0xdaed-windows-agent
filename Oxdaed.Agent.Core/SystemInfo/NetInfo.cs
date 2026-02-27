using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Oxdaed.Agent.SystemInfo;

public static class NetInfo
{
    public static (string? ip, string? mac) GetIpAndMac()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                var addr = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(a.Address));
                if (addr == null) continue;

                var mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                return (addr.Address.ToString(), mac);
            }
        }
        catch { }
        return (null, null);
    }
}
