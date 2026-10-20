namespace RoutePilot.Agent;

internal static class ComponentLivenessTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    internal static void Run()
    {
        var now=DateTime.UtcNow;
        var owner=new PolicyOwner{Pid=123,StartedUtc=now.AddSeconds(-60),SessionId="session"};
        PolicyPulse Pulse(string phase="Running")=>new(){InstanceId=Guid.NewGuid().ToString("N"),SessionId="session",Pid=123,StartedUtc=owner.StartedUtc,
            Phase=phase,ProgressUtc=now,DeadlineUtc=now.AddSeconds(phase=="Publishing"?45:15),Sequence=1};
        var pulse=Pulse();
        Assert(ComponentLiveness.PulseState(pulse,owner,"session",now)=="Healthy","Current worker progress must be healthy.");
        Assert(ComponentLiveness.PulseState(pulse,owner,"session",now.AddSeconds(16))=="Stale","A live PID cannot conceal a hung loop.");
        Assert(ComponentLiveness.PulseState(Pulse("Publishing"),owner,"session",now.AddSeconds(30))=="Starting","Bounded rule publication needs a larger legitimate budget.");
        pulse.DeadlineUtc=now.AddDays(1);Assert(ComponentLiveness.PulseState(pulse,owner,"session",now)=="Unknown","Arbitrary deadlines cannot disguise a hang.");
        pulse=Pulse();pulse.Pid++;Assert(ComponentLiveness.PulseState(pulse,owner,"session",now)=="Unknown","Another PID's progress must not confirm identity.");
        pulse=Pulse();pulse.StartedUtc=pulse.StartedUtc.AddSeconds(1);Assert(ComponentLiveness.PulseState(pulse,owner,"session",now)=="Unknown","PID reuse invalidates progress.");
        Assert(ComponentLiveness.PulseState(Pulse(),owner,"next",now)=="Unknown","Another session's progress is not current.");
        Assert(ComponentLiveness.PulseState(Pulse(),owner,"session",now.AddSeconds(-3))=="Unknown","Clock rollback cannot prove progress.");
        Assert(ComponentLiveness.PulseState(null,owner,"session",now)=="Stale","A missing pulse only gets a bounded process-start grace.");
        Assert(ComponentLiveness.PulseState(null,new(){StartedUtc=now},"session",now.AddSeconds(5))=="Starting","A new worker needs time to publish its first progress.");
        string root=Path.Combine(Path.GetTempPath(),"cita-progress-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"runtime"));
        try
        {
            Storage.Write(Path.Combine(root,"runtime","session.json"),new PolicySession{Id="session",Running=true});
            var progress=new PolicyProgress(root,"session");progress.Mark("Starting",true);
            string path=Path.Combine(root,"runtime","policy-heartbeat.json");var first=ComponentLiveness.Read<PolicyPulse>(path);
            Assert(first.SessionId=="session"&&first.Pid==Environment.ProcessId&&first.Sequence==1,"Progress records include authoritative process/session identity.");
            Storage.Write(Path.Combine(root,"runtime","session.json"),new PolicySession{Id="new",Running=true});progress.Mark("Stopped",true);
            Assert(ComponentLiveness.Read<PolicyPulse>(path).Sequence==1,"An old worker cannot overwrite a replacement session's progress.");
            File.WriteAllText(path,new string('x',16385));bool bounded=false;
            try{ComponentLiveness.Read<PolicyPulse>(path);}catch(InvalidDataException){bounded=true;}
            Assert(bounded,"Progress reads must be bounded.");
        }
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(),"cita-progress-")),StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root,true);
        }
        Assert(CheckedProcess.Open(new(Environment.ProcessId,now,Environment.ProcessPath!))==null,"A component helper cannot terminate itself.");
        Console.WriteLine("PASS: real progress deadlines, bounded startup/publication, stopped loop detection, identity/session/PID reuse, clock rollback and bounded progress files.");
    }
}
