using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace RoutePilot.Telemetry;

public sealed class WireSockSample
{
    public bool Available{get;set;}
    public string Code{get;set;}="Unavailable";
    public string Profile{get;set;}="";
    public int ConnectionState{get;set;}
    public int? RttMs{get;set;}
    public double? EstimatedLossPercent{get;set;}
    public ulong RxBytes{get;set;}
    public ulong TxBytes{get;set;}
    public long UptimeSeconds{get;set;}
    public DateTime ObservedUtc{get;set;}
}

// Minimal protobuf wire reader. No vendor DLL is loaded and no tunnel handle is created.
internal static class StatsProto
{
    internal sealed record Field(int Number,int Wire,ulong Value,byte[] Bytes);
    internal static ulong Varint(ReadOnlySpan<byte> data,ref int offset)
    {
        ulong value=0;
        for(int shift=0;shift<70;shift+=7)
        {
            if(offset>=data.Length)throw new InvalidDataException("Truncated statistics message.");
            byte b=data[offset++];if(shift==63 && b>1)throw new InvalidDataException("Invalid statistics integer.");
            value|=(ulong)(b&127)<<shift;if(b<128)return value;
        }
        throw new InvalidDataException("Invalid statistics integer.");
    }
    internal static List<Field> Fields(byte[] data)
    {
        if(data.Length>8192)throw new InvalidDataException("Statistics message exceeds limit.");
        var result=new List<Field>();int at=0;
        while(at<data.Length)
        {
            if(result.Count>=128)throw new InvalidDataException("Too many statistics fields.");
            ulong tag=Varint(data,ref at);int field=checked((int)(tag>>3)),wire=(int)(tag&7);
            if(field==0)throw new InvalidDataException("Invalid statistics tag.");
            ulong value=0;byte[] bytes=Array.Empty<byte>();
            if(wire==0)value=Varint(data,ref at);
            else
            {
                int size=wire switch{1=>8,5=>4,2=>checked((int)Varint(data,ref at)),_=>throw new InvalidDataException("Unsupported statistics field.")};
                if(size<0 || size>data.Length-at)throw new InvalidDataException("Truncated statistics field.");
                bytes=data.AsSpan(at,size).ToArray();at+=size;
            }
            result.Add(new(field,wire,value,bytes));
        }
        return result;
    }
    internal static Field? One(List<Field> fields,int number,int wire)
    {
        var matches=fields.Where(f=>f.Number==number).ToArray();
        if(matches.Length>1 || matches.Length==1&&matches[0].Wire!=wire)throw new InvalidDataException("Ambiguous statistics field.");
        return matches.SingleOrDefault();
    }
    internal static ulong Integer(List<Field> fields,int number)=>One(fields,number,0)?.Value??0;
    internal static byte[] Data(List<Field> fields,int number)=>One(fields,number,2)?.Bytes??Array.Empty<byte>();
    internal static void PutVarint(Stream output,ulong value)
    {while(value>=128){output.WriteByte((byte)(value|128));value>>=7;}output.WriteByte((byte)value);}
    internal static byte[] IntegerField(int number,ulong value)
    {using var output=new MemoryStream();PutVarint(output,(ulong)(number<<3));PutVarint(output,value);return output.ToArray();}
    internal static byte[] BytesField(int number,byte[] bytes)
    {using var output=new MemoryStream();PutVarint(output,(ulong)((number<<3)|2));PutVarint(output,(ulong)bytes.Length);output.Write(bytes);return output.ToArray();}
    internal static byte[] Delimited(byte[] bytes)
    {using var output=new MemoryStream();PutVarint(output,(ulong)bytes.Length);output.Write(bytes);return output.ToArray();}
}

