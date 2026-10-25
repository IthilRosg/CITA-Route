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
    readonly bool persistentControl;
    readonly Action<Process>? started;
    internal NativeAwgBackend(string image,bool persistentControl=false,Action<Process>? started=null){this.image=Path.GetFullPath(image);this.persistentControl=persistentControl;this.started=started;}
    internal int ProcessId{get{try{return process?.Id??0;}catch(InvalidOperationException){return 0;}}}
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
    internal static async Task<JsonElement> ReadReply(StreamReader reader,CancellationToken stop,int maxChars=8192)
    {
        var text=new StringBuilder();var one=new char[1];
        while(text.Length<maxChars)
        {
            int read=await reader.ReadAsync(one.AsMemory(),stop);
            if(read==0)throw new IOException("Native backend closed.");
            if(one[0]=='\n'){using var doc=JsonDocument.Parse(text.ToString());return doc.RootElement.Clone();}
            text.Append(one[0]);
        }
        throw new InvalidDataException("Oversized native backend reply.");
    }
    internal static async Task PreflightAsync(string image,JsonElement spec,CancellationToken stop)
    {
        var identity=ValidateStart(spec);
        var info=new ProcessStartInfo(Path.GetFullPath(image)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--validate-profile-lab");
        using var p=Process.Start(info)??throw new IOException("Profile validation did not start.");
        _=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await p.StandardInput.WriteLineAsync(spec.GetRawText().AsMemory(),deadline.Token);p.StandardInput.Close();
            var result=await ReadReply(p.StandardOutput,deadline.Token);await p.WaitForExitAsync(deadline.Token);
            if(p.ExitCode!=0||result.GetProperty("stage").GetString()!="ConfigurationValidated"||result.GetProperty("session").GetString()!=identity.Session||result.GetProperty("generation").GetUInt64()!=identity.Generation)throw new InvalidDataException("Profile validation failed.");
        }
        finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
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
            if(persistentControl)info.Environment["CITA_NATIVE_PERSISTENT_CONTROL"]="1";
            process=Process.Start(info)??throw new IOException("Backend did not start.");
            started?.Invoke(process);
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
