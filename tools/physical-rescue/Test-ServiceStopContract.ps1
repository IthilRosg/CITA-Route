param([Parameter(Mandatory)][string]$Image,[Parameter(Mandatory)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
# Reuse the bounded, read-only bundle/IL decoder. No service method is invoked.
$decoder=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-ServiceCommitContract.ps1'))
$anchor=$decoder.IndexOf('$results=@()');if($anchor -lt 0){throw 'Decoder anchor differs.'}
$prefix=$decoder.Substring(0,$anchor).Replace("'ShortInlineI'{"+'$at++'+"}","'ShortInlineI'{"+'$operand=[int][sbyte]::Parse($il[$at].ToString(''X2''),[Globalization.NumberStyles]::HexNumber);$at++'+"}")
$prefix=$prefix.Replace('$PSScriptRoot',("'"+$PSScriptRoot.Replace("'","''")+"'"))
$tail=@'
$service=$assembly.GetType('RoutePilot.Agent.NativeService',$true)
$binding=[Reflection.BindingFlags]'Static,NonPublic'
$control=Decode ($service.GetMethod('Control',$binding));$rearm=Decode ($service.GetMethod('RearmStartup',$binding))
function Evaluate-Gate($Rows,[int]$InputControl,[switch]$ControlPrefix){
 $map=@{};foreach($row in $Rows){$map[[int]$row.offset]=$row}
 $stack=[Collections.Generic.Stack[int]]::new();$local=0;$at=0
 for($steps=0;$steps -lt 64;$steps++){
  if($ControlPrefix -and $at -ge 30){return ($at -eq 30)}
  if(!$map.ContainsKey($at)){throw 'Unknown IL gate target'};$x=$map[$at];$next=$x.next
  switch($x.op.Name){
   'ldarg.0'{$stack.Push($InputControl)}
   'ldc.i4.0'{$stack.Push(0)}
   'ldc.i4.1'{$stack.Push(1)}
   'ldc.i4.5'{$stack.Push(5)}
   'ldc.i4.s'{$stack.Push([int]$x.operand)}
   'stloc.0'{$local=$stack.Pop()}
   'ldloc.0'{$stack.Push($local)}
   'beq.s'{$b=$stack.Pop();$a=$stack.Pop();if($a -eq $b){$next=[int]$x.targets[0]}}
   'bne.un.s'{$b=$stack.Pop();$a=$stack.Pop();if($a -ne $b){$next=[int]$x.targets[0]}}
   'br.s'{$next=[int]$x.targets[0]}
   'brfalse.s'{if($stack.Pop() -eq 0){$next=[int]$x.targets[0]}}
   'call'{if(!$ControlPrefix -or $x.operand.DeclaringType -ne $service -or $x.operand.Name -cne 'RearmStartup'){throw 'Unsupported IL gate call'};$stack.Push([int](Evaluate-Gate $rearm ($stack.Pop())))}
   'ret'{return [bool]$stack.Pop()}
   default{throw ('Unsupported IL gate opcode '+$x.op.Name)}
  };$at=$next
 };throw 'IL gate budget exceeded'
}
# The restricted Control prefix has one conditional PrepareForShutdown call.
# Pin its location rather than silently interpret a different method shape.
$prepares=@($control|Where-Object {$_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -ceq 'PrepareForShutdown'})
if($prepares.Count -ne 1 -or $prepares[0].offset -ne 41 -or $prepares[0].operand.DeclaringType.FullName -cne 'RoutePilot.Agent.AgentHost'){throw 'Installed Control shape differs.'}
$cases=@(foreach($value in @(0,1,5,13,15)){[pscustomobject]@{control=$value;rearms=(Evaluate-Gate $rearm $value);mayReachPrepare=(Evaluate-Gate $control $value -ControlPrefix)}})
foreach($case in $cases){if($case.rearms -ne ($case.control -in @(5,15)) -or $case.mayReachPrepare -ne ($case.control -in @(5,15))){throw 'Installed stop gate differs.'}}
[pscustomobject]@{imageSha256=$ExpectedSha256;managedSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));cases=$cases;noServiceMethodExecuted=$true;scope='Restricted interpretation of exact installed RearmStartup and Control prefix. Does not verify asynchronous shutdown, cleanup, or real SCM behavior.'}
}finally{$imageLease.Dispose()}
'@
& ([ScriptBlock]::Create($prefix+$tail)) -Image $Image -ExpectedSha256 $ExpectedSha256
