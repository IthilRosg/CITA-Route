using System.Text.Json;

namespace RoutePilot.Agent;

internal sealed class NativeForcedIntentCommitUnknownException(ulong attemptedRevision,Exception cause):IOException("Forced intent commit requires reconciliation.",cause)
{
    internal ulong AttemptedRevision=>attemptedRevision;
}
internal sealed class NativeForcedIntentConflictException():InvalidOperationException("Forced intent revision changed or disappeared.");
internal sealed class NativeForcedIntentStore
{
    readonly string root,path,lockPath,floorPath;
    readonly Action<FileSystemInfo,bool> verify;
    NativeForcedIntentStore(string root,Action<FileSystemInfo,bool> verify)
    {
        this.root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path=Path.Combine(this.root,"forced-intent.json");floorPath=Path.Combine(this.root,"forced-intent-floor.json");lockPath=Path.Combine(this.root,"forced-intent.lock");this.verify=verify;
    }
    internal static NativeForcedIntentStore Protected(string root)=>new(root,NativeTrustedBundle.CheckAcl);
    internal static NativeForcedIntentStore ForTests(string root,Action<FileSystemInfo,bool>? verify=null)=>new(root,verify??((_,_)=>{}));
    void CheckRoot()
    {
        var directory=new DirectoryInfo(root);verify(directory,false);
        for(var parent=directory.Parent;parent!=null;parent=parent.Parent)verify(parent,true);
    }
    FileStream Lock()
    {
        CheckRoot();long deadline=Environment.TickCount64+1000;FileStream handle;
        while(true)
        {
            try{handle=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}
            catch(IOException error)when(((error.HResult&0xffff) is 32 or 33)&&Environment.TickCount64<deadline){Thread.Sleep(20);}
        }
        try{verify(new FileInfo(lockPath),false);CheckRoot();return handle;}catch{handle.Dispose();throw;}
    }
    NativeForcedIntentDocument? Load()
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var file=new FileInfo(path);verify(file,false);
            if(stream.Length is <1 or >65536)throw new InvalidDataException("Forced intent file size refused.");
            byte[] bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);return NativeForcedIntent.Parse(bytes);
        }
        catch(FileNotFoundException){return null;}
    }
    ulong? LoadFloor()
    {
        try
        {
            using var stream=new FileStream(floorPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            var file=new FileInfo(floorPath);verify(file,false);
            if(stream.Length is <1 or >4096)throw new InvalidDataException("Forced intent floor size refused.");
            using var document=JsonDocument.Parse(stream);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Schema",out var schema)||schema.GetInt32()!=1||!root.TryGetProperty("Revision",out var revision)||!revision.TryGetUInt64(out var value)||value==0)throw new InvalidDataException("Forced intent floor refused.");
            return value;
        }
        catch(FileNotFoundException){return null;}
    }
    void WriteFloor(ulong revision)
    {
        if(revision==0)throw new InvalidDataException("Forced intent floor refused.");
        byte[] data=JsonSerializer.SerializeToUtf8Bytes(new{Schema=1,Revision=revision});
        string temporary=Path.Combine(root,"forced-intent-floor-"+Guid.NewGuid().ToString("N")+".new");bool created=false;
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){created=true;output.Write(data);output.Flush(true);}
            verify(new FileInfo(temporary),false);CheckRoot();
            if(File.Exists(floorPath))File.Replace(temporary,floorPath,null,true);else File.Move(temporary,floorPath);
            created=false;verify(new FileInfo(floorPath),false);
        }
        finally{if(created)try{File.Delete(temporary);}catch{}}
    }
    ulong EstablishFloor(NativeForcedIntentDocument document)
    {
        var floor=LoadFloor();
        if(floor is null){WriteFloor(document.Revision);return document.Revision;}
        if(floor.Value>document.Revision)throw new InvalidDataException("Forced intent rollback detected.");
        if(floor.Value<document.Revision)WriteFloor(document.Revision);
        return document.Revision;
    }
    internal NativeForcedIntentDocument Read(ulong minimumRevision)
    {
        if(minimumRevision==0)throw new ArgumentOutOfRangeException(nameof(minimumRevision));
        using var held=Lock();var document=Load()??throw new InvalidDataException("Forced intent missing; no implicit empty policy.");
        var floor=EstablishFloor(document);if(document.Revision<minimumRevision||document.Revision<floor)throw new InvalidDataException("Forced intent rollback detected.");return document;
    }
    internal NativeForcedIntentDocument Replace(ulong expectedRevision,NativeForcedIntentRule[] rules)
    {
        using var held=Lock();var previous=Load();
        if(expectedRevision==0||previous is null||previous.Revision!=expectedRevision)throw new NativeForcedIntentConflictException();
        var floor=LoadFloor();if(floor is { } established&&expectedRevision<established)throw new NativeForcedIntentConflictException();
        var next=Commit(new NativeForcedIntentDocument(1,checked(expectedRevision+1),rules));
        try{WriteFloor(next.Revision);}catch(Exception error){throw new NativeForcedIntentCommitUnknownException(next.Revision,error);}
        return next;
    }
    // Only a caller that has independently established a fresh installation
    // may initialize; missing storage after an existing install is not proof.
    internal NativeForcedIntentDocument InitializeNew()
    {
        using var held=Lock();if(Load()!=null)throw new InvalidOperationException("Forced intent already initialized.");
        var next=Commit(new NativeForcedIntentDocument(1,1,[]));WriteFloor(next.Revision);return next;
    }
    NativeForcedIntentDocument Commit(NativeForcedIntentDocument next)
    {
        byte[] data=NativeForcedIntent.Encode(next);
        string temporary=Path.Combine(root,"forced-intent-"+Guid.NewGuid().ToString("N")+".new");bool created=false,renameStarted=false;Exception? failure=null;
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {created=true;output.Write(data);output.Flush(true);}
            verify(new FileInfo(temporary),false);CheckRoot();
            renameStarted=true;
            if(File.Exists(path))File.Replace(temporary,path,null,true);else File.Move(temporary,path);
            created=false;verify(new FileInfo(path),false);
            var actual=Load()??throw new IOException("Committed forced intent disappeared.");
            if(!NativeForcedIntent.Encode(actual).AsSpan().SequenceEqual(data))throw new IOException("Committed forced intent differs from requested state.");
            return actual;
        }
        catch(Exception error){failure=renameStarted?new NativeForcedIntentCommitUnknownException(next.Revision,error):error;if(renameStarted)throw failure;throw;}
        finally{if(created)try{File.Delete(temporary);}catch(Exception cleanup){if(failure is null)throw;failure.Data["TemporaryCleanupFailure"]=cleanup;}}
    }
}
