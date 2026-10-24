using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RoutePilot.Agent;

// Candidate backend only: no service registration, route mutation or automatic
// production selection. Its caller must supply a trusted component image.
internal sealed class NativeAwgBackend : IAsyncDisposable
{
    readonly SemaphoreSlim gate=new(1);
    readonly string image;
    Process? process;
    string session="";
    ulong generation;
    bool stopped;
    internal NativeAwgBackend(string image)=>this.image=Path.GetFullPath(image);
    static bool Hex(string? text,int length)=>text?.Length==length && text.All(Uri.IsHexDigit);
    internal static (string Session,ulong Generation) ValidateStart(JsonElement spec)
    {
        string? session=spec.GetProperty("Session").GetString(),token=spec.GetProperty("Token").GetString(),config=spec.GetProperty("Config").GetString();
        ulong generation=spec.GetProperty("Generation").GetUInt64();
        if(!Hex(session,32)||!Hex(token,64)||generation==0||string.IsNullOrWhiteSpace(config)||config.Length>65536)throw new InvalidDataException("Invalid native backend specification.");
        return(session!,generation);
    }
    internal static void ValidateCommand(JsonElement command,string session,ulong generation)
    {
        if(command.GetProperty("Session").GetString()!=session||command.GetProperty("Generation").GetUInt64()!=generation||command.GetProperty("Action").GetString() is not ("Snapshot" or "CrashChild" or "BlackholeChild" or "Stop"))throw new InvalidDataException("Native backend ownership mismatch.");
    }
    static async Task<JsonElement> ReadReply(StreamReader reader,CancellationToken stop)
    {
        var text=new StringBuilder();var one=new char[1];
        while(text.Length<8192)
        {
            int read=await reader.ReadAsync(one.AsMemory(),stop);
            if(read==0)throw new IOException("Native backend closed.");
            if(one[0]=='\n'){using var doc=JsonDocument.Parse(text.ToString());return doc.RootElement.Clone();}
            text.Append(one[0]);
        }
        throw new InvalidDataException("Oversized native backend reply.");
    }
    internal async Task<JsonElement> StartAsync(JsonElement spec,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            if(process!=null)throw new InvalidOperationException("Backend already started.");
            (session,generation)=ValidateStart(spec);
            var info=new ProcessStartInfo(image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add("--coordinated-owner-lab");
            process=Process.Start(info)??throw new IOException("Backend did not start.");
            _=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await process.StandardInput.WriteLineAsync(spec.GetRawText().AsMemory(),deadline.Token);await process.StandardInput.FlushAsync(deadline.Token);
            var reply=await ReadReply(process.StandardOutput,deadline.Token);
            if(reply.GetProperty("stage").GetString()!="ListeningUnverified"||reply.GetProperty("session").GetString()!=session||reply.GetProperty("generation").GetUInt64()!=generation||reply.GetProperty("port").GetInt32() is <1 or >65535)throw new IOException("Backend readiness mismatch.");
            return reply;
        }
        finally{gate.Release();}
    }
    internal async Task<JsonElement> SendAsync(JsonElement command,CancellationToken stop)
    {
        await gate.WaitAsync(stop);
        try
        {
            ValidateCommand(command,session,generation);
            if(process==null||stopped||process.HasExited)throw new IOException("Backend is not active.");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await process.StandardInput.WriteLineAsync(command.GetRawText().AsMemory(),deadline.Token);await process.StandardInput.FlushAsync(deadline.Token);
            var reply=await ReadReply(process.StandardOutput,deadline.Token);
            string? action=command.GetProperty("Action").GetString();
            if(reply.GetProperty("stage").GetString()!=action||reply.GetProperty("session").GetString()!=session||reply.GetProperty("generation").GetUInt64()!=generation)throw new IOException("Backend reply mismatch.");
            if(action=="Stop")
            {
                if(reply.GetProperty("state").GetString()!="Stopped"||reply.GetProperty("childPid").GetInt32()!=0)throw new IOException("Backend shutdown incomplete.");
                stopped=true;
            }
            return reply;
        }
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if(process==null)return;
            try{process.StandardInput.Close();}catch(IOException){}
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try{await process.WaitForExitAsync(deadline.Token);}catch(OperationCanceledException){if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
            process.Dispose();process=null;stopped=true;
        }
        finally{gate.Release();}
    }
}
