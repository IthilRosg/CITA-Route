using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace RoutePilot.Agent;

internal static class PolicyNetwork
{
    // GUID-returning NLM methods cannot be reliably invoked through IDispatch.
    // These read-only vtable prefixes match the Windows SDK netlistmgr.h.
    [ComImport,Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IManager
    {
        [PreserveSig]int GetNetworks(uint flags,out IntPtr unused);
        [PreserveSig]int GetNetwork(Guid id,out INetwork network);
        [PreserveSig]int GetNetworkConnections(out IConnections connections);
    }
    [ComImport,Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IConnections
    {
        [PreserveSig]int GetNewEnum(out IntPtr unused);
        [PreserveSig]int Next(uint count,out IConnection connection,ref uint fetched);
    }
    [ComImport,Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IConnection
    {
        [PreserveSig]int GetNetwork(out INetwork network);
        [PreserveSig]int IsInternet(out short value);
        [PreserveSig]int IsConnected(out short value);
        [PreserveSig]int GetConnectivity(out uint value);
        [PreserveSig]int GetConnectionId(out Guid value);
        [PreserveSig]int GetAdapterId(out Guid value);
    }
    [ComImport,Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetwork
    {
        [PreserveSig]int GetName(out IntPtr unused);
        [PreserveSig]int SetName(IntPtr unused);
        [PreserveSig]int GetDescription(out IntPtr unused);
        [PreserveSig]int SetDescription(IntPtr unused);
        [PreserveSig]int GetNetworkId(out Guid value);
    }
    internal static string Profiles(string adapterId)
    {
        var type=Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
        var manager=(IManager)Activator.CreateInstance(type!)!;
        try
        {
            Marshal.ThrowExceptionForHR(manager.GetNetworkConnections(out var connections));
            try
            {
                var ids=new List<string>();uint fetched=0;
                while(connections.Next(1,out var connection,ref fetched)==0 && fetched==1)
                {
                    try
                    {
                        Marshal.ThrowExceptionForHR(connection.GetAdapterId(out Guid adapter));
                        Marshal.ThrowExceptionForHR(connection.IsConnected(out short connected));
                        if(connected==0 || !adapter.ToString("D").Equals(adapterId.Trim('{','}'),StringComparison.OrdinalIgnoreCase))continue;
                        Marshal.ThrowExceptionForHR(connection.GetNetwork(out var network));
                        try{Marshal.ThrowExceptionForHR(network.GetNetworkId(out Guid id));ids.Add(id.ToString("N"));}
                        finally{Marshal.ReleaseComObject(network);}
                    }
                    finally{Marshal.ReleaseComObject(connection);}
                    fetched=0;
                }
                return string.Join(",",ids.Distinct().Order(StringComparer.Ordinal));
            }
            finally{Marshal.ReleaseComObject(connections);}
        }
        finally{Marshal.ReleaseComObject(manager);}
    }
    internal static string Signature(string root)
    {
        using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime","bridge.json")));
        string name=config.RootElement.GetProperty("outbounds")[0].GetProperty("bind_interface").GetString()!;
        var nic=NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>n.Name==name);
        if(nic==null)return "missing-interface:"+name;
        var properties=nic.GetIPProperties();
        // Adaptive rules are IPv4-only. Rotating temporary IPv6 addresses must
        // not invalidate an otherwise unchanged IPv4 network every few minutes.
        string ips=string.Join(",",properties.UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Order(StringComparer.Ordinal));
        string gateways=string.Join(",",properties.GatewayAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Order(StringComparer.Ordinal));
        string dns=string.Join(",",properties.DnsAddresses.Select(a=>a.ToString()).Order(StringComparer.Ordinal));
        string profiles="";
        try
        {
            profiles=Profiles(nic.Id);
        }
        catch(Exception ex)when(ex is COMException or InvalidComObjectException or ArgumentException){profiles="unavailable";}
        string text=nic.Id.ToLowerInvariant()+"|"+nic.OperationalStatus+"|"+ips+"|"+gateways+"|"+dns+"|"+profiles;
        string profile=Path.Combine(root,"private","SmartRouting-AWG2.conf");
        if(File.Exists(profile))text+="|"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
