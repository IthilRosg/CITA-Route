param([Parameter(Mandatory)][string]$Image,[Parameter(Mandatory)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
# Inspect the exact bundled managed image; do not invoke the service or its methods.
$imageLease=[IO.File]::Open($Image,'Open','Read','Read')
try{
if((Get-FileHash -InputStream $imageLease).Hash -cne $ExpectedSha256){throw 'Service image hash differs'}
$sdk=Join-Path $PSScriptRoot '../../runtime/dotnet10/sdk/10.0.402/Microsoft.NET.HostModel.dll'
$hostModel=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($sdk))
$isBundle=$hostModel.GetType('Microsoft.NET.HostModel.Bundle.Bundler').GetMethod('IsBundle',[Reflection.BindingFlags]'Static,Public,NonPublic')
$bundleArgs=[object[]]@([IO.Path]::GetFullPath($Image),[long]0)
if(!$isBundle.Invoke($null,$bundleArgs)){throw 'Bundled agent required'}
$stream=$imageLease
try{
 $stream.Position=$bundleArgs[1];$reader=[IO.BinaryReader]::new($stream)
 if($reader.ReadUInt32() -ne 6 -or $reader.ReadUInt32() -ne 0){throw 'Unsupported bundle version'}
 $count=$reader.ReadInt32();if($count -lt 1 -or $count -gt 64){throw 'Bundle count refused'}
 $null=$reader.ReadString();for($i=0;$i -lt 5;$i++){$null=$reader.ReadInt64()}
 $entry=$null
 for($i=0;$i -lt $count;$i++){
  $offset=$reader.ReadInt64();$size=$reader.ReadInt64();$compressed=$reader.ReadInt64();$kind=$reader.ReadByte();$name=$reader.ReadString()
  if($name -ceq 'RoutePilot.Agent.dll'){if($entry -or $compressed -ne 0 -or $kind -ne 1 -or $offset -lt 0 -or $size -lt 1 -or $size -gt 16777216 -or $offset+$size -gt $bundleArgs[1]){throw 'Managed bundle entry refused'};$entry=@{offset=$offset;size=$size}}
 }
 if(!$entry){throw 'Managed agent absent'}
 $stream.Position=$entry.offset;$bytes=$reader.ReadBytes($entry.size);if($bytes.Length -ne $entry.size){throw 'Truncated managed image'}
}finally{$stream.Position=0}
$assembly=[Reflection.Assembly]::Load($bytes)
$type=$assembly.GetType('RoutePilot.Agent.NativeServiceRoute',$true)
$opcodes=@{};[Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')|ForEach-Object{$op=$_.GetValue($null);$opcodes[[int]$op.Value -band 65535]=$op}
function Decode([Reflection.MethodInfo]$method){
 $body=$method.GetMethodBody();if(!$body -or @($body.ExceptionHandlingClauses|Where-Object {$_.Flags -notin @([Reflection.ExceptionHandlingClauseOptions]::Finally,[Reflection.ExceptionHandlingClauseOptions]::Clause)}).Count){throw 'Unsupported method exception flow'}
 $il=$body.GetILAsByteArray();$items=[Collections.Generic.List[object]]::new();$at=0
 while($at -lt $il.Length){
  $start=$at;$code=[int]$il[$at++];if($code -eq 254){$code=65024+[int]$il[$at++]};if(!$opcodes.ContainsKey($code)){throw "Unknown opcode $code at $start in $($method.Name)"};$op=$opcodes[$code];$operand=$null;$targets=@()
  switch($op.OperandType.ToString()){
   'InlineNone'{}
   'ShortInlineBrTarget'{$delta=[int][sbyte]::Parse($il[$at].ToString('X2'),[Globalization.NumberStyles]::HexNumber);$at++;$targets=@($at+$delta)}
   'InlineBrTarget'{$delta=[BitConverter]::ToInt32($il,$at);$at+=4;$targets=@($at+$delta)}
   'InlineSwitch'{$n=[BitConverter]::ToInt32($il,$at);$at+=4;if($n -lt 0 -or $n -gt 1024){throw 'Switch budget'};$end=$at+4*$n;for($j=0;$j -lt $n;$j++){$targets+=($end+[BitConverter]::ToInt32($il,$at));$at+=4}}
   'InlineMethod'{$token=[BitConverter]::ToInt32($il,$at);$at+=4;$operand=$method.Module.ResolveMethod($token)}
   'InlineField'{$token=[BitConverter]::ToInt32($il,$at);$at+=4;$operand=$method.Module.ResolveField($token)}
   'InlineI8'{$at+=8}
   'InlineR'{$at+=8}
   'ShortInlineR'{$at+=4}
   'InlineVar'{$at+=2}
   'ShortInlineVar'{$at++}
   'ShortInlineI'{$at++}
   {$_ -in @('InlineI','InlineString','InlineSig','InlineType','InlineTok')}{$at+=4}
   default{throw 'Unsupported operand'}
  }
  if($at -gt $il.Length){throw 'Truncated opcode'}
  $exceptionTargets=@($body.ExceptionHandlingClauses|Where-Object {$start -ge $_.TryOffset -and $start -lt $_.TryOffset+$_.TryLength}|ForEach-Object {$_.HandlerOffset})
  $items.Add([pscustomobject]@{offset=$start;next=$at;op=$op;operand=$operand;targets=$targets;exceptionTargets=$exceptionTargets})
 }
 return ,$items
}
function Assert-Dominated($items,$barrier,$target){
 $map=@{};foreach($item in $items){$map[$item.offset]=$item}
 $seen=[Collections.Generic.HashSet[int]]::new();$pending=[Collections.Generic.Stack[int]]::new();$pending.Push(0)
 while($pending.Count){$at=$pending.Pop();if(!$seen.Add($at)){continue};if(!$map.ContainsKey($at)){throw 'Invalid control-flow target'};$item=$map[$at]
  foreach($edge in $item.exceptionTargets){$pending.Push($edge)}
  if(& $barrier $item){continue};if(& $target $item){throw 'State commit can bypass successful durable save'}
  foreach($edge in $item.targets){$pending.Push($edge)}
  if($item.op.FlowControl.ToString() -notin @('Branch','Return','Throw')){$pending.Push($item.next)}
 }
}
# Counterexamples for the limited CFG check. This does not upgrade it into a
# general-purpose verifier of CLR exception-unwinding semantics.
function FixtureNode($at,$op,$targets=@(),$exceptions=@()){
 [pscustomobject]@{offset=$at;next=$at+1;op=$op;operand=$null;targets=$targets;exceptionTargets=$exceptions}
}
$valid=@((FixtureNode 0 ([Reflection.Emit.OpCodes]::Call)),(FixtureNode 1 ([Reflection.Emit.OpCodes]::Stfld)),(FixtureNode 2 ([Reflection.Emit.OpCodes]::Ret)))
Assert-Dominated $valid {param($x)$x.offset -eq 0} {param($x)$x.offset -eq 1}
$bypass=@((FixtureNode 0 ([Reflection.Emit.OpCodes]::Brtrue_S) @(2)),(FixtureNode 1 ([Reflection.Emit.OpCodes]::Call)),(FixtureNode 2 ([Reflection.Emit.OpCodes]::Stfld)),(FixtureNode 3 ([Reflection.Emit.OpCodes]::Ret)))
$exceptionBypass=@((FixtureNode 0 ([Reflection.Emit.OpCodes]::Call) @() @(1)),(FixtureNode 1 ([Reflection.Emit.OpCodes]::Stfld)),(FixtureNode 2 ([Reflection.Emit.OpCodes]::Ret)))
$fixtures=[Collections.Generic.List[object]]::new();$fixtures.Add($bypass);$fixtures.Add($exceptionBypass)
foreach($fixture in $fixtures){
 $rejected=$false
 try{Assert-Dominated $fixture {param($x)$x.op.Name -eq 'call'} {param($x)$x.op.Name -eq 'stfld'}}catch{if($_.Exception.Message -ne 'State commit can bypass successful durable save'){throw};$rejected=$true}
 if(!$rejected){throw 'CFG counterexample was accepted'}
}
$results=@()
foreach($name in @('Request','RequestImport')){
 $method=$type.GetMethod($name,[Reflection.BindingFlags]'Instance,NonPublic');if(!$method){throw 'Service method missing'};$items=Decode $method
 $saves=@($items|Where-Object {$_.op.Name -eq 'call' -and $_.operand.DeclaringType -eq $type -and $_.operand.Name -eq 'Save'})
 $stores=@($items|Where-Object {$_.op.Name -eq 'stfld' -and $_.operand.DeclaringType -eq $type -and $_.operand.Name -eq 'intent'})
 if($saves.Count -ne 1 -or $stores.Count -ne 1){throw 'Unexpected service commit shape'}
 Assert-Dominated $items {param($x)$x.offset -eq $saves[0].offset} {param($x)$x.offset -eq $stores[0].offset}
 $results+=@{method=$name;saveOffset=$saves[0].offset;intentStoreOffset=$stores[0].offset;saveDominatesCommit=$true}
}
$save=Decode ($type.GetMethod('Save',[Reflection.BindingFlags]'Instance,NonPublic'))
if(@($type.GetMethod('Save',[Reflection.BindingFlags]'Instance,NonPublic').GetMethodBody().ExceptionHandlingClauses|Where-Object {$_.Flags -ne [Reflection.ExceptionHandlingClauseOptions]::Finally}).Count){throw 'Save may catch a failed commit'}
$moves=@($save|Where-Object {$_.op.Name -eq 'call' -and $_.operand.DeclaringType -eq [IO.File] -and $_.operand.Name -eq 'Move' -and $_.operand.GetParameters().Count -eq 3})
if($moves.Count -ne 1){throw 'Atomic replace call missing'}
$before=@($save|Where-Object {$_.next -eq $moves[0].offset});if($before.Count -ne 1 -or $before[0].op.Name -ne 'ldc.i4.1'){throw 'Save does not replace destination'}
$destination=@($save|Where-Object {$_.next -eq $before[0].offset});if($destination.Count -ne 1 -or $destination[0].op.Name -ne 'ldfld' -or $destination[0].operand.DeclaringType -ne $type -or $destination[0].operand.Name -ne 'statePath' -or $destination[0].operand.FieldType -ne [string]){throw 'Save destination is not the native state path'}
$receiver=@($save|Where-Object {$_.next -eq $destination[0].offset});if($receiver.Count -ne 1 -or $receiver[0].op.Name -ne 'ldarg.0'){throw 'Save destination receiver differs'}
Assert-Dominated $save {param($x)$x.offset -eq $moves[0].offset} {param($x)$x.op.Name -eq 'ret'}
[pscustomobject]@{imageSha256=$ExpectedSha256;managedSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));methods=$results;saveUsesAtomicOverwrite=$true;destination='this.statePath';cfgCounterexamplesRejected=$true;scope='Limited static IL inspection of exact image; Request/RequestImport Save order and Save destination. Not a formal verification of all methods or EH transitions; no service method executed'}
}finally{$imageLease.Dispose()}
