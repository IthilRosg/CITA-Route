using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RoutePilot.Agent;

// Read-only IPv4 listener ownership; avoids loading NetTCPIP/CIM into Watch.
internal static class TcpListenerOwnership
{
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
    internal static string Read(int pid,int port)
    {
        if(pid<=0||port is <1 or >65535)return "Unknown";
        int size=0;
        uint code=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);
        if(code!=122)return "Unknown";
        for(int attempt=0;attempt<3;attempt++)
        {
            if(size is <4 or >4194304)return "Unknown";
            int capacity=size;IntPtr buffer=Marshal.AllocHGlobal(capacity);
            try
            {
                code=GetExtendedTcpTable(buffer,ref size,false,2,3,0);
                if(code==122)continue;
                if(code!=0||size<4||size>capacity)return "Unknown";
                int count=Marshal.ReadInt32(buffer);
                if(count<0||count>(size-4)/24)return "Unknown";
                bool found=false;
                for(int i=0;i<count;i++)
                {
                    IntPtr row=buffer+4+i*24;
                    int wirePort=Marshal.ReadInt32(row,8);
                    int localPort=((wirePort&255)<<8)|((wirePort>>8)&255);
                    if(Marshal.ReadInt32(row)!=2||Marshal.ReadInt32(row,4)!=0x0100007F||localPort!=port)continue;
                    if(Marshal.ReadInt32(row,20)!=pid)return "Foreign";
                    found=true;
                }
                return found?"Ready":"Missing";
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        return "Unknown";
    }
    internal static string Bridge(string root)
    {
        var before=ComponentLiveness.Read<OrdinarySession>(Path.Combine(root,"runtime","session.json"));
        if(!before.Running||File.Exists(Path.Combine(root,"runtime","stop.request")))return "Unknown";
        var identity=new ProcessIdentity(before.BridgePid,before.BridgeStartedUtc,Path.Combine(AppContext.BaseDirectory,"vpn-bridge.exe"));
        if(!identity.Matches())return "Missing";
        string result=Read(before.BridgePid,20808);
        var after=ComponentLiveness.Read<OrdinarySession>(Path.Combine(root,"runtime","session.json"));
        bool current=after.Running&&before.Id==after.Id&&before.BridgeGeneration==after.BridgeGeneration&&before.BridgePid==after.BridgePid&&before.BridgeStartedUtc==after.BridgeStartedUtc&&identity.Matches()&&!File.Exists(Path.Combine(root,"runtime","stop.request"));
        return current?result:"Unknown";
    }
    internal static void Test()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            if(Read(Environment.ProcessId,port)!="Ready"||Read(Environment.ProcessId==1?2:1,port)!="Foreign"||Read(0,port)!="Unknown")throw new Exception("Native listener ownership was not verified.");
        }
        finally{listener.Stop();}
        if(Read(Environment.ProcessId,port)!="Missing")throw new Exception("Closed listener remained ready.");
    }
}
