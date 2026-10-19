using System;
using System.IO;

namespace RoutePilot;

public static class InstallationMaintenance
{
    public static bool Active(string root)
    {
        string path=Path.Combine(root,"runtime","installation-maintenance.lock");
        if(!File.Exists(path))return false;
        try{using var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return false;}
        catch(IOException){return true;}catch(UnauthorizedAccessException){return true;}
    }
}
