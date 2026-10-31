using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RoutePilot.Agent;
internal static class NativeConnectionLineageTests
{
    internal static void Run()
    {
        var kind=typeof(NativeServiceRoute);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var intentType=kind.GetNestedType("Intent",BindingFlags.NonPublic)!;
        object Intent(string text)=>JsonSerializer.Deserialize(text,intentType)!;
        void Field(object instance,string name,object? value)=>kind.GetField(name,flags)!.SetValue(instance,value);
        var check=kind.GetMethod("CheckConnectionIntent",BindingFlags.Static|BindingFlags.NonPublic)!;
        void Check(object value)=>check.Invoke(null,[value]);
        void Refused(Action action){try{action();}catch(Exception e)when(e is InvalidDataException or TargetInvocationException){return;}throw new IOException("Unsafe lineage operation was accepted.");}
        string run=new('c',32),op=new('d',32);
        string Legacy(bool desired)=>JsonSerializer.Serialize(new{Run=run,Generation=2,Desired=desired,Operation=op,Boot=""});
        Check(Intent(Legacy(false)));
        Refused(()=>Check(Intent(Legacy(true))));
        string scratch=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("CITA_TEST_SCRATCH_ROOT")??Path.GetTempPath()));
        string root=Path.Combine(scratch,"cita-lineage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string state=Path.Combine(root,"intent.json");File.WriteAllText(state,Legacy(false));
            var route=(NativeServiceRoute)RuntimeHelpers.GetUninitializedObject(kind);
            var bundle=new NativeTrustedBundle();
            typeof(NativeTrustedBundle).GetProperty("ProfileText",flags)!.SetValue(bundle,"fixture");
            Field(route,"bundle",bundle);Field(route,"statePath",state);Field(route,"sync",new object());Field(route,"signal",new SemaphoreSlim(0,1));
            Field(route,"intent",Intent(Legacy(false)));Field(route,"phase","Disconnected");
            string a=new('a',32),b=new('b',32);
            // Preallocated ID closes the lost-start-reply window.
            route.Request(true,op,a);
            string first=File.ReadAllText(state);using(var doc=JsonDocument.Parse(first)){if(doc.RootElement.GetProperty("ConnectionId").GetString()!=a||!doc.RootElement.GetProperty("Desired").GetBoolean())throw new IOException("Start did not persist caller lineage.");}
            route.Request(true,op,a);
            if(File.ReadAllText(state)!=first)throw new IOException("Same-ID replay was not idempotent.");
            Refused(()=>route.Request(true,op,b));
            var advance=kind.GetMethod("AdvanceConnectionAttempt",BindingFlags.Static|BindingFlags.NonPublic)!;
            object old=kind.GetField("intent",flags)!.GetValue(route)!;
            object repaired=advance.Invoke(null,[old])!;string repairJson=JsonSerializer.Serialize(repaired,intentType);
            using(var doc=JsonDocument.Parse(repairJson)){if(doc.RootElement.GetProperty("ConnectionId").GetString()!=a||doc.RootElement.GetProperty("Generation").GetUInt64()!=4||doc.RootElement.GetProperty("Operation").GetString()==op)throw new IOException("Repair did not preserve lineage.");}
            Check(repaired);Field(route,"intent",repaired);File.WriteAllText(state,repairJson);
            route.StopByConnectionId(a);string stopped=File.ReadAllText(state);
            route.StopByConnectionId(a);if(File.ReadAllText(state)!=stopped)throw new IOException("Repeated lineage stop wrote again.");
            Field(route,"phase","Disconnected");
            Refused(()=>route.Request(true,op,b)); // Previous operation was retired.
            using(var doc=JsonDocument.Parse(stopped)){op=doc.RootElement.GetProperty("Operation").GetString()!;}
            Refused(()=>route.Request(true,op,a)); // Retired identity cannot be reused.
            route.Request(true,op,b);
            string second=File.ReadAllText(state);using var inFlight=new CancellationTokenSource();Field(route,"operation",inFlight);
            Refused(()=>route.StopByConnectionId(a));
            if(inFlight.IsCancellationRequested||File.ReadAllText(state)!=second)throw new IOException("Stale stop touched a new connection.");
            route.StopByConnectionId(b);Field(route,"phase","Disconnected");
            using(var doc=JsonDocument.Parse(File.ReadAllText(state)))op=doc.RootElement.GetProperty("Operation").GetString()!;
            route.RequestImport(new NativeProfileImportRequest("eA==","fixture"),op);
            using(var doc=JsonDocument.Parse(File.ReadAllText(state)))if(doc.RootElement.GetProperty("ConnectionId").GetString()!="")throw new IOException("Profile import retained old connection lineage.");
            ((SemaphoreSlim)kind.GetField("signal",flags)!.GetValue(route)!).Dispose();bundle.Dispose();
        }
        finally{if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)),scratch,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root),@"\Acita-lineage-[a-f0-9]{32}\z"))throw new IOException("Lineage test cleanup target refused.");Directory.Delete(root,true);}
    }
}
