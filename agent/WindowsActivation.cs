using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RoutePilot.Agent;

// Kernel uptime survives Windows Fast Startup. A boot activation event is
// separate from service restart and sleep/resume; no user/session data is read.
internal static class WindowsActivation
{
    [DllImport("wevtapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr EvtQuery(IntPtr session,string path,string query,int flags);
    [DllImport("wevtapi.dll",SetLastError=true)]static extern bool EvtNext(IntPtr query,int count,IntPtr[] events,int timeout,int flags,out int returned);
    [DllImport("wevtapi.dll",SetLastError=true)]static extern bool EvtRender(IntPtr context,IntPtr value,int flags,int size,IntPtr buffer,out int used,out int count);
    [DllImport("wevtapi.dll")]static extern bool EvtClose(IntPtr value);
    static readonly Lazy<string> Identity=new(Read);
    internal static string Current=>Identity.Value;
    internal static string Parse(string xml,DateTime now)
    {
        if(xml.Length>32768)return "";
        try
        {
            var document=XDocument.Parse(xml);var system=document.Root?.Elements().SingleOrDefault(e=>e.Name.LocalName=="System");
            if(system==null)return "";
            XElement? Field(string name)=>system.Elements().SingleOrDefault(e=>e.Name.LocalName==name);
            if(Field("Provider")?.Attribute("Name")?.Value!="Microsoft-Windows-Kernel-Boot"||Field("EventID")?.Value!="27"||
                !ulong.TryParse(Field("EventRecordID")?.Value,out var record)||
                !DateTime.TryParse(Field("TimeCreated")?.Attribute("SystemTime")?.Value,null,System.Globalization.DateTimeStyles.RoundtripKind,out var stamp))return "";
            stamp=stamp.ToUniversalTime();
            if(stamp>now.AddSeconds(2)||stamp<now.AddYears(-2))return "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record+"|"+stamp.ToString("O"))));
        }
        catch(Exception ex)when(ex is System.Xml.XmlException or InvalidOperationException){return "";}
    }
    static string Read()
    {
        IntPtr query=IntPtr.Zero,item=IntPtr.Zero,buffer=IntPtr.Zero;
        try
        {
            query=EvtQuery(IntPtr.Zero,"System","*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]",1|0x200);
            if(query==IntPtr.Zero)return "";
            var events=new IntPtr[1];if(!EvtNext(query,1,events,1000,0,out int returned)||returned!=1)return "";item=events[0];
            EvtRender(IntPtr.Zero,item,1,0,IntPtr.Zero,out int size,out _);
            if(size is <=0 or >65536)return "";
            buffer=Marshal.AllocHGlobal(size);
            if(!EvtRender(IntPtr.Zero,item,1,size,buffer,out _,out _))return "";
            return Parse(Marshal.PtrToStringUni(buffer)??"",DateTime.UtcNow);
        }
        catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception){return "";}
        finally{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);if(item!=IntPtr.Zero)EvtClose(item);if(query!=IntPtr.Zero)EvtClose(query);}
    }
}
