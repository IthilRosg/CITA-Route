using System.Text.Json;
using RoutePilot.Agent;

if(args.Length==2 && args[0]=="--startup-lab")return await StartupLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--servers-lab")return await ServersLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--workload-lab")return await WorkloadLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--export-second-exit-lab")return await WorkloadLab.ExportSource(Path.GetFullPath(args[1]));
if(args.Length==4 && args[0]=="--core-workload-lab")return await WorkloadLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);

if(args.Length==4 && args[0]=="--core-binding-lab")return await CoreBindingLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==4 && args[0]=="--core-two-servers-lab")return await CoreBindingLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3],true);
if(args.Length==4 && args[0]=="--core-safety-lab")return await CoreSafetyLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);
if(args.Length==4 && args[0]=="--core-network-lab")return await CoreNetworkLab.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);

if(args.Length==2 && args[0]=="--profiles-lab")return await ProfilesLab.Run(Path.GetFullPath(args[1]));
if(args.Length==2 && args[0]=="--profiles-races-lab")return await ProfilesLab.Run(Path.GetFullPath(args[1]),true);

if(args.Length==1 && args[0]=="--self-test")
{
    using var spec=JsonDocument.Parse(JsonSerializer.Serialize(new{Session=new string('a',32),Generation=7UL,Token=new string('b',64),Config="fixture"}));
    var identity=NativeAwgBackend.ValidateStart(spec.RootElement);
    foreach(var command in new[]{new{Session=identity.Session,Generation=6UL,Action="Stop"},new{Session=new string('c',32),Generation=7UL,Action="Stop"},new{Session=identity.Session,Generation=7UL,Action="RestartNetwork"}})
    {
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(command));bool refused=false;try{NativeAwgBackend.ValidateCommand(doc.RootElement,identity.Session,identity.Generation);}catch(InvalidDataException){refused=true;}if(!refused)throw new Exception("Ownership test failed.");
    }
    Console.WriteLine("PASS: backend command identity and action scope.");return 0;
}
if(args.Length!=2||args[0]!="--owner-agent-lab")return 2;
await using var backend=new NativeAwgBackend(Path.Combine(Path.GetFullPath(args[1]),"native-awg-lab.exe"));
try
{
    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(100));
    string? first=await Console.In.ReadLineAsync(budget.Token);if(first==null||first.Length>131072)return 1;
    using(var doc=JsonDocument.Parse(first)){var ready=await backend.StartAsync(doc.RootElement,budget.Token);Console.WriteLine(ready.GetRawText());}
    for(int i=0;i<64;i++)
    {
        string? line=await Console.In.ReadLineAsync(budget.Token);if(line==null)return 0;if(line.Length>2048)return 1;
        using var doc=JsonDocument.Parse(line);var reply=await backend.SendAsync(doc.RootElement,budget.Token);Console.WriteLine(reply.GetRawText());if(doc.RootElement.GetProperty("Action").GetString()=="Stop")return 0;
    }
}
catch{Console.Error.WriteLine("Native backend laboratory operation failed.");return 1;}
return 1;
