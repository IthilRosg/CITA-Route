using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RoutePilot.Quality;

public sealed class UdpQualityOptions
{
    public bool Enabled{get;set;}
    public string Address{get;set;}="";
    public int Port{get;set;}=38565;
    public string Key{get;set;}="";
    [JsonIgnore] public bool ValidKey
    {
        get{if(Key==null || Key.Length!=64)return false;foreach(char c in Key)if(!Uri.IsHexDigit(c))return false;return true;}
    }
    [JsonIgnore] public string Identity=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Address+":"+Port+":"+(Key??"").ToUpperInvariant())));
    public static bool PublicIpv4(IPAddress ip)
    {
        if(ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return false;
        byte[] b=ip.GetAddressBytes();
        return b[0] is not (0 or 10 or 127) && b[0]<224 && !(b[0]==169 && b[1]==254) && !(b[0]==172 && b[1] is >=16 and <=31) && !(b[0]==192 && b[1]==168) && !(b[0]==100 && b[1] is >=64 and <=127) && !(b[0]==198 && b[1] is 18 or 19) && !(b[0]==192 && b[1]==0 && b[2] is 0 or 2) && !(b[0]==198 && b[1]==51 && b[2]==100) && !(b[0]==203 && b[1]==0 && b[2]==113);
    }
}
public sealed class UdpPathQuality
{
    public string Status{get;set;}="Unmeasured";
    public string Reason{get;set;}="NotConfigured";
    public int Sent{get;set;}
    public int Received{get;set;}
    public int Duplicates{get;set;}
    public int Reordered{get;set;}
    public int Late{get;set;}
    public double? LossPercent{get;set;}
    public DateTime WindowStartedUtc{get;set;}
    public DateTime UpdatedUtc{get;set;}
    public int DeadlineMs{get;set;}=1500;
}
public sealed class UdpQualityState
{
    public int Schema{get;set;}=1;
    public string SessionId{get;set;}="";
    public string NetworkSignature{get;set;}="";
    public string SourceProfile{get;set;}="";
    public int BridgeGeneration{get;set;}
    public string ConfigurationId{get;set;}="";
    public string Transport{get;set;}="SOCKS5 UDP / IPv4";
    public DateTime UpdatedUtc{get;set;}
    public UdpPathQuality Direct{get;set;}=new();
    public UdpPathQuality Vpn{get;set;}=new();
}
