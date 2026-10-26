using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoutePilot.Agent;

// Diagnostic observations, not an attestation of continuous absence of logons.
// Record counts only; never persist Windows user names or VPN configuration.
internal sealed class StartupEvidence
{
    public int Schema{get;set;}=1;
    public DateTime BootUtc{get;set;}
    public string ActivationId{get;set;}=WindowsActivation.Current;
    public DateTime StartedUtc{get;set;}=DateTime.UtcNow;
    public DateTime? ConnectedUtc{get;set;}
    public int ServiceSessionId{get;set;}=Process.GetCurrentProcess().SessionId;
    public int? InteractiveUsersAtStart{get;set;}=CountUsers();
    public int? InteractiveUsersAtConnection{get;set;}
    public string SessionId{get;set;}="";
    public int BridgeGeneration{get;set;}
    public int ConnectionAttempts{get;set;}
    public string Version{get;set;}=typeof(StartupEvidence).Assembly.GetName().Version!.ToString(3);
    public static int? CountUsers()
    {
        if(!WTSEnumerateSessions(IntPtr.Zero,0,1,out var buffer,out int count))return null;
        try
        {
            if(count is <0 or >256)return null;
            int users=0,size=Marshal.SizeOf<Session>();
            for(int i=0;i<count;i++)
            {
                var session=Marshal.PtrToStructure<Session>(IntPtr.Add(buffer,i*size));
                if(session.Id==0)continue;
                if(!WTSQuerySessionInformation(IntPtr.Zero,session.Id,5,out var name,out int bytes))return null;
                try{if(bytes>2)users++;}finally{WTSFreeMemory(name);}
            }
            return users;
        }
        finally{WTSFreeMemory(buffer);}
    }
    [StructLayout(LayoutKind.Sequential)] struct Session{public int Id;public IntPtr Name;public int State;}
    [DllImport("wtsapi32.dll",EntryPoint="WTSEnumerateSessionsW",SetLastError=true)] static extern bool WTSEnumerateSessions(IntPtr server,int reserved,int version,out IntPtr sessions,out int count);
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)] static extern bool WTSQuerySessionInformation(IntPtr server,int session,int information,out IntPtr value,out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
}
