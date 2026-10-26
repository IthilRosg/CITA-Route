using System.Collections.Generic;
using System.ComponentModel;

namespace RoutePilot;

public sealed class ConnectionRow:INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    string id="",host="",process="—",network="",rule="",address="",started="";
    bool isVpn,isDpi;long upload,download;
    void Set<T>(ref T field,T value,string name,params string[] dependent)
    {
        if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;
        PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
        foreach(string property in dependent)PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(property));
    }
    public string Id{get=>id;set=>Set(ref id,value,nameof(Id));}
    public string Host{get=>host;set=>Set(ref host,value,nameof(Host));}
    public string Process{get=>process;set=>Set(ref process,value,nameof(Process));}
    public string Network{get=>network;set=>Set(ref network,value,nameof(Network));}
    public bool IsVpn{get=>isVpn;set=>Set(ref isVpn,value,nameof(IsVpn),nameof(Route),nameof(RouteColor));}
    public bool IsDpi{get=>isDpi;set=>Set(ref isDpi,value,nameof(IsDpi),nameof(Route),nameof(RouteColor));}
    public string Route=>IsVpn?"VPN":IsDpi?"DPI":"Напрямую";
    public string RouteColor=>IsVpn?"#E8443F":IsDpi?"#D7AD67":"#86A9C8";
    public string Rule{get=>rule;set=>Set(ref rule,value,nameof(Rule));}
    public long Upload{get=>upload;set=>Set(ref upload,value,nameof(Upload),nameof(Uploaded));}
    public long Download{get=>download;set=>Set(ref download,value,nameof(Download),nameof(Downloaded));}
    public string Uploaded=>Format.Bytes(Upload);
    public string Downloaded=>Format.Bytes(Download);
    public string Address{get=>address;set=>Set(ref address,value,nameof(Address));}
    public string Started{get=>started;set=>Set(ref started,value,nameof(Started));}
}
