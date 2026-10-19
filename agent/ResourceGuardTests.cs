using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RoutePilot.Resources;

namespace RoutePilot.Agent;

public static class ResourceGuardTests
{
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static ResourceSample Sample(DateTime now,int used=100)=>new()
    {
        ObservedUtc=now,ProcessesObservedUtc=now,MemoryAvailable=true,PhysicalTotal=16L<<30,PhysicalAvailable=8L<<30,CommitLimit=24L<<30,CommitTotal=8L<<30,
        Pools=new(){new(){Family="IPv4",Start=49152,Count=16384,Used=used,TableAvailable=true,RangeAvailable=true,ReservationsAvailable=true,Allocation="Available"},
                    new(){Family="IPv6",Start=49152,Count=16384,Used=10,TableAvailable=true,RangeAvailable=true,ReservationsAvailable=true,Allocation="Available"}}
    };
    public static void Run()
    {
        Assert(WindowsResourceSampler.ParseRange("Protocol udp\r\nStart Port : 49152\r\nNumber of Ports : 16384\r\n") is {Start:49152,Count:16384},"Actual dynamic range.");
        Assert(WindowsResourceSampler.ParseRange("Начало : 10000\r\nКоличество : 1000\r\n") is {Start:10000,Count:1000},"Localized/custom range.");
        Assert(WindowsResourceSampler.ParseRange("Start : 65000\nCount : 1000\n")==null&&WindowsResourceSampler.ParseRange("unavailable")==null,"Invalid ranges stay unknown.");
        var reserved=WindowsResourceSampler.ParseReservations("Start Port End Port\n 10000 10020 *\n 10010 10030\n");
        Assert(reserved.Count(x=>x)==31,"Overlapping exclusions cannot count twice.");
        var rows=new WindowsResourceSampler.UdpRow[]{new(2,10000,1),new(2,10031,1),new(2,10031,2),new(2,10032,1),new(2,9999,1)};
        var pool=WindowsResourceSampler.Summarize(2,rows,new(10000,100),true,reserved);
        Assert(pool.Capacity==69&&pool.Used==2&&pool.Endpoints==5,"Unique non-reserved ports differ from endpoints and shared binds.");
        Assert(WindowsResourceSampler.Summarize(2,rows,null,false,new bool[65536]).UsedPercent==null,"No assumed denominator.");
        var identities=new ResourceProcessTracker();var created=DateTime.UtcNow.AddMinutes(-1);
        identities.Begin(new[]{42});identities.Observe(42,new(){Pid=42,StartedUtc=created,CitaComponent=true});
        for(int i=0;i<4;i++){identities.Begin(new[]{42});Assert(!identities.Observe(42,null),"Repeated access failures must preserve knowledge of the unreadable component.");}
        identities.Observe(42,new(){Pid=42,StartedUtc=created.AddSeconds(1)});Assert(identities.Observe(42,null),"A reused PID must drop the previous component identity.");
        identities.Observe(42,new(){Pid=42,StartedUtc=created,CitaComponent=true});identities.Begin(Array.Empty<int>());Assert(identities.Observe(42,null),"A confirmed exit clears identity.");
        foreach(int family in new[]{2,23})
        {
            int width=family==2?12:28,offset=family==2?4:20;
            var bytes=new byte[4+width];BinaryPrimitives.WriteUInt32LittleEndian(bytes,1);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4+offset),54321);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4+offset+4),1234);
            var parsed=WindowsResourceSampler.ParseUdpTable(bytes,family);
            Assert(parsed.Count==1&&parsed[0].Port==54321&&parsed[0].Pid==1234,"Native family layout/endian/owner.");
            bool refused=false;try{WindowsResourceSampler.ParseUdpTable(bytes[..^1],family);}catch(InvalidDataException){refused=true;}Assert(refused,"Truncated table rejected.");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes,uint.MaxValue);refused=false;try{WindowsResourceSampler.ParseUdpTable(bytes,family);}catch(InvalidDataException){refused=true;}Assert(refused,"Unbounded table rejected.");
        }
        var now=DateTime.UtcNow;var engine=new ResourceMonitorEngine();
        Assert(engine.Update(Sample(now)).Level==0,"Normal state is quiet.");
        Assert(engine.Update(Sample(now.AddSeconds(1),16339)).Level==0,"Single MSI-sized spike requires confirmation.");
        Assert(engine.Update(Sample(now.AddSeconds(2),16339)).Level==2,"Repeated MSI-sized pressure is critical without consuming real ports.");
        var unknown=Sample(now.AddSeconds(3));unknown.Pools[0].TableAvailable=false;
        Assert(engine.Update(unknown).Level==2,"Missing data cannot prove recovery.");
        Assert(engine.Update(Sample(now.AddSeconds(4))).Level==2&&engine.Update(Sample(now.AddSeconds(5))).Level==2,"Brief recovery cannot clear an incident.");
        var recovered=engine.Update(Sample(now.AddSeconds(6)));
        Assert(recovered.Level==0&&recovered.Incidents.Last().Transition=="Recovered","Three reliable clear observations prove recovery.");
        Assert(ResourceStateCache.Valid(recovered,now.AddSeconds(6)),"Current snapshot readable by GUI.");
        Assert(!ResourceStateCache.Valid(recovered,now.AddMinutes(2))&&!ResourceStateCache.Valid(recovered,now.AddMinutes(-2)),"Stale/future snapshots rejected.");
        var broken=Sample(now.AddSeconds(7));broken.Pools[0].RangeAvailable=false;broken.Pools[0].TableAvailable=false;
        Assert(!engine.Update(broken).Complete,"Partial coverage cannot look fully healthy.");
        var failure=Sample(now.AddSeconds(8));failure.Pools[0].Allocation="ResourceFailure";engine.Update(failure);
        failure=Sample(now.AddSeconds(9));failure.Pools[0].Allocation="ResourceFailure";
        Assert(engine.Update(failure).Issues.Any(x=>x.Code=="UdpAllocationFailure"),"Resource bind errors confirm independently of percentage.");
        bool replay=false;try{engine.Update(failure);}catch(InvalidDataException){replay=true;}Assert(replay,"A replay cannot confirm a second observation.");
        var memory=new ResourceMonitorEngine();
        for(int i=0;i<2;i++){var x=Sample(now.AddSeconds(i*10));x.CommitTotal=x.CommitLimit-(128L<<20);recovered=memory.Update(x);}
        Assert(recovered.Issues.Any(x=>x.Code=="CommitPressure"),"System commit shortage classified separately.");
        var growth=new ResourceMonitorEngine();ResourceState? state=null;
        for(int i=0;i<23;i++)
        {
            var x=Sample(now.AddSeconds(i*30));x.Processes.Add(new(){Pid=100,Name="RoutePilot",StartedUtc=now.AddMinutes(-1),CitaComponent=true,PrivateBytes=(100L+i*30)<<20,Handles=100});state=growth.Update(x);
        }
        Assert(state!.Issues.Any(x=>x.Code=="CitaResourceGrowth"),"Sustained owned-component growth is reported as suspicion.");
        for(int i=23;i<26;i++){var unreadable=Sample(now.AddSeconds(i*30));unreadable.Unavailable.Add("CitaProcessCoverage");state=growth.Update(unreadable);}
        Assert(state.Issues.Any(x=>x.Code=="CitaResourceGrowth")&&!state.Complete,"Unreadable component metrics cannot prove growth recovery.");
        var reused=Sample(now.AddSeconds(26*30));reused.Processes.Add(new(){Pid=100,Name="RoutePilot",StartedUtc=now.AddSeconds(26*30),CitaComponent=true,PrivateBytes=50L<<20,Handles=100});
        state=growth.Update(reused);Assert(!state.Issues.Any(x=>x.ObservedUtc==reused.ObservedUtc&&x.Code=="CitaResourceGrowth"),"Reused PID never inherits previous trend.");
        for(int i=0;i<120;i++)state=engine.Update(Sample(now.AddMinutes(20).AddSeconds(i*10),i%6<3?16339:100));
        Assert(state!.Incidents.Count<=32,"Incident history bounded under repeated pressure/recovery.");
        var restarted=new ResourceMonitorEngine(state.Incidents).Update(Sample(now.AddDays(1)));
        Assert(restarted.Issues.Count==0&&restarted.Incidents.Count==state.Incidents.Count,"History survives restart without inheriting active/pending incidents.");
        Assert(new ResourceMonitorEngine().Update(Sample(now)).Issues.Count==0,"New monitor cannot inherit pending confirmation.");
        Console.WriteLine("PASS: UDP layouts/ranges/exclusions/shared binds, synthetic MSI pressure, confirmation/recovery, unknown/stale states, allocation errors, memory pressure, owned-process growth, PID reuse and bounded history.");
    }
    public static async Task Integration(string root)
    {
        string folder=Path.Combine(root,"runtime","resource-integration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var sockets=new List<Socket>();var sampler=new WindowsResourceSampler(root);Task? monitor=null;
        using var stop=new CancellationTokenSource();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            var before=await sampler.Capture(budget.Token);
            Assert(before.Pools.First(p=>p.Family=="IPv4").TableAvailable,"Live UDP table unavailable.");
            Assert(before.Processes.Any(p=>p.Pid==Environment.ProcessId&&p.CitaComponent),"Exact native image identity did not recognize the current CITA agent.");
            for(int i=0;i<16;i++){var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);sockets.Add(socket);socket.Bind(new IPEndPoint(IPAddress.Loopback,0));}
            var ports=sockets.Select(s=>((IPEndPoint)s.LocalEndPoint!).Port).ToHashSet();var table=WindowsResourceSampler.ReadUdpTable(2);
            Assert(ports.All(p=>table.Any(r=>r.Port==p&&r.Pid==Environment.ProcessId)),"Controlled sockets not attributed to own PID.");
            var warm=new List<long>();for(int i=0;i<4;i++)warm.Add((await sampler.Capture(budget.Token)).DurationMs);
            foreach(var socket in sockets)socket.Dispose();sockets.Clear();table=WindowsResourceSampler.ReadUdpTable(2);
            Assert(!table.Any(r=>ports.Contains(r.Port)&&r.Pid==Environment.ProcessId),"Controlled sockets remained after disposal.");
            monitor=ResourceMonitor.Run(folder,root,stop.Token);string path=Path.Combine(folder,"resource-state.json");
            while(!File.Exists(path))await Task.Delay(50,budget.Token);
            Assert(ResourceStateCache.Read(path,DateTime.UtcNow)!=null,"Background monitor did not publish a current bounded report.");
            stop.Cancel();await monitor.WaitAsync(TimeSpan.FromSeconds(5));
            Storage.Write(Path.Combine(folder,"timing.json"),new{coldMs=before.DurationMs,warmMs=warm,notes="Read-only counters; 16 local bind-only sockets; no VPN/services/route changes. Not a leak test."});
            Console.WriteLine($"PASS: live counters, actual allocation, 16 socket owners/disposal, background report/cancellation. Cold={before.DurationMs}ms; warm median={warm.Order().ElementAt(2)}ms. No traffic sent.");
        }
        finally{foreach(var socket in sockets)socket.Dispose();stop.Cancel();if(monitor!=null)try{await monitor.WaitAsync(TimeSpan.FromSeconds(5));}catch{}}
    }
}
