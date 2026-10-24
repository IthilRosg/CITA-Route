$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystemProduct).UUID -ne 'E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83'){throw 'Wrong isolated guest'}
$report=@{performed=$false;success=$false;startedUtc=[datetime]::UtcNow.ToString('o')};$timer=[IntPtr]::Zero
try{
 Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CitaSleepLab {
 [DllImport("powrprof.dll",SetLastError=true)]public static extern byte GetPwrCapabilities([Out]byte[] data);
 [DllImport("powrprof.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.U1)]public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)]bool hibernate,[MarshalAs(UnmanagedType.U1)]bool force,[MarshalAs(UnmanagedType.U1)]bool disableWake);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr CreateWaitableTimer(IntPtr attributes,bool manualReset,string name);
 [DllImport("kernel32.dll",SetLastError=true)]public static extern bool SetWaitableTimer(IntPtr handle,ref long due,int period,IntPtr callback,IntPtr argument,bool resume);
 [DllImport("kernel32.dll")]public static extern void SetLastError(uint error);
 [DllImport("kernel32.dll")]public static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll")]public static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll")]public static extern bool GetSystemPowerStatus([Out]byte[] data);
}
'@
 $capabilities=New-Object byte[] 256
 if([CitaSleepLab]::GetPwrCapabilities($capabilities) -eq 0 -or $capabilities[3] -eq 0){$report.reason='GuestS1Unavailable';throw 'S1 unavailable'}
 $policy=& powercfg.exe /query SCHEME_CURRENT SUB_SLEEP RTCWAKE
 $indices=@([regex]::Matches(($policy -join "`n"),'0x([a-fA-F0-9]{8})')|ForEach-Object {[Convert]::ToUInt32($_.Groups[1].Value,16)})
 $power=New-Object byte[] 16
 if($indices.Count -ne 2 -or -not [CitaSleepLab]::GetSystemPowerStatus($power)){$report.reason='WakePolicyUnconfirmed';throw 'Wake policy unconfirmed'}
 $index=if($power[0] -eq 1){0}elseif($power[0] -eq 0){1}else{-1}
 if($index -lt 0 -or $indices[$index] -eq 0){$report.reason='GuestWakeTimerDisabled';throw 'Wake timers disabled; power policy preserved'}
 $timer=[CitaSleepLab]::CreateWaitableTimer([IntPtr]::Zero,$true,$null)
 if($timer -eq [IntPtr]::Zero){throw 'Wake timer unavailable'}
 [long]$due=-150000000
 [CitaSleepLab]::SetLastError(0)
 $armed=[CitaSleepLab]::SetWaitableTimer($timer,[ref]$due,0,[IntPtr]::Zero,[IntPtr]::Zero,$true)
 $errorCode=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
 if(-not $armed -or $errorCode -eq 50){$report.reason='GuestWakeResumeUnsupported';throw 'Wake timer cannot resume this guest'}
 $began=[datetime]::UtcNow;$clock=[Diagnostics.Stopwatch]::StartNew()
 [IO.File]::WriteAllText('C:\CitaLab\NativeAwg\sleep-request.json',(@{stage='GuestSleepRequested';requestedUtc=$began.ToString('o')}|ConvertTo-Json -Compress))
 Start-Sleep -Seconds 3
 if(-not [CitaSleepLab]::SetSuspendState($false,$false,$false)){$report.reason='GuestSuspendRefused';throw 'Guest suspend refused'}
 $report.performed=$true;$report.elapsedMs=$clock.ElapsedMilliseconds;$report.wakeTimerSignaled=([CitaSleepLab]::WaitForSingleObject($timer,5000) -eq 0)
 $report.sleepEventSeen=[bool](Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power';Id=42;StartTime=$began.ToLocalTime()} -MaxEvents 1 -ErrorAction SilentlyContinue)
 $report.success=[bool]($report.sleepEventSeen -and $report.elapsedMs -ge 5000)
 $report.automaticWakeVerified=$false
}catch{$report.errorType=$_.Exception.GetType().Name}
finally{if($timer -ne [IntPtr]::Zero){[CitaSleepLab]::CloseHandle($timer)|Out-Null};$report.finishedUtc=[datetime]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 3|Set-Content C:\CitaLab\NativeAwg\os-sleep-result.json -Encoding UTF8}
if(-not $report.success){exit 1}
