namespace RoutePilot.Agent;

internal static class NativeGuardedSocketTests
{
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    internal static async Task Run()
    {
        var socket=new NativeBoundSocketIdentity(Guid.NewGuid(),Guid.NewGuid().ToString("N"),"192.168.1.124",52001,"1.1.1.1",443,25);
        var calls=new List<string>();var guard=new Permissions(calls);var inner=new Interception(calls);
        var combined=new NativeGuardedSocketInterception(guard,inner,@"C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe");
        await combined.WaitReadyAsync(socket,default);await combined.RetireAsync(default);
        Assert(string.Join(',',calls)=="grant,verify,ready,retire,revoke,verify"&&combined.RetirementConfirmed&&!guard.Applied,
            "Socket readiness/retirement must include both WFP and interception in the correct order.");

        calls.Clear();guard=new(calls){LostGrantAck=true};inner=new(calls);
        combined=new(guard,inner,"agent");bool failed=false;
        try{await combined.WaitReadyAsync(socket,default);}catch(IOException){failed=true;}
        Assert(failed&&guard.Applied&&!calls.Contains("ready"),"A lost WFP grant ACK must not start interception or lose cleanup ownership.");
        await combined.RetireAsync(default);Assert(!guard.Applied&&combined.RetirementConfirmed,"A partially acknowledged grant must still be revoked.");

        calls.Clear();guard=new(calls);inner=new(calls){ConfirmRetirement=false};combined=new(guard,inner,"agent");
        await combined.WaitReadyAsync(socket,default);failed=false;
        try{await combined.RetireAsync(default);}catch(IOException){failed=true;}
        Assert(failed&&!combined.RetirementConfirmed&&guard.Applied&&!calls.Contains("revoke"),"WFP permission must remain owned while interceptor retirement is unconfirmed.");
        inner.ConfirmRetirement=true;guard.FailRevoke=true;failed=false;
        try{await combined.RetireAsync(default);}catch(IOException){failed=true;}
        Assert(failed&&!combined.RetirementConfirmed&&guard.Applied,"A revoke failure cannot permit socket release.");
        guard.FailRevoke=false;await combined.RetireAsync(default);
        Assert(combined.RetirementConfirmed&&!guard.Applied,"Retry must finish the same guarded socket owner.");
        Console.WriteLine("PASS: exact WFP socket admission/retirement order, lost grant ACK ownership, unconfirmed worker/revoke quarantine and retry.");
    }
    sealed class Permissions(List<string> calls):INativeConnectorPermissions
    {
        internal bool Applied,LostGrantAck,FailRevoke;
        public Task SetAsync(NativeConnectorPermission permission,bool grant,CancellationToken stop)
        {
            calls.Add(grant?"grant":"revoke");
            if(!grant&&FailRevoke)throw new IOException("controlled revoke refusal");
            Applied=grant;if(grant&&LostGrantAck)throw new IOException("controlled lost grant ACK");return Task.CompletedTask;
        }
        public Task<bool> VerifyAsync(CancellationToken stop){calls.Add("verify");return Task.FromResult(true);}
    }
    sealed class Interception(List<string> calls):INativeSocketInterception
    {
        internal bool ConfirmRetirement=true;
        public bool RetirementConfirmed{get;private set;}
        public Task WaitReadyAsync(NativeBoundSocketIdentity identity,CancellationToken stop){calls.Add("ready");return Task.CompletedTask;}
        public Task RetireAsync(CancellationToken stop){calls.Add("retire");RetirementConfirmed=ConfirmRetirement;return Task.CompletedTask;}
    }
}
