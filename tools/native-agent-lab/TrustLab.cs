using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RoutePilot.Agent;

internal static class TrustLab
{
    internal static int Run(string root,string digest)
    {
        bool valid=false,wrongDigest=false,writeDenied=false,replaceDenied=false,unsafeAcl=false,tamper=false;string? errorType=null;
        try
        {
            using(var lease=NativeTrustedBundle.Open(root,digest))
            {
                valid=File.Exists(lease.OwnerPath)&&File.Exists(lease.CorePath);
                try{File.WriteAllText(lease.OwnerPath,"refused");}catch(IOException){writeDenied=true;}
                try{File.Move(lease.CorePath,lease.CorePath+".moved");}catch(IOException){replaceDenied=true;}
                try{using var bad=NativeTrustedBundle.Open(root,new string('0',64));}catch(InvalidDataException){wrongDigest=true;}
            }
            var directory=new DirectoryInfo(root);string original=directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);var changed=directory.GetAccessControl();
            changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.Modify,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));
            try
            {
                directory.SetAccessControl(changed);
                try{using var bad=NativeTrustedBundle.Open(root,digest);}catch(UnauthorizedAccessException){unsafeAcl=true;}
            }
            finally{var restored=new DirectorySecurity();restored.SetSecurityDescriptorSddlForm(original);directory.SetAccessControl(restored);}
            var catalog=JsonSerializer.Deserialize<NativeTrustedBundle.Catalog>(File.ReadAllText(Path.Combine(root,"native-catalog.json")))!;
            // Mutate a single byte after releasing the lease; acceptance must
            // reject it even though the catalog and ACLs remain unchanged.
            using(var file=new FileStream(Path.Combine(root,catalog.Owner.Name),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {int value=file.ReadByte();file.Position=0;file.WriteByte((byte)(value^1));}
            try{using var bad=NativeTrustedBundle.Open(root,digest);}catch(InvalidDataException){tamper=true;}
        }
        catch(Exception e){errorType=e.GetType().Name;}
        bool success=valid&&wrongDigest&&writeDenied&&replaceDenied&&unsafeAcl&&tamper;
        Console.WriteLine(JsonSerializer.Serialize(new{schema=1,success,valid,wrongDigest,writeDenied,replaceDenied,unsafeAcl,tamper,errorType,scope="Protected component acceptance only; no production service registration"}));return success?0:1;
    }
}
