using System.Text;
using System.Text.Json;
namespace RoutePilot.Agent;
internal static class NativeMaintenanceValidationTests
{
    internal static void Run()
    {
        using(var process=System.Diagnostics.Process.GetCurrentProcess())
        {
            var self=ProcessIdentity.From(process);
            if(!NativeMaintenanceValidation.MayUseOperation(self,self)||!NativeMaintenanceValidation.MayUseOperation(null,self))throw new Exception("Operation owner or unowned recovery was refused.");
            if(NativeMaintenanceValidation.MayUseOperation(self,self with{StartedUtc=self.StartedUtc.AddSeconds(2)})||NativeMaintenanceValidation.MayUseOperation(self,self with{Path=self.Path+".other"})||NativeMaintenanceValidation.MayUseOperation(self,self with{Pid=int.MaxValue}))throw new Exception("Foreign operation caller was accepted.");
            if(!NativeMaintenanceValidation.MayUseOperation(self with{Pid=int.MaxValue},self))throw new Exception("Retired operation blocked recovery.");
        }
        string id=new('a',32);
        void Reject(Action action){try{action();}catch(Exception e)when(e is InvalidDataException or JsonException){return;}throw new Exception("Unsafe maintenance validation accepted.");}
        NativeMaintenanceValidation.CheckMarkerBytes(Encoding.ASCII.GetBytes(id+"\n"),id);
        Reject(()=>NativeMaintenanceValidation.CheckMarkerBytes(Encoding.ASCII.GetBytes(id+"\r\n"),id));
        Reject(()=>NativeMaintenanceValidation.CheckMarkerBytes(Encoding.ASCII.GetBytes(id+"\n"),new string('b',32)));
        Reject(()=>NativeMaintenanceValidation.CheckMarkerBytes(Encoding.ASCII.GetBytes(id.ToUpperInvariant()+"\n"),id.ToUpperInvariant()));
        int calls=0;
        foreach(string input in new[]{"{\"Desired\":true}","{\"Desired\":false,\"Desired\":true}","{\"Desired\":\"false\"}","{}"}){
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes(input));
            Reject(()=>NativeMaintenanceValidation.LoadChecked(stream,()=>++calls));
        }
        if(calls!=0)throw new Exception("Invalid intent invoked native loading.");
        using(var input=new MemoryStream(Encoding.UTF8.GetBytes("{\"Desired\":false}")))
            if(NativeMaintenanceValidation.LoadChecked(input,()=>++calls)!=1||input.Position!=0)throw new Exception("Disconnected intent not accepted.");
        var state=JsonSerializer.SerializeToElement(new{phase="Stopping",desired=false,operation=id,generation=7,errorCode=(string?)null,profileName="private-name",runtime=new{Secret="hidden"}});
        var safe=NativeMaintenanceValidation.SafeStatus(state);
        if(safe.GetProperty("phase").GetString()!="Stopping"||safe.TryGetProperty("profileName",out _)||safe.TryGetProperty("runtime",out _))throw new Exception("Maintenance status exposed extra state.");
        var bad=JsonSerializer.SerializeToElement(new{phase="Connected",desired=true,operation=id,generation=7,errorCode=(string?)null});
        Reject(()=>NativeMaintenanceValidation.SafeStatus(bad));
    }
}
