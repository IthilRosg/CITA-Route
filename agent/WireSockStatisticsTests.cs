using System.IO.Pipes;
using System.Text;
using RoutePilot.Telemetry;

namespace RoutePilot.Agent;

internal static class WireSockStatisticsTests
{
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    static byte[] Join(params byte[][] parts)=>parts.SelectMany(x=>x).ToArray();
    internal static byte[] Response(string profile="SmartRouting-AWG2",int state=3,float loss=0.125f,ulong received=100,int rtt=46)
    {
        byte[] floatField=Join(new byte[]{45},BitConverter.GetBytes(loss));
        byte[] stats=Join(StatsProto.IntegerField(1,50),StatsProto.IntegerField(2,received),floatField,
            StatsProto.IntegerField(6,unchecked((ulong)rtt)),StatsProto.BytesField(7,StatsProto.IntegerField(1,90)));
        return Join(StatsProto.IntegerField(1,(ulong)state),StatsProto.BytesField(3,Encoding.UTF8.GetBytes(profile)),StatsProto.BytesField(11,stats));
    }
    static byte[] Frame(byte[] payload,int status=0)=>Join(
        StatsProto.Delimited(StatsProto.BytesField(2,Array.Empty<byte>())),
        StatsProto.Delimited(StatsProto.BytesField(3,Join(StatsProto.IntegerField(1,(ulong)payload.Length),StatsProto.IntegerField(2,1)))),payload,
        StatsProto.Delimited(StatsProto.BytesField(5,StatsProto.IntegerField(2,(ulong)status))));
    static async Task<(WireSockSample? Sample,bool Rejected)> Fixture(byte[] response,bool fragmented=false,bool stall=false,byte[]? raw=null)
    {
        string name="CitaWireSockFixture-"+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        byte[] request=WireSockStatistics.Request();
        var serving=Task.Run(async()=>
        {
            try
            {
                await server.WaitForConnectionAsync(stop.Token);var input=new byte[request.Length];await server.ReadExactlyAsync(input,stop.Token);
                int offset=0,length=checked((int)StatsProto.Varint(input,ref offset));
                var init=StatsProto.Fields(StatsProto.Fields(input.AsSpan(offset,length).ToArray()).Single().Bytes);
                Assert(Encoding.UTF8.GetString(StatsProto.Data(init,1))==WireSockStatistics.Method,"The fixture must receive only the status RPC.");
                if(stall){await Task.Delay(Timeout.Infinite,stop.Token);return;}
                byte[] reply=raw??Frame(response);
                if(fragmented){foreach(byte b in reply){await server.WriteAsync(new[]{b},stop.Token);await Task.Delay(1,stop.Token);}}
                else await server.WriteAsync(reply,stop.Token);
            }
            catch(Exception ex)when(ex is OperationCanceledException or IOException){}
        });
        await client.ConnectAsync(stop.Token);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(stall?100:2000);
        try{return(WireSockStatistics.Parse(await WireSockStatistics.Query(client,deadline.Token),"SmartRouting-AWG2",DateTime.UtcNow),false);}
        catch(Exception ex)when(ex is OperationCanceledException or IOException or InvalidDataException or OverflowException){return(null,true);}
        finally{stop.Cancel();await serving;}
    }
    internal static async Task Run()
    {
        DateTime now=DateTime.UtcNow;
        var sample=WireSockStatistics.Parse(Response(),"SmartRouting-AWG2",now);
        Assert(sample.Available&&sample.RttMs==46&&sample.EstimatedLossPercent==12.5&&sample.UptimeSeconds==90,"WireSock native statistics must decode fraction/RTT/duration independently of UDP probes.");
        sample=WireSockStatistics.Parse(Response(loss:0),"SmartRouting-AWG2",now);
        Assert(sample.EstimatedLossPercent==0,"A supported zero estimate with actual received data is valid.");
        sample=WireSockStatistics.Parse(Response(received:0),"SmartRouting-AWG2",now);
        Assert(sample.EstimatedLossPercent==null&&sample.RttMs==46,"No inbound traffic must not fabricate a zero loss estimate; valid RTT remains independent.");
        Assert(!WireSockStatistics.Parse(Response("SmartRouting-AWG2-copy"),"SmartRouting-AWG2",now).Available,"A similarly named foreign tunnel cannot supply CITA metrics.");
        Assert(!WireSockStatistics.Parse(Response(state:2),"SmartRouting-AWG2",now).Available,"Connecting must not publish connected metrics.");
        foreach(var bytes in new[]{Response(loss:float.NaN),Response(loss:-0.2f),Response(loss:1.1f),Response(rtt:-5),new byte[]{0},new byte[]{10,255},Join(Response(),StatsProto.IntegerField(1,3))})
        {
            bool refused=false;try{WireSockStatistics.Parse(bytes,"SmartRouting-AWG2",now);}catch(InvalidDataException){refused=true;}
            Assert(refused,"Malformed, duplicate or out-of-range native statistics must fail closed.");
        }
        using var source=new MemoryStream(WireSockStatistics.Request());var requests=new List<List<StatsProto.Field>>();
        while(source.Position<source.Length)
        {int length=source.ReadByte();var bytes=new byte[length];source.ReadExactly(bytes);requests.Add(StatsProto.Fields(bytes));}
        var start=StatsProto.Fields(requests[0].Single().Bytes);
        Assert(new UTF8Encoding(false,true).GetString(StatsProto.Data(start,1))==WireSockStatistics.Method && requests.Count==4,
            "The only wire method must be RpcGetConnectionStatus, without Hello, profile/config export or mutation RPCs.");
        var roundtrip=await Fixture(Response(),fragmented:true);
        Assert(roundtrip.Sample?.EstimatedLossPercent==12.5 && !roundtrip.Rejected,"Fragmented pipe frames must preserve native statistics.");
        Assert((await Fixture(Response(),raw:Frame(Response(),7))).Rejected,"A rejected RPC must not publish a payload before trailers.");
        Assert((await Fixture(Response(),stall:true)).Rejected,"A stalled service must cancel promptly.");
        byte[] oversized=StatsProto.Delimited(StatsProto.BytesField(3,StatsProto.IntegerField(1,100000)));
        Assert((await Fixture(Response(),raw:oversized)).Rejected,"Oversized native replies must be rejected before allocation.");
        Assert((await Fixture(Response(),raw:Join(Frame(Response()).Take(10).ToArray()))).Rejected,"Truncated IPC frames must not publish partial statistics.");
        var cancelled=new CancellationTokenSource();cancelled.Cancel();bool rejected=false;
        try{await WireSockStatistics.Query(new MemoryStream(),cancelled.Token);}catch(OperationCanceledException){rejected=true;}
        Assert(rejected,"A cancelled query must not finish after a newer session begins.");cancelled.Dispose();
        Console.WriteLine("PASS: WireSock native statistics, read-only IPC, fragmented/error/oversized/truncated replies, bounded timeout, foreign/connecting/no-traffic guards and cancellation.");
    }
}
