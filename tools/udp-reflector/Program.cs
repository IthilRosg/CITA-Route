using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RoutePilot.Quality;

// Separate unprivileged optional server. It is never started by the Windows client.
if(args.Length is <1 or >3)
{Console.Error.WriteLine("Usage: Cita.UdpReflector <key-file> [bind-IPv4=127.0.0.1] [port=38565]");return 2;}
try
{
    string keyPath=Path.GetFullPath(args[0]);
    if(new FileInfo(keyPath).Length>256)throw new InvalidDataException("Invalid key file.");
    byte[] key=Convert.FromHexString(File.ReadAllText(keyPath).Trim());
    if(key.Length!=32)throw new InvalidDataException("The key must contain 32 random bytes encoded as 64 hex characters.");
    var bind=args.Length>=2?IPAddress.Parse(args[1]):IPAddress.Loopback;
    int port=args.Length>=3?int.Parse(args[2]):38565;
    if(bind.AddressFamily!=AddressFamily.InterNetwork || port is <1024 or >65535)throw new ArgumentException("Use IPv4 and a port from 1024 to 65535.");
    using var socket=new UdpClient(new IPEndPoint(bind,port));using var stop=new CancellationTokenSource();
    Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
    Console.WriteLine($"CITA UDP reflector listening on {bind}:{port}; authenticated 64-byte echo, maximum 40 replies/s.");
    var clock=Stopwatch.StartNew();long bucket=0;int examined=0,replies=0;
    try
    {
        while(true)
        {
            var incoming=await socket.ReceiveAsync(stop.Token);
            long now=clock.ElapsedMilliseconds/1000;
            if(now!=bucket){bucket=now;examined=0;replies=0;}
            // Bound HMAC work, state, and replies even under invalid/replayed input.
            if(++examined>200 || replies>=40 || !UdpProbeProtocol.Valid(incoming.Buffer,key))continue;
            await socket.SendAsync(incoming.Buffer,incoming.RemoteEndPoint,stop.Token);replies++;
        }
    }
    catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    return 0;
}
catch(Exception ex){Console.Error.WriteLine("Reflector stopped: "+ex.GetType().Name);return 1;}