public static class WireSockStatistics
{
    internal const string Method="/WireSockGrpcAPI.WireSockGrpc/RpcGetConnectionStatus";
    const string Pipe="WireSockControl";
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process,int flags,StringBuilder name,ref int size);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,int size,out int needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryServiceConfig(IntPtr service,IntPtr config,int size,out int needed);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)]struct ServiceStatus
    {public uint Type,State,Controls,Win32Exit,ServiceExit,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]struct ServiceConfig
    {public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    static bool ServiceOwns(uint pid,string expected)
    {
        IntPtr manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)return false;
        try
        {
            IntPtr service=OpenService(manager,"WireSockConnectService",5);if(service==IntPtr.Zero)return false;
            try
            {
                if(!QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<ServiceStatus>(),out _) || status.State!=4 || status.Pid!=pid)return false;
                QueryServiceConfig(service,IntPtr.Zero,0,out int size);if(size<Marshal.SizeOf<ServiceConfig>() || size>16384)return false;
                IntPtr buffer=Marshal.AllocHGlobal(size);
                try
                {
                    if(!QueryServiceConfig(service,buffer,size,out _))return false;
                    string image=Marshal.PtrToStringUni(Marshal.PtrToStructure<ServiceConfig>(buffer).Image)??"";
                    return string.Equals(image.Trim(),'"'+expected+'"',StringComparison.OrdinalIgnoreCase) || string.Equals(image.Trim(),expected,StringComparison.OrdinalIgnoreCase);
                }
                finally{Marshal.FreeHGlobal(buffer);}
            }
            finally{CloseServiceHandle(service);}
        }
        finally{CloseServiceHandle(manager);}
    }
    static string Installation(string cli)
    {
        string full=Path.GetFullPath(cli),folder=Path.GetDirectoryName(full)!;
        if(!string.Equals(Path.GetFileName(full),"wiresock-connect-cli.exe",StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(folder),"command-line",StringComparison.OrdinalIgnoreCase) || !File.Exists(full))throw new IOException("Unsupported WireSock installation.");
        var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)};
        if(!roots.Any(root=>root.Length>0 && full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))throw new IOException("WireSock must be installed in Program Files.");
        for(var current=new DirectoryInfo(folder);current!=null;current=current.Parent)
            if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked WireSock installation is unsupported.");
        var version=FileVersionInfo.GetVersionInfo(full);
        if(version.FileMajorPart!=3 || version.FileMinorPart is <4 or >6)throw new NotSupportedException("Unsupported WireSock statistics contract.");
        return Directory.GetParent(folder)!.FullName;
    }
    static bool TrustedServer(NamedPipeClientStream pipe,string root)
    {
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid))return false;
        string expected=Path.Combine(root,"service","wiresock-connect-service.exe");
        if(!File.Exists(expected) || (File.GetAttributes(expected)&FileAttributes.ReparsePoint)!=0 || (File.GetAttributes(Path.GetDirectoryName(expected)!)&FileAttributes.ReparsePoint)!=0)return false;
        // Some service DACLs deny OpenProcess even for QueryLimitedInformation.
        // SCM's read-only status/config rights provide an independent owner check.
        using var handle=OpenProcess(0x1000,false,checked((int)pid));if(handle.IsInvalid)return ServiceOwns(pid,expected);
        var name=new StringBuilder(32768);int length=name.Capacity;
        if(!QueryFullProcessImageName(handle,0,name,ref length))return ServiceOwns(pid,expected);
        return string.Equals(name.ToString(),expected,StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<WireSockSample> Read(string cli,string expectedProfile="SmartRouting-AWG2",CancellationToken stop=default)
    {
        try
        {
            string root=Installation(cli);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(1500);
            using var pipe=new NamedPipeClientStream(".",Pipe,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token);
            if(!TrustedServer(pipe,root))return new(){Code="UntrustedService"};
            byte[] response=await Query(pipe,timeout.Token);stop.ThrowIfCancellationRequested();
            return Parse(response,expectedProfile,DateTime.UtcNow);
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}
        catch(NotSupportedException){return new(){Code="UnsupportedVersion"};}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or Win32Exception or OverflowException or ArgumentException)
        {return new(){Code=ex is OperationCanceledException?"Timeout":ex is InvalidDataException?"InvalidResponse":"Unavailable"};}
    }
    internal static byte[] Request()
    {
        // Exactly one read-only RPC, empty current-profile request. No Hello, Connect,
        // Disconnect, config/profile requests or SDK loading can be selected here.
        byte[] init=StatsProto.BytesField(1,Encoding.UTF8.GetBytes(Method));
        byte[] deadline=StatsProto.IntegerField(1,(ulong)DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds());
        init=init.Concat(StatsProto.BytesField(2,deadline)).ToArray();
        return StatsProto.Delimited(StatsProto.BytesField(1,init))
            .Concat(StatsProto.Delimited(StatsProto.BytesField(2,Array.Empty<byte>())))
            .Concat(StatsProto.Delimited(StatsProto.BytesField(3,StatsProto.IntegerField(2,1))))
            .Concat(StatsProto.Delimited(StatsProto.IntegerField(4,2))).ToArray();
    }
    internal static async Task<byte[]> Query(Stream pipe,CancellationToken stop)
    {
        await pipe.WriteAsync(Request(),stop);await pipe.FlushAsync(stop);
        byte[]? response=null;int total=0;
        async Task<byte[]> Read(int size)
        {
            if(size<0 || size>8192 || (total+=size)>32768)throw new InvalidDataException("Statistics response exceeds budget.");
            var data=new byte[size];await pipe.ReadExactlyAsync(data,stop);return data;
        }
        async Task<int> Length()
        {
            uint value=0;for(int shift=0;shift<=28;shift+=7){byte b=(await Read(1))[0];if(shift==28 && b>15)throw new InvalidDataException("Invalid frame size.");value|=(uint)(b&127)<<shift;if(b<128)return checked((int)value);}
            throw new InvalidDataException("Invalid frame size.");
        }
        for(int count=0;count<16;count++)
        {
            var frame=StatsProto.Fields(await Read(await Length()));
            if(frame.Count!=1)throw new InvalidDataException("Ambiguous statistics frame.");
            var part=frame[0];if(part.Wire!=2)throw new InvalidDataException("Invalid statistics frame.");
            if(part.Number==2)continue; // response headers
            if(part.Number==3)
            {
                if(response!=null)throw new InvalidDataException("Multiple statistics responses.");
                var info=StatsProto.Fields(part.Bytes);int size=checked((int)StatsProto.Integer(info,1));
                if(size==0)throw new InvalidDataException("Empty statistics response.");
                if(StatsProto.Integer(info,2)>1)throw new InvalidDataException("Invalid payload framing.");
                response=await Read(size); // byte-mode pipe handles either payload placement.
            }
            else if(part.Number==5)
            {
                if(StatsProto.Integer(StatsProto.Fields(part.Bytes),2)!=0 || response==null)throw new IOException("Statistics service refused the query.");
                return response;
            }
            else throw new InvalidDataException("Unsupported statistics frame.");
        }
        throw new InvalidDataException("Statistics frame budget exceeded.");
    }
    internal static WireSockSample Parse(byte[] bytes,string expectedProfile,DateTime now)
    {
        var fields=StatsProto.Fields(bytes);
        int state=checked((int)StatsProto.Integer(fields,1));byte[] profileBytes=StatsProto.Data(fields,3);
        if(profileBytes.Length>512)throw new InvalidDataException("Invalid statistics profile.");
        string profile=new UTF8Encoding(false,true).GetString(profileBytes);
        if(state is <0 or >6)throw new InvalidDataException("Invalid connection state.");
        if(profile.Length==0 && state!=3)return new(){Code="NotConnected",ObservedUtc=now};
        if(!string.Equals(profile,expectedProfile,StringComparison.OrdinalIgnoreCase))return new(){Code="ForeignProfile",ObservedUtc=now};
        var sample=new WireSockSample{Profile=profile,ConnectionState=state,ObservedUtc=now,Code="NotConnected"};
        if(state!=3)return sample;
        if(StatsProto.Integer(fields,4)!=0){sample.Code="ServiceError";return sample;}
        var payload=StatsProto.One(fields,11,2);if(payload==null){sample.Code="NoStatistics";return sample;}
        var stats=StatsProto.Fields(payload.Bytes);
        sample.TxBytes=StatsProto.Integer(stats,1);sample.RxBytes=StatsProto.Integer(stats,2);
        int rtt=unchecked((int)StatsProto.Integer(stats,6));
        float loss=StatsProto.One(stats,5,5) is {} value?BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value.Bytes)):0;
        if(!float.IsFinite(loss) || loss<0 || loss>1 || rtt<0 || rtt>120000)throw new InvalidDataException("Invalid native statistics.");
        var duration=StatsProto.Fields(StatsProto.Data(stats,7));
        ulong seconds=StatsProto.Integer(duration,1);if(seconds>315576000)throw new InvalidDataException("Invalid connection duration.");sample.UptimeSeconds=(long)seconds;
        sample.RttMs=rtt>0?rtt:null;
        // Zero traffic cannot establish a packet-loss estimate, even if proto3 omitted zero fields.
        sample.EstimatedLossPercent=sample.RxBytes>0?Math.Round(loss*100.0,2):null;
        sample.Available=true;sample.Code="NativeStatistics";return sample;
    }
}
