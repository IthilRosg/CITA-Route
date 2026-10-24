using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot.Agent;

internal static class WorkloadLab
{
    internal static async Task<int> ExportSource(string root)
    {
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));var stop=budget.Token;
            string image=Path.Combine(root,"native-awg-lab.exe"),config=await File.ReadAllTextAsync(Path.Combine(root,"private","second-server.conf"),stop),expected=(await File.ReadAllTextAsync(Path.Combine(root,"private","second-server-egress.txt"),stop)).Trim();
            string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));await using var backend=new NativeAwgBackend(image);
            var ready=await backend.StartAsync(JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1UL,Token=token,Config=config}),stop);
            var probe=await StartupLab.Probe(image,"--proxy-client",new{Port=ready.GetProperty("port").GetInt32(),Token=token,LiteralIPv4=true,ExportExit=true},stop);
            if(!probe.GetProperty("success").GetBoolean()||!string.Equals(probe.GetProperty("egressHash").GetString(),expected,StringComparison.OrdinalIgnoreCase)||!probe.TryGetProperty("egressIPv4",out var ip))throw new InvalidDataException();
            string address=ip.GetString()!;if(!System.Net.IPAddress.TryParse(address,out var parsed)||parsed.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)throw new InvalidDataException();
            await File.WriteAllTextAsync(Path.Combine(root,"private","second-server-source.txt"),address,stop);Console.WriteLine("{\"success\":true}");return 0;
        }
        catch{Console.WriteLine("{\"success\":false}");return 1;}
    }
    internal static async Task<int> Run(string root,string? coreConfig=null,string? owner=null)
    {
        bool success=false;string? errorType=null;JsonElement result=default,before=default,after=default;long peakPrivate=0,peakWorking=0;int peakHandles=0,samples=0;double cpuSeconds=0;var seen=new Dictionary<(int,long),double>();var clock=Stopwatch.StartNew();
        try
        {
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(90));var stop=budget.Token;
            string native=Path.Combine(root,"native-awg-lab.exe"),peer=await File.ReadAllTextAsync(Path.Combine(root,"private",coreConfig==null?"peer.conf":"second-server.conf"),stop);
            string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var spec=JsonSerializer.SerializeToElement(new{Session=Guid.NewGuid().ToString("N"),Generation=1UL,Token=token,Config=peer});
            await using var backend=new NativeAwgBackend(native);var ready=await backend.StartAsync(spec,stop);
            JsonElement Command()=>JsonSerializer.SerializeToElement(new{Session=spec.GetProperty("Session").GetString(),Generation=1UL,Action="Snapshot"});
            before=await backend.SendAsync(Command(),stop);
            var privateSpec=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(await File.ReadAllTextAsync(Path.Combine(root,"private","workload.json"),stop))!;
            privateSpec["Port"]=JsonSerializer.SerializeToElement(ready.GetProperty("port").GetInt32());privateSpec["Token"]=JsonSerializer.SerializeToElement(token);
            using var reference=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"..","server-egress-reference.json"),stop));
            string expectedExit=coreConfig==null?reference.RootElement.GetProperty("serverEgressHash").GetString()!:(await File.ReadAllTextAsync(Path.Combine(root,"private","second-server-egress.txt"),stop)).Trim();
            await using var binding=coreConfig==null?null:new NativeCoreBinding(Path.Combine(root,"native-core-lab.exe"),coreConfig,native,owner!,expectedExit);
            if(binding!=null)
            {
                await binding.StartAsync(stop);
                using(var identity=Process.GetProcessById(binding.ProcessId))await File.WriteAllTextAsync(Path.Combine(root,"private","binding-core-pid.json"),JsonSerializer.Serialize(new{pid=identity.Id,ticks=identity.StartTime.ToUniversalTime().Ticks}),stop);
                await binding.QuarantineAsync(spec,stop);await binding.ActivateAsync(backend,spec,ready,stop);
                privateSpec["OsTun"]=JsonSerializer.SerializeToElement(true);
            }
            var info=new ProcessStartInfo(Path.Combine(root,"native-workload-lab.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};using var process=Process.Start(info)!;_=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(privateSpec));process.StandardInput.Close();privateSpec.Clear();var reply=process.StandardOutput.ReadLineAsync(stop).AsTask();
                while(!reply.IsCompleted)
                {
                    long privateTotal=0,working=0;int handles=0;
                    foreach(string name in new[]{"native-awg-lab","native-workload-lab","native-core-lab"})foreach(var p in Process.GetProcessesByName(name))
                    {
                        using(p){try{if(p.HasExited)continue;var key=(p.Id,p.StartTime.ToUniversalTime().Ticks);double cpu=p.TotalProcessorTime.TotalSeconds;if(seen.TryGetValue(key,out double previous))cpuSeconds+=Math.Max(0,cpu-previous);else cpuSeconds+=cpu;seen[key]=cpu;privateTotal+=p.PrivateMemorySize64;working+=p.WorkingSet64;handles+=p.HandleCount;}catch(InvalidOperationException){}}
                    }
                    peakPrivate=Math.Max(peakPrivate,privateTotal);peakWorking=Math.Max(peakWorking,working);peakHandles=Math.Max(peakHandles,handles);samples++;await Task.Delay(200,stop);
                }
                using var doc=JsonDocument.Parse(await reply??throw new IOException());result=doc.RootElement.Clone();await process.WaitForExitAsync(stop);success=process.ExitCode==0&&result.GetProperty("success").GetBoolean();
                after=await backend.SendAsync(Command(),stop);
            }
            finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
        }
        catch(Exception e){errorType=e.GetType().Name;}
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,errorType,result=result.ValueKind==JsonValueKind.Undefined?(JsonElement?)null:result,before=before.ValueKind==JsonValueKind.Undefined?(JsonElement?)null:before,after=after.ValueKind==JsonValueKind.Undefined?(JsonElement?)null:after,resources=new{samples,peakPrivateBytes=peakPrivate,peakWorkingBytes=peakWorking,peakHandles,cpuSeconds,elapsedMs=clock.ElapsedMilliseconds,logicalProcessors=Environment.ProcessorCount},scope="Concurrent native-proxy TLS downloads, paced TLS video and real QUIC stream/datagrams; synthetic game/voice cadence, not commercial applications"}));return success?0:1;
    }
}
