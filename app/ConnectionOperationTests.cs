using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoutePilot;

public static class ConnectionOperationTests
{
    public static async Task Run(string root)
    {
        string folder=Path.Combine(root,"runtime","operation-test-кириллица ' & $-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(root,"FileStorage.ps1"),Path.Combine(folder,"FileStorage.ps1"));
            File.WriteAllText(Path.Combine(folder,"Configure-Connection.ps1"),"""
                [CmdletBinding()]
                param([string]$Action,[string]$OperationId,[string]$CliPath,[string]$SourceProfile,[string]$ExpectedEgressIp,[string]$UserSid,[string]$UserProfile)
                $runtime=Join-Path $PSScriptRoot 'runtime'
                . (Join-Path $PSScriptRoot 'FileStorage.ps1')
                [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'handoff-actions.txt'),$Action+"`n")
                if($Action -eq 'ReleaseConflicts'){[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'handoff.active'),'released');Write-JsonFile (Join-Path $runtime 'connection-handoff.json') @{operationId=$OperationId};exit 0}
                if($Action -eq 'CompleteHandoff' -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'complete-wait.request'))){
                    Write-ConnectionProgress 'Completing'
                    for($i=0;$i -lt 30;$i++){Assert-ConnectionOperation;Start-Sleep -Milliseconds 100}
                }
                if($Action -in @('CompleteHandoff','RestoreHandoff')){Remove-Item -LiteralPath (Join-Path $PSScriptRoot 'handoff.active'),(Join-Path $runtime 'connection-handoff.json') -Force -ErrorAction SilentlyContinue;exit 0}
                $runtime=Join-Path $PSScriptRoot 'runtime'
                . (Join-Path $PSScriptRoot 'FileStorage.ps1')
                Write-ConnectionProgress 'Preparing'
                if($SourceProfile -eq 'Failure'){throw 'Controlled setup failure'}
                Write-JsonFile (Join-Path $PSScriptRoot 'capture.json') @{profile=$SourceProfile;egress=$ExpectedEgressIp;cli=$CliPath}
                exit 0
                """,new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(folder,"SmartRouting.ps1"),"""
                [CmdletBinding()]
                param([string]$Action,[string]$OperationId)
                $runtime=Join-Path $PSScriptRoot 'runtime'
                . (Join-Path $PSScriptRoot 'FileStorage.ps1')
                Write-ConnectionProgress 'Connecting'
                try{
                    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'wait.request')){
                        for($i=0;$i -lt 50;$i++){Assert-ConnectionOperation;Start-Sleep -Milliseconds 100}
                    }
                    Assert-ConnectionOperation
                    Write-JsonFile (Join-Path $PSScriptRoot 'route.json') @{connected=($Action -eq 'Start');cleaned=($Action -eq 'Stop')}
                    Write-JsonFile (Join-Path $runtime 'session.json') @{running=($Action -eq 'Start');operationId=$OperationId}
                    exit 0
                }catch{
                    Write-JsonFile (Join-Path $PSScriptRoot 'route.json') @{connected=$false;cleaned=$true}
                    Write-JsonFile (Join-Path $runtime 'session.json') @{running=$false;operationId=$OperationId}
                    throw
                }
                """,new UTF8Encoding(true));
            string profile="Профиль ' & $(throw 'must not execute')",cli=Path.Combine(folder,"cli.exe");
            var selected=new ConnectionSelection(cli,profile,"");
            var started=await ConnectionOperation.Run(folder,"Start",false,selected);
            if(!started.Success || !started.Applied)throw new Exception("Combined setup/Start lost its successful child exit: "+started.Message);
            string handoffSequence=File.ReadAllText(Path.Combine(folder,"handoff-actions.txt")).Replace("\r","");
            if(File.Exists(Path.Combine(folder,"handoff.active")) || handoffSequence!="Apply\nReleaseConflicts\nCompleteHandoff\n")throw new Exception("Setup must finish before the old connection stops; handoff completes after Start: "+handoffSequence.Replace("\n","|")+" active="+File.Exists(Path.Combine(folder,"handoff.active")));
            using(var captured=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"capture.json"))))
                if(captured.RootElement.GetProperty("profile").GetString()!=profile || captured.RootElement.GetProperty("egress").GetString()!="")throw new Exception("Encoded setup values changed.");
            var stopped=await ConnectionOperation.Run(folder,"Stop",false);
            if(!stopped.Success)throw new Exception("A script's exit 0 bypassed the operation report.");
            var failed=await ConnectionOperation.Run(folder,"Start",false,selected with{Profile="Failure"});
            if(failed.Success || failed.Applied || !failed.Message.Contains("Controlled setup failure"))throw new Exception("Setup failure was not preserved.");
            if(File.Exists(Path.Combine(folder,"handoff.active")) || !File.ReadAllText(Path.Combine(folder,"handoff-actions.txt")).EndsWith("Apply\n"))throw new Exception("Setup failure must leave the previous connection untouched.");
            File.WriteAllText(Path.Combine(folder,"wait.request"),"");
            using var cancel=new CancellationTokenSource();
            var progress=new InlineProgress(value=>{if(value.Phase=="Connecting")cancel.Cancel();});
            var cancelled=await ConnectionOperation.Run(folder,"Start",false,null,progress,cancel.Token);
            if(cancelled.Success || !cancelled.Cancelled)throw new Exception("Cancellation must await the operation owner's cleanup.");
            if(File.Exists(Path.Combine(folder,"handoff.active")))throw new Exception("Cancelled Start must restore released connections before completion.");
            using(var route=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"route.json"))))
                if(route.RootElement.GetProperty("connected").GetBoolean() || !route.RootElement.GetProperty("cleaned").GetBoolean())throw new Exception("Cancelled Start connected late or skipped cleanup.");
            using var before=new CancellationTokenSource();before.Cancel();
            bool rejected=false;try{await ConnectionOperation.Run(folder,"Start",false,null,null,before.Token);}catch(OperationCanceledException){rejected=true;}
            if(!rejected)throw new Exception("Already cancelled work must not launch a child.");
            var timed=await ConnectionOperation.Run(folder,"Start",false,timeout:TimeSpan.FromSeconds(3));
            if(timed.Success || !timed.Cancelled)throw new Exception("The operation deadline must request cooperative cancellation rather than leave a late Start.");
            using(var locked=new FileStream(Path.Combine(folder,"runtime","connection-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                var busy=await ConnectionOperation.Run(folder,"Start",false);
                if(busy.Success || !ConnectionOperation.Active(folder) || UpdateJournal.BlockReason(folder,false,false,false,false)==null)throw new Exception("An unfinished operation must block duplicate Start and release installation.");
            }
            File.Delete(Path.Combine(folder,"wait.request"));
            File.WriteAllText(Path.Combine(folder,"complete-wait.request"),"");
            using(var lateCancel=new CancellationTokenSource())
            {
                var completedProgress=new InlineProgress(value=>{if(value.Phase=="Completing")lateCancel.Cancel();});
                var late=await ConnectionOperation.Run(folder,"Start",false,null,completedProgress,lateCancel.Token);
                using var route=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"route.json")));
                if(late.Success || !late.Cancelled || route.RootElement.GetProperty("connected").GetBoolean() || File.Exists(Path.Combine(folder,"handoff.active")))throw new Exception("Cancellation after Start must stop the new route before restoring the previous connection.");
            }
            File.Delete(Path.Combine(folder,"complete-wait.request"));
            using(var update=UpdateJournal.ReservePreparation(folder))
            {
                var blocked=await ConnectionOperation.Run(folder,"Start",false);
                if(blocked.Success)throw new Exception("An update preparation must reject a concurrent Start.");
                var stopDuringUpdate=await ConnectionOperation.Run(folder,"Stop",false);
                if(!stopDuringUpdate.Success)throw new Exception("An update preparation must allow its own Stop.");
            }
        }
        catch
        {
            if(Directory.Exists(Path.Combine(folder,"logs")))
                File.WriteAllText(Path.Combine(root,"logs","operation-fixture-failure.log"),string.Join("\n",Array.ConvertAll(Directory.GetFiles(Path.Combine(folder,"logs")),p=>Path.GetFileName(p)+"\n"+File.ReadAllText(p))));
            throw;
        }
        finally
        {
            string allowed=Path.GetFullPath(Path.Combine(root,"runtime"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(folder).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe operation fixture cleanup.");
            if(Directory.Exists(folder))Directory.Delete(folder,true);
        }
    }
    sealed class InlineProgress(Action<ConnectionProgress> action):IProgress<ConnectionProgress>{public void Report(ConnectionProgress value)=>action(value);}
}
