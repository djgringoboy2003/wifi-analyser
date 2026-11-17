# Fault-injection tests. All hardware, restore-point and scheduler commands are mocked.
# The production script is redirected into a unique workspace folder before execution.
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\recovery-tests'))
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\AirScope.Windows\Services\Recovery.ps1') -Raw
$testRoot=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $testRoot -Force
$script:passed=0; $script:failed=0
$adapterId='12345678-1234-1234-1234-123456789abc'

function Scenario {
    param([string]$Name,[scriptblock]$Body)
    $scenarioRoot=Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    $global:AirScopeTest=@{Root=$scenarioRoot; Properties=@{PreferredBand='2';LowPowerEnable='1'}; Writes=0; Restarts=0; Task=$false; Checkpoint=$true; TaskFailure=$false; WriteFailure=$false; RestoreFailure=$false; Description=''; Status='Up'}
    $rewritten=$source.Replace("[Environment]::GetFolderPath('CommonApplicationData')",("'"+$scenarioRoot.Replace("'","''")+"'"))
    $global:AirScopeTest.Engine=[ScriptBlock]::Create($rewritten)
    try { & $Body; $script:passed++; Write-Output ('PASS '+$Name) }
    catch { $script:failed++; Write-Output ('FAIL '+$Name+': '+$_.Exception.Message) }
}
function Check([bool]$Condition,[string]$Message='Assertion failed') { if (!$Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Body) { $threw=$false; try { & $Body | Out-Null } catch { $threw=$true }; Check $threw 'Unsafe operation was accepted' }
function Plan([string]$Key='PreferredBand',[string]$Before='2',[string]$After='3') {
    @{AdapterId=$adapterId;Changes=@(@{Keyword=$Key;Name=$Key;Before='old';After='new';BeforeValues=@($Before);AfterValues=@($After)})} | ConvertTo-Json -Depth 7 -Compress
}
function Run([string]$Operation,[string]$Payload) { & $global:AirScopeTest.Engine -Operation $Operation -Payload $Payload }
function Snapshot { Get-Content -LiteralPath ((Get-ChildItem -LiteralPath (Join-Path $global:AirScopeTest.Root 'AirScope') -Filter '*.json' -File | Select-Object -First 1).FullName) -Raw | ConvertFrom-Json }
function IdPayload([string]$Id) { @{Id=$Id} | ConvertTo-Json -Compress }

# Mocks are intentionally named exactly like the Windows commands called by the engine.
function Get-Acl { param($LiteralPath) $obj=[pscustomobject]@{}; $obj | Add-Member -MemberType ScriptMethod -Name GetOwner -Value { param($type) [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544') }; return $obj }
function Set-Acl { param($LiteralPath,$AclObject) }
function Get-NetAdapter { [pscustomobject]@{InterfaceGuid=('{'+([string]$adapterId).Trim('{}').ToUpperInvariant()+'}');Name='WiFi test';InterfaceDescription='MediaTek Wi-Fi 7 MT7925 Wireless LAN Card';Status=$global:AirScopeTest.Status} }
function Get-NetAdapterAdvancedProperty {
    param($Name,$RegistryKeyword)
    $keys=if ($RegistryKeyword) { @($RegistryKeyword) } else { @('PreferredBand','LowPowerEnable') }
    foreach ($key in $keys) { [pscustomobject]@{RegistryKeyword=$key;DisplayName=$key;DisplayValue='test';RegistryValue=@($global:AirScopeTest.Properties[$key]);ValidRegistryValues=@('0','1','2','3');ValidDisplayValues=@('0','1','2','3')} }
}
function Set-NetAdapterAdvancedProperty {
    param($Name,$RegistryKeyword,$RegistryValue,[switch]$NoRestart)
    if ($global:AirScopeTest.WriteFailure -and [string]$RegistryValue[0] -eq '3') { throw 'Injected apply failure' }
    if ($global:AirScopeTest.RestoreFailure -and [string]$RegistryValue[0] -eq '2') { throw 'Injected restore failure' }
    $global:AirScopeTest.Writes++; $global:AirScopeTest.Properties[$RegistryKeyword]=[string]$RegistryValue[0]
}
function Restart-NetAdapter { param($Name,$Confirm) $global:AirScopeTest.Restarts++ }
function Checkpoint-Computer { param($Description,$RestorePointType,$WarningAction,$ErrorAction) if (!$global:AirScopeTest.Checkpoint) { throw 'Injected unavailable restore point' }; $global:AirScopeTest.Description=$Description }
function Get-ComputerRestorePoint { if ($global:AirScopeTest.Description) { [pscustomobject]@{SequenceNumber=123;Description=$global:AirScopeTest.Description} } }
function New-ScheduledTaskAction { param($Execute,$Argument) [pscustomobject]@{Execute=$Execute;Argument=$Argument} }
function New-ScheduledTaskTrigger { param([switch]$Once,$At,[switch]$AtStartup) [pscustomobject]@{At=$At;Startup=[bool]$AtStartup} }
function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) [pscustomobject]@{User=$UserId} }
function New-ScheduledTaskSettingsSet { param([switch]$StartWhenAvailable,[switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,$ExecutionTimeLimit,$RestartCount,$RestartInterval) [pscustomobject]@{Retries=$RestartCount} }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Principal,$Settings,[switch]$Force) if ($global:AirScopeTest.TaskFailure) { throw 'Injected task failure' }; $global:AirScopeTest.Task=$true; $global:AirScopeTest.Action=$Action; $global:AirScopeTest.Principal=$Principal }
function Unregister-ScheduledTask { param($TaskName,$Confirm,$ErrorAction) $global:AirScopeTest.Task=$false }

Scenario 'No restore point means zero settings writes' {
    $global:AirScopeTest.Checkpoint=$false; Expect-Failure { Run apply (Plan) }; Check ($global:AirScopeTest.Writes -eq 0); Check ((Snapshot).State -eq 'Blocked')
}
Scenario 'No rollback task means zero settings writes' {
    $global:AirScopeTest.TaskFailure=$true; Expect-Failure { Run apply (Plan) }; Check ($global:AirScopeTest.Writes -eq 0)
}
Scenario 'Unchanged preconditions enforced before mutation' {
    Expect-Failure { Run apply (Plan -Before '0') }; Check ($global:AirScopeTest.Writes -eq 0)
}
Scenario 'Unknown registry keyword rejected' {
    Expect-Failure { Run apply (Plan -Key 'ArbitraryKey') }; Check ($global:AirScopeTest.Writes -eq 0)
}
Scenario 'Invalid registry value rejected' {
    Expect-Failure { Run apply (Plan -After '999') }; Check ($global:AirScopeTest.Writes -eq 0)
}
Scenario 'Non-numeric payload rejected' {
    Expect-Failure { Run apply (Plan -After 'bad;command') }; Check ($global:AirScopeTest.Writes -eq 0)
}
Scenario 'Successful trial has snapshot, restore point and independent task' {
    Run apply (Plan) | Out-Null; $s=Snapshot; Check ($s.State -eq 'Pending'); Check ($s.RestorePointSequence -eq 123); Check $global:AirScopeTest.Task
    Check ($global:AirScopeTest.Properties.PreferredBand -eq '3'); Check ($global:AirScopeTest.Principal.User -eq 'SYSTEM')
    Check ($global:AirScopeTest.Action.Argument -notmatch 'AirScope.exe')
}
Scenario 'Failed mutation automatically restores original values' {
    $global:AirScopeTest.WriteFailure=$true; Expect-Failure { Run apply (Plan) }; Check ($global:AirScopeTest.Properties.PreferredBand -eq '2'); Check ((Snapshot).State -eq 'RolledBack'); Check (!$global:AirScopeTest.Task)
}
Scenario 'Manual rollback restores and verifies the saved value' {
    Run apply (Plan) | Out-Null; Run rollback (IdPayload (Snapshot).Id) | Out-Null; Check ($global:AirScopeTest.Properties.PreferredBand -eq '2'); Check ((Snapshot).State -eq 'RolledBack')
}
Scenario 'Keeping trial preserves settings and removes rollback task' {
    Run apply (Plan) | Out-Null; Run keep (IdPayload (Snapshot).Id) | Out-Null; Check ((Snapshot).State -eq 'Committed'); Check (!$global:AirScopeTest.Task); Check ($global:AirScopeTest.Properties.PreferredBand -eq '3')
}
Scenario 'Committed snapshots can still be restored manually' {
    Run apply (Plan) | Out-Null; $id=(Snapshot).Id; Run keep (IdPayload $id) | Out-Null; Run rollback (IdPayload $id) | Out-Null; Check ($global:AirScopeTest.Properties.PreferredBand -eq '2')
}
Scenario 'Only one unfinished trial can exist' {
    Run apply (Plan) | Out-Null; $count=$global:AirScopeTest.Writes; Expect-Failure { Run apply (Plan -Before '3' -After '2') }; Check ($global:AirScopeTest.Writes -eq $count)
}
Scenario 'Disconnected trial cannot be kept' {
    Run apply (Plan) | Out-Null; $global:AirScopeTest.Status='Disconnected'; Expect-Failure { Run keep (IdPayload (Snapshot).Id) }; Check $global:AirScopeTest.Task
}
Scenario 'Expired trial cannot be kept' {
    Run apply (Plan) | Out-Null; $s=Snapshot; $s.RollbackAt=[DateTimeOffset]::Now.AddMinutes(-1).ToString('o'); $s | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $global:AirScopeTest.Root ('AirScope\'+$s.Id+'.json'))
    Expect-Failure { Run keep (IdPayload $s.Id) }; Check $global:AirScopeTest.Task
}
Scenario 'Snapshot captures settings without mutation or checkpoint' {
    Run snapshot (Plan) | Out-Null; Check ((Snapshot).State -eq 'Captured'); Check ($global:AirScopeTest.Writes -eq 0); Check (!$global:AirScopeTest.Task)
}
Scenario 'Scheduled code restores after the application is gone' {
    Run apply (Plan) | Out-Null; $id=(Snapshot).Id; & (Join-Path $global:AirScopeTest.Root 'AirScope\Rollback.ps1') -Id $id | Out-Null
    Check ((Snapshot).State -eq 'RolledBack'); Check ($global:AirScopeTest.Properties.PreferredBand -eq '2')
}
Scenario 'Scheduled rollback does not undo a kept trial' {
    Run apply (Plan) | Out-Null; $id=(Snapshot).Id; Run keep (IdPayload $id) | Out-Null
    & (Join-Path $global:AirScopeTest.Root 'AirScope\Rollback.ps1') -Id $id | Out-Null
    Check ((Snapshot).State -eq 'Committed'); Check ($global:AirScopeTest.Properties.PreferredBand -eq '3')
}
Scenario 'Failed recovery records failure and retains retry task' {
    Run apply (Plan) | Out-Null; $global:AirScopeTest.RestoreFailure=$true; Expect-Failure { Run rollback (IdPayload (Snapshot).Id) }
    Check ((Snapshot).State -eq 'RollbackFailed'); Check $global:AirScopeTest.Task
}
Remove-Variable AirScopeTest -Scope Global
Write-Output ("`n{0} passed; {1} failed." -f $script:passed,$script:failed)
if ($script:failed -gt 0) { exit 1 }
