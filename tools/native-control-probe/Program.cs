using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

if(args.Length==2&&args[0]=="--fake") { await Fake(args[1]); return; }
if(args.Length!=1)throw new ArgumentException("Agent assembly required");
var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
var type=assembly.GetType("RoutePilot.Agent.NativeCoreBinding",true)!;
var flags=BindingFlags.NonPublic|BindingFlags.Instance;
var command=type.GetMethod("CommandAsync",flags)!;
var field=type.GetField("process",flags)!;
foreach(string mode in new[]{"bad-policy","late-policy","rejected","bad-scope","bad-retirement","retirement-pending","bad-owner","bad-generation","bad-revision","missing-digest","bad-telemetry","concurrent","cancel-after-write","cancel-before-write","ok","connector-replace"})
{
    object binding=Activator.CreateInstance(type,flags,null,new object?[]{"fixture","fixture","fixture","owner","",null,null,null,false,null,null},null)!;
    var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    if(Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("--fake");info.ArgumentList.Add(mode);
    using var child=Process.Start(info)!;var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var snapshotReceived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var received=new System.Collections.Concurrent.ConcurrentQueue<string>();
    var stderr=Task.Run(async()=>{while(await child.StandardError.ReadLineAsync() is string line){if(line.StartsWith("got:"))received.Enqueue(line[4..]);if(line=="got:Snapshot")snapshotReceived.TrySetResult();if(line=="received")ready.TrySetResult();}});field.SetValue(binding,child);
    async Task<JsonElement> Send(string action,ulong? expected=null,string? digest=null,object? expectedScope=null,CancellationToken stop=default)
    {
        object?[] parameters={action,"session",1UL,0,"",true,stop,null,null,null,0UL,null,expected,digest,expectedScope,null};
        return await (Task<JsonElement>)command.Invoke(binding,parameters)!;
    }
    try
    {
        if(mode=="connector-replace")
        {
            type.GetField("session",flags)!.SetValue(binding,"session");type.GetField("generation",flags)!.SetValue(binding,1UL);
            type.GetField("connectorAdmission",flags)!.SetValue(binding,true);type.GetProperty("SupportsZapretConnector",flags)!.SetValue(binding,true);
            var requestType=assembly.GetType("RoutePilot.Agent.NativeConnectorRequest",true)!;
            var connectionType=assembly.GetType("RoutePilot.Agent.INativeConnectorConnection",true)!;
            var request=System.Linq.Expressions.Expression.Parameter(requestType);var token=System.Linq.Expressions.Expression.Parameter(typeof(CancellationToken));
            var authorizeType=typeof(Func<,>).MakeGenericType(requestType,typeof(bool));
            var authorize=System.Linq.Expressions.Expression.Lambda(authorizeType,System.Linq.Expressions.Expression.Constant(false),request).Compile();
            var taskType=typeof(Task<>).MakeGenericType(connectionType);var factoryType=typeof(Func<,,>).MakeGenericType(requestType,typeof(CancellationToken),taskType);
            var factory=System.Linq.Expressions.Expression.Lambda(factoryType,System.Linq.Expressions.Expression.Throw(System.Linq.Expressions.Expression.New(typeof(InvalidOperationException)),taskType),request,token).Compile();
            var admission=Activator.CreateInstance(assembly.GetType("RoutePilot.Agent.NativeConnectorAdmission",true)!,true)!;
            var start=type.GetMethod("StartOwnedConnectorAsync",flags)!;var healthy=type.GetMethod("IsOwnedConnectorReady",flags)!;
            async Task Start(string epoch,bool replace,CancellationToken stop=default)=>await (Task)start.Invoke(binding,new object[]{epoch,(Func<bool>)(()=>true),authorize,factory,CancellationToken.None,stop,admission,replace})!;
            bool Healthy(string epoch)=>(bool)healthy.Invoke(binding,new object[]{epoch})!;
            string oldEpoch=Guid.NewGuid().ToString("N"),newEpoch=Guid.NewGuid().ToString("N");
            try
            {
                await Start(oldEpoch,false);if(!Healthy(oldEpoch))throw new Exception("Owned connector did not become healthy.");
                bool denied=false;try{await Start(newEpoch,false);}catch(InvalidOperationException){denied=true;}
                if(!denied||!Healthy(oldEpoch))throw new Exception("Unrequested replacement disturbed the live connector.");
                using(var canceled=new CancellationTokenSource())
                {canceled.Cancel();denied=false;try{await Start(newEpoch,true,canceled.Token);}catch(OperationCanceledException){denied=true;}if(!denied||!Healthy(oldEpoch))throw new Exception("Canceled replacement disturbed its current owner.");}
                await Start(newEpoch,true);if(Healthy(oldEpoch)||!Healthy(newEpoch))throw new Exception("Replacement did not retire the old epoch before opening a new one.");
            }
            finally{await (Task)type.GetMethod("StopOwnedConnectorAsync",flags)!.Invoke(binding,new object[]{CancellationToken.None})!;}
            if(Healthy(newEpoch))throw new Exception("Retired connector was reported healthy.");
            Console.WriteLine(mode+": PASS");continue;
        }
        if(mode=="cancel-before-write")
        {
            using var canceled=new CancellationTokenSource();canceled.Cancel();bool refused=false;
            try{await Send("Policy",2,"ABC",stop:canceled.Token);}catch(OperationCanceledException){refused=true;}
            if(!refused)throw new Exception("Cancellation before write ignored");
            await Send("Snapshot");Console.WriteLine(mode+": PASS");continue;
        }
        if(mode is "concurrent" or "cancel-after-write")
        {
            using var cancellation=new CancellationTokenSource();var first=Send("Policy",2,"ABC",stop:cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task<JsonElement>? second=mode=="concurrent"?Send("Snapshot"):null;
            if(second!=null&&second.IsCompleted)throw new Exception("Concurrent command bypassed pending policy");
            if(mode=="cancel-after-write")cancellation.Cancel();
            var ack=await first.WaitAsync(TimeSpan.FromSeconds(3));
            if(ack.GetProperty("stage").GetString()!="Policy")throw new Exception("Wrong policy response");
            var snap=await (second??Send("Snapshot")).WaitAsync(TimeSpan.FromSeconds(3));
            if(snap.GetProperty("stage").GetString()!="Snapshot")throw new Exception("Reply ordering mismatch");
            await snapshotReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));if(!received.ToArray().SequenceEqual(new[]{"Policy","Snapshot"}))throw new Exception("Fixture observed unexpected command order");
            Console.WriteLine(mode+": PASS");continue;
        }
        bool failed=false;
        if(mode is "bad-scope" or "bad-retirement" or "retirement-pending")
        {
            var permission=assembly.GetType("RoutePilot.Agent.NativeDirectPermission",true)!;
            var scope=Activator.CreateInstance(permission,flags|BindingFlags.Public,null,new object[]{@"C:\fixture.exe","1.1.1.1",(ushort)443,"tcp",1U},null)!;
            try{await Send(mode=="bad-scope"?"EnableDirect":"RetireDirect",expectedScope:scope);}catch(IOException){failed=true;}
        }
        else try{await Send("Policy",2,"ABC");}catch(Exception e)when(e is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException){failed=true;}
        if(failed!=(mode is not ("ok" or "retirement-pending")))throw new Exception(mode+": unexpected first result");
        if(failed&&Convert.ToInt64(type.GetProperty("PolicyDirectDials",flags)!.GetValue(binding))!=0)throw new Exception(mode+": unverified telemetry was applied");
        bool nextFailed=false;try{await Send("Snapshot");}catch(IOException){nextFailed=true;}
        if(nextFailed!=(mode is "bad-policy" or "late-policy" or "bad-scope" or "bad-retirement" or "bad-owner" or "bad-generation" or "bad-revision" or "missing-digest" or "bad-telemetry"))throw new Exception(mode+": unexpected subsequent command");
        Console.WriteLine(mode+": PASS");
    }
    finally
    {
        try{child.StandardInput.Close();}catch{}
        if(!child.WaitForExit(6000)){child.Kill();if(!child.WaitForExit(5000))throw new Exception("Owned fake core did not exit");}
        await stderr.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
var calibration=assembly.GetType("RoutePilot.Agent.NativeCalibrationTests",true)!;
await (Task)calibration.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
Console.WriteLine("calibration: PASS");
bool lockCovered=(bool)calibration.GetProperty("LockWaitCovered",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
Console.WriteLine("calibration-lock: "+(lockCovered?"PASS":"SKIP"));
var ownership=assembly.GetType("RoutePilot.Agent.NativeConnectorOwnershipTests",true)!;
await (Task)ownership.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
Console.WriteLine("connector-ownership: PASS");
var zapretAuth=assembly.GetType("RoutePilot.Agent.NativeZapretAuthorizationTests",true)!;
await (Task)zapretAuth.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
Console.WriteLine("zapret-authorization: PASS");
foreach(string name in new[]{"NativeConnectorAdmissionTests","NativeCoreConnectorAuthorizationRaceTests","NativeZapretPolicyGateTests","NativeZapretProbeTests","NativeZapretManifestTests","NativeAutomaticPolicyTests","NativeProcessJobTests","NativeCoreRecoveryFailureTests"})
{
    var test=assembly.GetType("RoutePilot.Agent."+name,true)!;
    await (Task)test.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
    Console.WriteLine((name switch{"NativeConnectorAdmissionTests"=>"connector-admission","NativeCoreConnectorAuthorizationRaceTests"=>"connector-authorization-race","NativeZapretPolicyGateTests"=>"zapret-policy-gate","NativeZapretProbeTests"=>"zapret-probe","NativeZapretManifestTests"=>"zapret-manifest","NativeAutomaticPolicyTests"=>"policy-staging","NativeProcessJobTests"=>"atomic-process","NativeCoreRecoveryFailureTests"=>"core-recovery-failure",_=>throw new InvalidOperationException()})+": PASS");
}

static async Task Fake(string mode)
{
    while(await Console.In.ReadLineAsync() is string line)
    {
        using var doc=JsonDocument.Parse(line);var c=doc.RootElement;
        string action=c.GetProperty("Action").GetString()!;
        await Console.Error.WriteLineAsync("got:"+action);await Console.Error.FlushAsync();
        if(mode=="cancel-before-write"&&action=="Policy")throw new Exception("Canceled command was sent");
        if(mode is "concurrent" or "cancel-after-write" && action=="Policy"){await Console.Error.WriteLineAsync("received");await Console.Error.FlushAsync();await Task.Delay(400);}
        if(mode=="late-policy"&&action=="Policy")await Task.Delay(5000);
        var reply=new Dictionary<string,object?>{{"stage",action},{"owner",c.GetProperty("Owner").GetString()},{"session",c.GetProperty("Session").GetString()},{"generation",c.GetProperty("Generation").GetUInt64()},{"success",!(mode=="rejected"&&action=="Policy")},{"policyRevision",2},{"policyDigest",mode=="bad-policy"?"BAD":"ABC"},{"policyDirectDials",123}};
        if(mode=="bad-owner"&&action=="Policy")reply["owner"]="foreign";
        if(mode=="bad-generation"&&action=="Policy")reply["generation"]=2;
        if(mode=="bad-revision"&&action=="Policy")reply["policyRevision"]="2";
        if(mode=="missing-digest"&&action=="Policy")reply.Remove("policyDigest");
        if(mode=="bad-telemetry"&&action=="Policy")reply["policyBlockedDials"]="invalid";
        if(action=="EnableDirect"){reply["policyRevision"]=0;reply["scope"]=new{Address="8.8.8.8",Network="tcp",Port=443};}
        if(action=="RetireDirect"){reply["policyRevision"]=0;reply["scope"]=new{Address="1.1.1.1",Network="tcp",Port=443};if(mode=="bad-retirement")reply["scopeRetired"]="false";}
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(reply));await Console.Out.FlushAsync();
    }
}
