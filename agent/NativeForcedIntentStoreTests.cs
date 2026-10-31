namespace RoutePilot.Agent;

internal static class NativeForcedIntentStoreTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"cita-forced-store-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var store=NativeForcedIntentStore.ForTests(root,(item,_)=>{if(item is FileInfo file&&!file.Exists)throw new InvalidDataException("ACL checks require an existing file.");});
            bool missing=false;try{store.Read(0);}catch(InvalidDataException){missing=true;}Check(missing,"Missing document silently became an empty policy.");
            var rule=new NativeForcedIntentRule("example.com",@"C:\Browser\App.exe","tcp",443,"direct");
            Check(store.InitializeNew().Revision==1&&store.Replace(1,[rule]).Revision==2,"Initial and first user revisions were not saved.");
            var reopened=NativeForcedIntentStore.ForTests(root);Check(reopened.Read(2).Rules.Length==1,"Saved intent was not recovered.");
            bool stale=false;try{reopened.Replace(1,[]);}catch(InvalidOperationException){stale=true;}Check(stale&&reopened.Read(2).Rules.Length==1,"Stale writer replaced a newer policy.");
            bool rollback=false;try{reopened.Read(3);}catch(InvalidDataException){rollback=true;}Check(rollback,"Trusted revision floor was ignored.");
            int targetChecks=0;var uncertain=NativeForcedIntentStore.ForTests(root,(item,_)=>{if(item is FileInfo&&item.Name=="forced-intent.json"&&++targetChecks==2)throw new IOException("Injected post-rename failure.");});
            bool unknown=false;try{uncertain.Replace(2,[rule]);}catch(NativeForcedIntentCommitUnknownException error){unknown=error.AttemptedRevision==3;}
            Check(unknown&&reopened.Read(3).Revision==3,"A post-rename failure did not require reconciliation with disk.");
            Check(reopened.Replace(3,[]).Revision==4&&store.Read(4).Rules.Length==0,"Explicit removal did not persist.");
            string path=Path.Combine(root,"forced-intent.json");File.WriteAllText(path,"{");
            bool corrupt=false;try{store.Replace(4,[rule]);}catch(System.Text.Json.JsonException){corrupt=true;}
            Check(corrupt&&File.ReadAllText(path)=="{","Corruption was silently overwritten.");
            File.Delete(path);bool lost=false;try{reopened.Replace(0,[rule]);}catch(InvalidOperationException){lost=true;}Check(lost&&!File.Exists(path),"Missing existing storage was silently recreated.");
            Console.WriteLine("PASS: forced intent disk round-trip, explicit empty policy, stale writer, revision floor and corrupt-file refusal; ACL test pending.");
        }
        finally{Directory.Delete(root,true);}
    }
}
