using System;
using System.IO;

namespace RoutePilot;

internal static class WireSockMaintenanceTests
{
    internal static void Run(string root)
    {
        string folder=Path.Combine(root,"runtime","dependency-journal-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            if(WireSockMaintenance.BlockReason(folder)!=null)throw new Exception("Missing dependency journal blocked a fresh client.");
            foreach(string phase in new[]{"Armed","Installing","NeedsAttention"})
            {
                WireSockMaintenance.Write(folder,phase);
                if(WireSockMaintenance.BlockReason(folder)==null)throw new Exception("Incomplete dependency installation permitted Start.");
            }
            var trusted=new ConnectionInventory{Kind="SecureConnectCli",CliPath="fixture",Version="3.6.1.1",CliTrusted=true,ManagementReady=true};
            WireSockMaintenance.Reconcile(folder,trusted);
            if(WireSockMaintenance.BlockReason(folder)!=null)throw new Exception("Fresh trusted inventory did not reconcile completed maintenance.");
            WireSockMaintenance.Write(folder,"Installing");trusted.CliTrusted=false;WireSockMaintenance.Reconcile(folder,trusted);
            if(WireSockMaintenance.BlockReason(folder)==null)throw new Exception("Unverified CLI cleared maintenance journal.");
            File.WriteAllText(Path.Combine(folder,"data","wiresock-maintenance.json"),"{");
            if(WireSockMaintenance.BlockReason(folder)==null)throw new Exception("Corrupt maintenance journal authorized Start.");
        }
        finally{Directory.Delete(folder,true);}
    }
}
