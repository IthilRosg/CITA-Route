using System.Net.NetworkInformation;

namespace RoutePilot.Agent;

internal sealed class PolicyNetworkMonitor:IDisposable
{
    readonly Func<string> _read;
    readonly Func<long> _clock;
    int _revision;
    int _sampled=-1;
    long _sampledAt;
    string _value="";
    internal PolicyNetworkMonitor(Func<string> read,Func<long>? clock=null,bool subscribe=true)
    {
        _read=read;_clock=clock??(()=>Environment.TickCount64);
        if(subscribe){NetworkChange.NetworkAddressChanged+=Changed;NetworkChange.NetworkAvailabilityChanged+=AvailabilityChanged;}
        _subscribed=subscribe;
    }
    readonly bool _subscribed;
    void Changed(object? sender,EventArgs e)=>Invalidate();
    void AvailabilityChanged(object? sender,NetworkAvailabilityEventArgs e)=>Invalidate();
    internal void Invalidate()=>Interlocked.Increment(ref _revision);
    internal string Read(bool fresh=false)
    {
        int revision=Volatile.Read(ref _revision);long now=_clock();
        if(fresh || revision!=_sampled || now<_sampledAt || now-_sampledAt>=2000)
        {_value=_read();_sampledAt=now;_sampled=revision;}
        return _value;
    }
    public void Dispose()
    {if(_subscribed){NetworkChange.NetworkAddressChanged-=Changed;NetworkChange.NetworkAvailabilityChanged-=AvailabilityChanged;}}
}
