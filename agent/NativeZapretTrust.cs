using System.Security.Cryptography;

namespace RoutePilot.Agent;

// Like the native core bundle: protect both the ACL chain and the checked
// bytes, and deny replacement until every child has demonstrably exited.
internal sealed class NativeZapretTrust:IDisposable
{
    readonly List<FileStream> held=new();
    internal static NativeZapretTrust Open(NativeZapretBundle bundle)
    {
        var lease=new NativeZapretTrust();
        try
        {
            bundle.Verify();
            var root=new DirectoryInfo(bundle.Root);NativeTrustedBundle.CheckAcl(root);
            for(var parent=root.Parent;parent!=null;parent=parent.Parent)NativeTrustedBundle.CheckAcl(parent,true);
            foreach(var name in NativeZapretBundle.RequiredFiles)
            {
                var file=new FileInfo(Path.Combine(bundle.Root,name.Replace('/',Path.DirectorySeparatorChar)));
                for(var parent=file.Directory;parent!=null&&!parent.FullName.Equals(root.FullName,StringComparison.OrdinalIgnoreCase);parent=parent.Parent)
                    NativeTrustedBundle.CheckAcl(parent);
                NativeTrustedBundle.CheckAcl(file);
                var handle=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);lease.held.Add(handle);
                if(handle.Length is <1 or >8388608||!Convert.ToHexString(SHA256.HashData(handle)).Equals(bundle.PinnedHashes[name],StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Protected Zapret component changed.");
            }
            return lease;
        }
        catch{lease.Dispose();throw;}
    }
    public void Dispose(){foreach(var file in held)file.Dispose();held.Clear();}
}
