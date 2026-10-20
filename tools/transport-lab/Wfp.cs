using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cita.TransportLab;

// Development-only BFE experiment. No reference from the shipping agent/client.
// ABI: Microsoft WinSDK fwpmtypes.h, fwptypes.h and fwpmu.h (x64 only).
internal sealed class Wfp : IDisposable
{
    internal static readonly Guid V4=new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), V6=new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    static readonly Guid AppId=new("d78e1e87-8644-4ea5-9437-d809ecefc971"), LocalInterface=new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    const uint NotFound=0x80320003, SubLayerNotFound=0x80320007;
    IntPtr engine;
    internal Wfp()
    {
        ValidateAbi();
        Check(FwpmEngineOpen0(null,10,IntPtr.Zero,IntPtr.Zero,out engine),"Open BFE (administrator required)");
    }
    internal static void ValidateAbi()
    {
        if(IntPtr.Size!=8 || Marshal.SizeOf<Value>()!=16 || Marshal.SizeOf<Condition>()!=40 || Marshal.SizeOf<Filter>()!=200 || Marshal.SizeOf<SubLayer>()!=72)
            throw new PlatformNotSupportedException("The transport lab requires the Windows x64 WFP ABI.");
    }
    static void Check(uint code,string operation){if(code!=0)throw new Win32Exception(unchecked((int)code),operation+": 0x"+code.ToString("X8"));}
    internal static ulong Luid(uint index){Check(ConvertInterfaceIndexToLuid(index,out ulong luid),"Resolve interface LUID");return luid;}
    internal void Replace(Fixture fixture,ulong? allowedLuid)
    {
        fixture.Validate();
        Check(FwpmGetAppIdFromFileName0(fixture.Image,out IntPtr app),"Resolve fixture application ID");
        IntPtr luid=Marshal.AllocHGlobal(8),conditions=IntPtr.Zero;
        try
        {
            Marshal.WriteInt64(luid,unchecked((long)(allowedLuid??0)));
            Transaction(()=>
            {
                foreach(Guid id in fixture.FilterKeys)DeleteFilter(fixture,id);
                if(!HasSubLayer(fixture))
                {
                    var sub=new SubLayer{Key=fixture.SubLayer,Display=new(){Name=fixture.Label},Flags=1,Weight=0x7fff};
                    Check(FwpmSubLayerAdd0(engine,ref sub,IntPtr.Zero),"Create fixture sublayer");
                }
                for(int i=0;i<4;i++)
                {
                    // No PERMIT filters: this experiment cannot override another firewall's block.
                    var list=new List<Condition>{new(){Field=AppId,Value=new(){Type=12,Pointer=app}}};
                    if(allowedLuid.HasValue)list.Add(i%2==0
                        ?new(){Field=LocalInterface,Match=10,Value=new(){Type=4,Pointer=luid}}
                        :new(){Field=LocalInterface,Value=new(){Type=0}});
                    else if(i%2==1)continue;
                    conditions=Marshal.AllocHGlobal(list.Count*Marshal.SizeOf<Condition>());
                    try
                    {
                        for(int c=0;c<list.Count;c++)Marshal.StructureToPtr(list[c],conditions+c*Marshal.SizeOf<Condition>(),false);
                        var filter=new Filter{Key=fixture.FilterKeys[i],Display=new(){Name=fixture.Label},Flags=1,
                            Layer=i<2?V4:V6,SubLayer=fixture.SubLayer,Count=(uint)list.Count,Conditions=conditions,Action=new(){Type=0x1001}};
                        Check(FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out _),"Install fixture block filter");
                    }
                    finally{Marshal.FreeHGlobal(conditions);conditions=IntPtr.Zero;}
                }
            });
        }
        finally{Marshal.FreeHGlobal(luid);FwpmFreeMemory0(ref app);}
    }
    internal void Remove(Fixture fixture)
    {
        fixture.Validate();
        Transaction(()=>
        {
            foreach(Guid key in fixture.FilterKeys)DeleteFilter(fixture,key);
            if(HasSubLayer(fixture))Check(FwpmSubLayerDeleteByKey0(engine,fixture.SubLayer),"Delete fixture sublayer");
        });
        foreach(Guid key in fixture.FilterKeys)
        {
            uint code=FwpmFilterGetByKey0(engine,key,out IntPtr ptr);
            if(code==0)FwpmFreeMemory0(ref ptr);
            if(code!=NotFound)throw new IOException("Fixture filter cleanup was not confirmed.");
        }
        if(HasSubLayer(fixture))throw new IOException("Fixture sublayer cleanup was not confirmed.");
    }
    void DeleteFilter(Fixture fixture,Guid key)
    {
        uint code=FwpmFilterGetByKey0(engine,key,out IntPtr ptr);
        if(code==NotFound)return;
        Check(code,"Read fixture filter before removal");
        try
        {
            var value=Marshal.PtrToStructure<Filter>(ptr);
            if(value.SubLayer!=fixture.SubLayer || value.Display.Name!=fixture.Label || value.Action.Type!=0x1001 || value.Count is <1 or >2)
                throw new IOException("Refusing to remove an unrecognized filter.");
            var first=Marshal.PtrToStructure<Condition>(value.Conditions);
            Check(FwpmGetAppIdFromFileName0(fixture.Image,out IntPtr expected),"Resolve fixture identity for cleanup");
            try
            {
                if(first.Field!=AppId || first.Match!=0 || first.Value.Type!=12 || !SameBlob(first.Value.Pointer,expected))
                    throw new IOException("Refusing to remove another application's filter.");
            }
            finally{FwpmFreeMemory0(ref expected);}
        }
        finally{FwpmFreeMemory0(ref ptr);}
        Check(FwpmFilterDeleteByKey0(engine,key),"Delete fixture block filter");
    }
    static bool SameBlob(IntPtr a,IntPtr b)
    {
        var x=Marshal.PtrToStructure<Blob>(a);var y=Marshal.PtrToStructure<Blob>(b);
        if(x.Size!=y.Size||x.Size>32768)return false;
        byte[] one=new byte[x.Size],two=new byte[y.Size];Marshal.Copy(x.Data,one,0,one.Length);Marshal.Copy(y.Data,two,0,two.Length);
        return one.AsSpan().SequenceEqual(two);
    }
    bool HasSubLayer(Fixture fixture)
    {
        uint code=FwpmSubLayerGetByKey0(engine,fixture.SubLayer,out IntPtr ptr);
        if(code==SubLayerNotFound)return false;
        Check(code,"Read fixture sublayer");
        try
        {
            var sub=Marshal.PtrToStructure<SubLayer>(ptr);
            if(sub.Display.Name!=fixture.Label)throw new IOException("Refusing an unrecognized sublayer.");
            return true;
        }
        finally{FwpmFreeMemory0(ref ptr);}
    }
    void Transaction(Action action)
    {
        Check(FwpmTransactionBegin0(engine,0),"Begin fixture transaction");
        try{action();Check(FwpmTransactionCommit0(engine),"Commit fixture transaction");}
        catch{FwpmTransactionAbort0(engine);throw;}
    }
    public void Dispose(){if(engine!=IntPtr.Zero){FwpmEngineClose0(engine);engine=IntPtr.Zero;}}
    [StructLayout(LayoutKind.Sequential)] struct Display{[MarshalAs(UnmanagedType.LPWStr)]public string? Name;[MarshalAs(UnmanagedType.LPWStr)]public string? Description;}
    [StructLayout(LayoutKind.Sequential)] struct Blob{public uint Size;public IntPtr Data;}
    [StructLayout(LayoutKind.Explicit,Size=16)] struct Value{[FieldOffset(0)]public uint Type;[FieldOffset(8)]public IntPtr Pointer;}
    [StructLayout(LayoutKind.Sequential)] struct Condition{public Guid Field;public uint Match;public Value Value;}
    [StructLayout(LayoutKind.Sequential)] struct ActionValue{public uint Type;public Guid Key;}
    [StructLayout(LayoutKind.Explicit,Size=16)] struct Context{[FieldOffset(0)]public ulong Raw;[FieldOffset(0)]public Guid Key;}
    [StructLayout(LayoutKind.Sequential)] struct Filter
    {
        public Guid Key;public Display Display;public uint Flags;public IntPtr Provider;public Blob Data;
        public Guid Layer,SubLayer;public Value Weight;public uint Count;public IntPtr Conditions;public ActionValue Action;
        public Context Context;public IntPtr Reserved;public ulong Id;public Value EffectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)] struct SubLayer{public Guid Key;public Display Display;public uint Flags;public IntPtr Provider;public Blob Data;public ushort Weight;}
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)]static extern uint FwpmEngineOpen0(string? server,uint auth,IntPtr identity,IntPtr session,out IntPtr engine);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmTransactionBegin0(IntPtr engine,uint flags);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)]static extern uint FwpmGetAppIdFromFileName0(string path,out IntPtr appId);
    [DllImport("fwpuclnt.dll")]static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)]static extern uint FwpmSubLayerAdd0(IntPtr engine,ref SubLayer sub,IntPtr security);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmSubLayerGetByKey0(IntPtr engine,in Guid key,out IntPtr sub);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine,in Guid key);
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)]static extern uint FwpmFilterAdd0(IntPtr engine,ref Filter filter,IntPtr security,out ulong id);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmFilterGetByKey0(IntPtr engine,in Guid key,out IntPtr filter);
    [DllImport("fwpuclnt.dll")]static extern uint FwpmFilterDeleteByKey0(IntPtr engine,in Guid key);
    [DllImport("iphlpapi.dll")]static extern uint ConvertInterfaceIndexToLuid(uint index,out ulong luid);
}
