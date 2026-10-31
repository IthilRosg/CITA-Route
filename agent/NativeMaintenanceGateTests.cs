namespace RoutePilot.Agent;

internal static class NativeMaintenanceGateTests
{
    internal static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"cita-maintenance-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string marker=Path.Combine(root,"native-upgrade.pending");
            bool Pending()=>NativeMaintenanceGate.IsPendingAt(marker,File.GetAttributes);
            if(Pending())throw new Exception("Absent marker must allow loading.");
            int calls=0;
            int Load()=>++calls;
            if(NativeMaintenanceGate.LoadChecked(Pending,Load)!=1)throw new Exception("Absent marker must invoke loading.");
            void Refused()
            {
                int prior=calls;
                try{NativeMaintenanceGate.LoadChecked(Pending,Load);throw new Exception("Maintenance did not refuse loading.");}
                catch(NativeMaintenanceInProgress){}
                if(calls!=prior)throw new Exception("Maintenance touched loader.");
            }
            File.WriteAllText(marker,"invalid");Refused();File.Delete(marker);
            Directory.CreateDirectory(marker);Refused();Directory.Delete(marker);
            if(!NativeMaintenanceGate.IsPendingAt(marker,_=>throw new UnauthorizedAccessException()))throw new Exception("Unreadable marker must fail closed.");
            if(!NativeMaintenanceGate.IsPendingAt(marker,_=>FileAttributes.ReparsePoint))throw new Exception("A link marker must fail closed.");
            var snapshot=System.Text.Json.JsonSerializer.SerializeToElement(new{phase="Connected",desired=true,profileName="example",generation=17});
            var status=NativeMaintenanceGate.StatusOverlay(snapshot);
            if(status.GetProperty("phase").GetString()!="Unavailable"||status.GetProperty("errorCode").GetString()!=nameof(NativeMaintenanceInProgress)||!status.GetProperty("desired").GetBoolean()||status.GetProperty("profileName").GetString()!="example"||status.GetProperty("generation").GetInt32()!=17||snapshot.GetProperty("phase").GetString()!="Connected")throw new Exception("Maintenance status must preserve metadata without mutating the live snapshot.");
            if(NativeMaintenanceGate.StatusOverlay(null).GetProperty("errorCode").GetString()!=nameof(NativeMaintenanceInProgress))throw new Exception("Maintenance status is required before backend loads.");
            if(!NativeMaintenanceGate.IsPendingAt(marker,_=>throw new DirectoryNotFoundException()))throw new Exception("Missing base must fail closed.");
            if(Pending())throw new Exception("Removed marker must permit later startup.");
        }
        finally{Directory.Delete(root,true);}
    }
}
