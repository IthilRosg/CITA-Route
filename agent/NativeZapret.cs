using System.Diagnostics;
using System.Collections.Frozen;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RoutePilot.Agent;

// This is a lease for a socket already exclusively bound by the trusted core,
// before connect. A destination-wide rule is not sufficient to create a lease.
internal sealed record NativeZapretScope(string Epoch,string Application,uint PhysicalIndex,
    string LocalAddress,ushort LocalPort,string RemoteAddress,ushort RemotePort,string Network);
internal enum NativeZapretCapture { WholeConnection,NonSyn,TlsPayload }

internal sealed record NativeZapretPlan(NativeZapretScope Scope,string Filter)
{
    internal static NativeZapretPlan ForTlsTcp(NativeZapretScope scope,string epoch,uint physicalIndex,
        string localAddress,IReadOnlySet<string> excludedAddresses,NativeZapretCapture capture=NativeZapretCapture.WholeConnection)
    {
        if(!Regex.IsMatch(epoch,"^[a-f0-9]{32}$",RegexOptions.CultureInvariant)||scope.Epoch!=epoch||
            scope.PhysicalIndex==0||scope.PhysicalIndex!=physicalIndex||scope.LocalAddress!=localAddress||
            string.IsNullOrWhiteSpace(scope.Application)||scope.Network!="tcp"||scope.RemotePort!=443||scope.LocalPort<1024)
            throw new InvalidDataException("Zapret requires a current identified TLS socket lease.");
        if(!IPAddress.TryParse(scope.LocalAddress,out var local)||local.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||
            IPAddress.IsLoopback(local)||local.Equals(IPAddress.Any)||!IPAddress.TryParse(scope.RemoteAddress,out var remote)||
            remote.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||excludedAddresses.Contains(remote.ToString())||!Public(remote))
            throw new InvalidDataException("Zapret scope address is excluded.");
        string outbound=$"outbound and ip.SrcAddr == {local} and ip.DstAddr == {remote} and tcp.SrcPort == {scope.LocalPort} and tcp.DstPort == 443";
        string inbound=$"inbound and ip.DstAddr == {local} and ip.SrcAddr == {remote} and tcp.DstPort == {scope.LocalPort} and tcp.SrcPort == 443";
        string extra=capture switch{NativeZapretCapture.WholeConnection=>"",NativeZapretCapture.NonSyn=>" and !tcp.Syn",NativeZapretCapture.TlsPayload=>" and tcp.PayloadLength > 0",_=>throw new InvalidDataException("Unknown Zapret capture policy.")};
        return new(scope,$"!loopback and !impostor and ip and tcp and ifIdx == {physicalIndex} and (({outbound}) or ({inbound})){extra}");
    }
    internal static bool Public(IPAddress address)
    {
        var b=address.GetAddressBytes();
        return b[0]!=0&&b[0]!=10&&b[0]!=26&&b[0]!=127&&b[0]<224&&
            !(b[0]==100&&b[1]>=64&&b[1]<=127)&&!(b[0]==169&&b[1]==254)&&
            !(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&(b[1]==168||b[1]==0))&&
            !(b[0]==198&&(b[1]==18||b[1]==19||b[1]==51&&b[2]==100))&&
            !(b[0]==203&&b[1]==0&&b[2]==113);
    }
    internal string[] Arguments(string root)=>[
        "--wf-raw="+Filter,
        "--lua-init=@"+Path.Combine(root,"lua","zapret-lib.lua"),
        "--lua-init=@"+Path.Combine(root,"lua","zapret-antidpi.lua"),
        "--filter-tcp=443","--filter-l7=tls","--payload=tls_client_hello",
        "--out-range=-d10","--lua-desync=multidisorder:pos=1,midsld"];
}
// Expected hashes must come from the installed, protected component manifest,
// not a manifest supplied by the connecting user. This checks bytes, not driver
// trust: signature/license/ACL acceptance is a separate installation gate.
internal sealed class NativeZapretBundle(string root,IReadOnlyDictionary<string,string> expected)
{
    readonly FrozenDictionary<string,string> identities=expected.ToFrozenDictionary(StringComparer.Ordinal);
    internal static readonly string[] RequiredFiles=["winws2.exe","cygwin1.dll","WinDivert.dll","WinDivert64.sys","lua/zapret-lib.lua","lua/zapret-antidpi.lua"];
    internal string Root=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    internal IReadOnlyDictionary<string,string> PinnedHashes=>identities;
    internal void Verify()
    {
        if(identities.Count!=RequiredFiles.Length||RequiredFiles.Any(name=>!identities.TryGetValue(name,out var hash)||!Regex.IsMatch(hash,"^[A-Fa-f0-9]{64}$")))
            throw new InvalidDataException("Incomplete Zapret component identity.");
        foreach(string name in RequiredFiles)
        {
            string path=Path.Combine(Root,name.Replace('/',Path.DirectorySeparatorChar));
            for(string? part=path;part!=null;part=Path.GetDirectoryName(part))
                if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Zapret reparse paths are refused.");
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length is <1 or >8388608||!Convert.ToHexString(SHA256.HashData(file)).Equals(identities[name],StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Zapret component identity changed.");
        }
    }
}
