using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace RoutePilot.Agent;

internal static class PolicyPublicationTests
{
    static void Assert(bool value,string message){if(!value)throw new IOException(message);}
    static int FreePort(){var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();int port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();return port;}
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"RoutePilot-publication-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"runtime","rules"));
        var ports=new PolicyPorts{Ack=FreePort(),Proof=FreePort()};
        string file=Path.Combine(root,"runtime","rules","auto-direct.json");
        var desired=PolicyPublisher.Routes.ToDictionary(r=>r,_=>Array.Empty<JsonObject>());
        string Marker()=>Storage.Read<JsonObject>(file)["rules"]!.AsArray().Last()!["domain"]![0]!.GetValue<string>();
        try
        {
            await using var publisher=new PolicyPublisher(root,ports,()=>true);
            async Task Unconfirmed(int milliseconds)
            {
                using var stop=new CancellationTokenSource(milliseconds);bool cancelled=false;
                try{await publisher.Apply(desired,stop.Token);}catch(OperationCanceledException){cancelled=true;}
                Assert(cancelled&&publisher.Version==0,"A written generation without engine proof must never become applied.");
            }
            await Unconfirmed(5000); // Exercise the real four-second canary deadline.
            string first=Marker(),body=File.ReadAllText(file);DateTime modified=File.GetLastWriteTimeUtc(file);
            await Unconfirmed(120);
            Assert(Marker()==first&&File.ReadAllText(file)==body&&File.GetLastWriteTimeUtc(file)==modified,"An unchanged retry must preserve the exact pending generation without rewriting its file.");
            desired["direct"]=new[]{PolicyPublisher.Rule(new PolicyKey("epoch","example.com",Environment.ProcessPath!,443))};
            await Unconfirmed(120);string changed=Marker();
            Assert(changed!=first,"Changed routing rules must require a fresh marker; an older proof cannot certify them.");
            Storage.Write(file,new{version=3,rules=Array.Empty<JsonObject>()});
            await Unconfirmed(120);
            Assert(Marker()!=changed,"An externally replaced pending document must require a fresh generation.");
            string replaced=Marker();File.Delete(file);
            await Unconfirmed(120);
            Assert(Marker()!=replaced,"A deleted pending document must be republished with a fresh generation.");
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("PASS: unchanged publication retries preserve pending proof; changed rules/files require a new generation and missing proof never becomes applied.");
    }
}
