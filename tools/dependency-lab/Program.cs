using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RoutePilot;

// Isolated acceptance helper. "verify" is read-only; connect/stop require the
// explicit guest-only runner and exercise the production operation transaction.
if(args.Length==2&&args[0] is "connect" or "stop")
{
    string root=Path.GetFullPath(args[1]);
    if(root!=@"C:\CitaLab\PlatformNext")return 2;
    using var backend=new Backend(root);
    ConnectionSelection? selected=null;
    if(args[0]=="connect")
    {
        var inventory=await new ConnectionSetup(root,backend).Discover();
        if(!inventory.CliTrusted||string.IsNullOrEmpty(inventory.SelectedProfile))return 2;
        selected=new(inventory.CliPath,inventory.SelectedProfile,inventory.ExpectedEgressIp);
    }
    var result=await ConnectionOperation.Run(root,args[0]=="connect"?"Start":"Stop",false,selected);
    Console.WriteLine(JsonSerializer.Serialize(new{result.Success,result.Cancelled,result.Pending}));return result.Success?0:1;
}
if(args.Length!=3||args[0]!="verify")return 2;
try
{
    string root=Path.GetFullPath(args[1]),file=Path.GetFullPath(args[2]);
    using var backend=new Backend(root);
    using var lease=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
    await WireSockDependency.Verify(file,backend);
    Console.WriteLine(JsonSerializer.Serialize(new{success=true,version=WireSockDependency.Reviewed.Version.ToString(),
        bytes=lease.Length,sha256=Convert.ToHexString(SHA256.HashData(lease)),signatureVerified=true,installerStarted=false}));
    return 0;
}
catch(Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new{success=false,error=ex.GetType().Name,installerStarted=false}));return 1;
}
