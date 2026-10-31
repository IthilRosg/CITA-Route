using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using RoutePilot.Agent;

if(args.Length!=1||Environment.MachineName!="USER"||
   !Path.GetFullPath(args[0]).Equals(@"C:\CITA-Lab\ZapretPreflight\module",StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Only the identified physical lab fixture is allowed.");
string root=Path.GetFullPath(args[0]);
using var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"upstream-receipt.json")));
var hashes=receipt.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
    file=>file.GetProperty("name").GetString()!,file=>file.GetProperty("sha256").GetString()!);
var bundle=new NativeZapretBundle(root,hashes);bundle.Verify();
var nic=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211&&n.OperationalStatus==OperationalStatus.Up&&
    n.GetIPProperties().UnicastAddresses.Any(a=>a.Address.ToString()=="192.168.1.124"));
var local=nic.GetIPProperties().UnicastAddresses.Single(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&a.Address.ToString()=="192.168.1.124").Address;
uint index=checked((uint)nic.GetIPProperties().GetIPv4Properties().Index);
var scope=new NativeZapretScope(Guid.NewGuid().ToString("N"),@"C:\CITA-Lab\anonymous-probe.exe",index,local.ToString(),53123,"8.8.8.8",443,"tcp");
var plan=NativeZapretPlan.ForTlsTcp(scope,scope.Epoch,index,local.ToString(),new HashSet<string>());
IntPtr library=NativeLibrary.Load(Path.Combine(root,"WinDivert.dll"));
var evaluations=new Dictionary<string,bool>();
try
{
    var compile=Marshal.GetDelegateForFunctionPointer<Compile>(NativeLibrary.GetExport(library,"WinDivertHelperCompileFilter"));
    if(!compile(plan.Filter,0,IntPtr.Zero,0,out var error,out uint position))
        throw new IOException("WinDivert filter rejected at "+position+": "+Marshal.PtrToStringAnsi(error));
    var evaluate=Marshal.GetDelegateForFunctionPointer<Evaluate>(NativeLibrary.GetExport(library,"WinDivertHelperEvalFilter"));
    bool Eval(bool outbound,string source,ushort sourcePort,string destination,ushort destinationPort,uint physical,bool impostor=false,bool loopback=false)
    {
        byte[] packet=Packet(source,sourcePort,destination,destinationPort);
        IntPtr address=Marshal.AllocHGlobal(80);
        try
        {
            Marshal.Copy(new byte[80],0,address,80);
            uint flags=(outbound?1u<<17:0)|(impostor?1u<<19:0)|(loopback?1u<<18:0);
            Marshal.WriteInt32(address,8,unchecked((int)flags));Marshal.WriteInt32(address,16,checked((int)physical));
            return evaluate(plan.Filter,packet,(uint)packet.Length,address);
        }
        finally{Marshal.FreeHGlobal(address);}
    }
    evaluations["owned-outbound"]=Eval(true,scope.LocalAddress,53123,"8.8.8.8",443,index);
    evaluations["owned-inbound"]=Eval(false,"8.8.8.8",443,scope.LocalAddress,53123,index);
    evaluations["foreign-local-port-refused"]=!Eval(true,scope.LocalAddress,53124,"8.8.8.8",443,index);
    evaluations["foreign-destination-refused"]=!Eval(true,scope.LocalAddress,53123,"8.8.4.4",443,index);
    evaluations["foreign-interface-refused"]=!Eval(true,scope.LocalAddress,53123,"8.8.8.8",443,index+1);
    evaluations["impostor-refused"]=!Eval(true,scope.LocalAddress,53123,"8.8.8.8",443,index,true);
    evaluations["loopback-refused"]=!Eval(true,scope.LocalAddress,53123,"8.8.8.8",443,index,false,true);
    if(evaluations.Values.Any(value=>!value))throw new IOException("WinDivert scope evaluation failed.");
}
finally{NativeLibrary.Free(library);}
var version=await Run(["--version"]);
var dry=await Run(["--dry-run",..plan.Arguments(root)]);
var lua=await Run(["--intercept=0",..plan.Arguments(root)]);
Console.WriteLine(JsonSerializer.Serialize(new{success=version.Exit==0&&dry.Exit==0&&lua.Exit==0,
    versionExit=version.Exit,dryRunExit=dry.Exit,luaInitializationExit=lua.Exit,
    versionOutput=version.Output,parameterOutput=dry.Output,luaOutput=lua.Output,
    filterCompiled=true,evaluations,interceptionRequested=false,scope="codec/parameters/Lua preflight, no real network bypass proof"}));
return version.Exit==0&&dry.Exit==0&&lua.Exit==0?0:1;

async Task<(int Exit,string Output)> Run(string[] arguments)
{
    var info=new ProcessStartInfo(Path.Combine(root,"winws2.exe")){
        UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,
        RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var argument in arguments)info.ArgumentList.Add(argument);
    using var process=Process.Start(info)??throw new IOException("Preflight failed to launch.");
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var output=Bounded(process.StandardOutput);var error=Bounded(process.StandardError);
    try{await process.WaitForExitAsync(timeout.Token);return(process.ExitCode,(await output)+(await error));}
    finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
}
static async Task<string> Bounded(StreamReader reader)
{
    var text=new System.Text.StringBuilder();char[] buffer=new char[1024];int count;
    while((count=await reader.ReadAsync(buffer))>0){if(text.Length+count>65536)throw new IOException("Preflight output budget exceeded.");text.Append(buffer,0,count);}
    return text.ToString();
}
static byte[] Packet(string source,ushort sourcePort,string destination,ushort destinationPort)
{
    byte[] p=new byte[40];p[0]=0x45;p[2]=0;p[3]=40;p[8]=64;p[9]=6;
    IPAddress.Parse(source).GetAddressBytes().CopyTo(p,12);IPAddress.Parse(destination).GetAddressBytes().CopyTo(p,16);
    BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20),sourcePort);BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22),destinationPort);
    p[32]=0x50;p[33]=2;return p;
}
[UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate bool Compile([MarshalAs(UnmanagedType.LPStr)]string filter,int layer,IntPtr output,uint length,out IntPtr error,out uint position);
[UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate bool Evaluate([MarshalAs(UnmanagedType.LPStr)]string filter,byte[] packet,uint length,IntPtr address);
