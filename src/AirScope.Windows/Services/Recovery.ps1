param([ValidateSet('apply','snapshot','rollback','keep')][string]$Operation, [string]$Payload)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'Stop'
$allowed = @('PreferredBand','BandSelection','BWSelection24G','BWSelection5G','BWSelection6G','CurrPhyMode','LowPowerEnable')
$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AirScope'
$adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$usersSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
if (Test-Path -LiteralPath $root) {
    $item = Get-Item -LiteralPath $root -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery storage cannot be a link.' }
    $owner = (Get-Acl -LiteralPath $root).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @($adminSid.Value,$systemSid.Value)) { throw 'Recovery storage has an unexpected owner. No changes were made.' }
} else { $null = New-Item -ItemType Directory -Path $root }
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($adminSid)
$acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @($adminSid,$systemSid)) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($usersSid,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
Set-Acl -LiteralPath $root -AclObject $acl

function Save-Snapshot($snapshot,$path) {
    $snapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($path+'.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($path+'.tmp') -Destination $path -Force
}

# Self-contained function copied into a protected file for Task Scheduler.
# The task runs only this fixed rollback code, never an executable in a user-writable folder.
function Restore-AirScope {
    param([string]$Id,[switch]$Scheduled)
    $ErrorActionPreference='Stop'
    $null=[guid]::ParseExact($Id,'D')
    $recoveryRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AirScope'
    $lock=[IO.File]::Open((Join-Path $recoveryRoot 'transaction.lock'),'OpenOrCreate','ReadWrite','None')
    try {
        $path=Join-Path $recoveryRoot ($Id+'.json')
        $item=Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid snapshot path.' }
        $snapshot=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($snapshot.Version -ne 1 -or $snapshot.Id -ne $Id) { throw 'Invalid snapshot.' }
        if ($Scheduled -and $snapshot.State -notin @('Pending','Applying','RollbackFailed')) { return }
        $adapterId=[guid]::Parse($snapshot.AdapterId)
        $adapter=Get-NetAdapter | Where-Object { [guid]$_.InterfaceGuid -eq $adapterId } | Select-Object -First 1
        if (!$adapter -or $adapter.InterfaceDescription -ne $snapshot.AdapterDescription) { throw 'The original adapter is unavailable.' }
        $allow=@('PreferredBand','BandSelection','BWSelection24G','BWSelection5G','BWSelection6G','CurrPhyMode','LowPowerEnable')
        if (@($snapshot.Changes).Count -lt 1 -or @($snapshot.Changes).Count -gt 7) { throw 'Invalid snapshot settings.' }
        foreach ($change in $snapshot.Changes) {
            if ($change.Keyword -notin $allow -or @($change.BeforeValues).Count -ne 1 -or [string]$change.BeforeValues[0] -notmatch '^\d+$') { throw 'Unsupported recovery setting.' }
            $property=Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $change.Keyword
            if ([string]$change.BeforeValues[0] -notin @($property.ValidRegistryValues | ForEach-Object { [string]$_ })) { throw 'Original value is no longer supported by this driver.' }
        }
        try {
            foreach ($change in $snapshot.Changes) {
                Set-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $change.Keyword -RegistryValue @($change.BeforeValues) -NoRestart
            }
            Restart-NetAdapter -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -Confirm:$false
            foreach ($change in $snapshot.Changes) {
                $property=Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $change.Keyword
                if ([string]$property.RegistryValue[0] -ne [string]$change.BeforeValues[0]) { throw 'A restored driver value could not be verified.' }
            }
            $snapshot.State='RolledBack'
        } catch {
            $snapshot.State='RollbackFailed'
            $snapshot.Description='Recovery needs attention: '+$_.Exception.Message
            $snapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($path+'.tmp') -Encoding UTF8
            Move-Item -LiteralPath ($path+'.tmp') -Destination $path -Force
            throw
        }
        $snapshot.Description='Original adapter settings restored and verified.'
        $snapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($path+'.tmp') -Encoding UTF8
        Move-Item -LiteralPath ($path+'.tmp') -Destination $path -Force
        Unregister-ScheduledTask -TaskName ('AirScope-Revert-'+$Id) -Confirm:$false -ErrorAction SilentlyContinue
        [pscustomobject]@{Id=$Id;State=$snapshot.State;Message=$snapshot.Description} | ConvertTo-Json -Compress
    } finally { $lock.Dispose() }
}

$request = $Payload | ConvertFrom-Json
if ($Operation -eq 'rollback') { Restore-AirScope -Id ([string]$request.Id); return }
$transaction=[IO.File]::Open((Join-Path $root 'transaction.lock'),'OpenOrCreate','ReadWrite','None')
try {
    if ($Operation -eq 'keep') {
        $id=[guid]::ParseExact([string]$request.Id,'D').ToString('D')
        $path=Join-Path $root ($id+'.json')
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid snapshot path.' }
        $s=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($s.State -ne 'Pending' -or [DateTimeOffset]::Now -ge [DateTimeOffset]::Parse($s.RollbackAt)) { throw 'This trial has expired or already finished. Refresh Recovery.' }
        $adapter=Get-NetAdapter | Where-Object { [guid]$_.InterfaceGuid -eq [guid]$s.AdapterId } | Select-Object -First 1
        if (!$adapter -or $adapter.InterfaceDescription -ne $s.AdapterDescription) { throw 'The original adapter is unavailable.' }
        if ($adapter.Status -ne 'Up') { throw 'Reconnect this adapter before keeping the trial.' }
        foreach ($c in $s.Changes) {
            if ($c.Keyword -notin $allowed) { throw 'Invalid setting.' }
            $property=Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $c.Keyword
            if ([string]$property.RegistryValue[0] -ne [string]$c.AfterValues[0]) { throw 'The trial settings have changed; restore instead.' }
        }
        $s.State='Committed'; $s.Description='Settings kept. Manual restore remains available.'
        Save-Snapshot $s $path
        Unregister-ScheduledTask -TaskName ('AirScope-Revert-'+$id) -Confirm:$false -ErrorAction SilentlyContinue
        [pscustomobject]@{Id=$id;State=$s.State;Message=$s.Description} | ConvertTo-Json -Compress
        return
    }
    # One active trial at a time. Expired or failed trials must be restored first.
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.json' -File) {
        $existing=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($existing.State -in @('Pending','Applying','RollbackFailed')) { throw 'An unfinished trial exists. Keep or restore it in Recovery first.' }
    }
    $adapterId=[guid]::Parse($request.AdapterId)
    $adapter=Get-NetAdapter | Where-Object { [guid]$_.InterfaceGuid -eq $adapterId } | Select-Object -First 1
    if (!$adapter -or $adapter.InterfaceDescription -notmatch 'MT7925') { throw 'Automatic tuning requires the selected MediaTek MT7925 adapter.' }
    $changes=@($request.Changes)
    if ($Operation -eq 'snapshot') {
        $changes=@(Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) | Where-Object { $_.RegistryKeyword -in $allowed } | ForEach-Object {
            [pscustomobject]@{Keyword=$_.RegistryKeyword;Name=$_.DisplayName;Before=$_.DisplayValue;After=$_.DisplayValue;
            BeforeValues=@($_.RegistryValue | ForEach-Object { [string]$_ });AfterValues=@($_.RegistryValue | ForEach-Object { [string]$_ })}
        })
    }
    if ($changes.Count -lt 1 -or $changes.Count -gt 7) { throw 'No valid settings were supplied.' }
    if (@($changes.Keyword | Sort-Object -Unique).Count -ne $changes.Count) { throw 'Duplicate settings.' }
    foreach ($c in $changes) {
        if ($c.Keyword -notin $allowed -or @($c.AfterValues).Count -ne 1 -or @($c.BeforeValues).Count -ne 1 -or [string]$c.AfterValues[0] -notmatch '^\d+$') { throw 'Unsupported setting or value.' }
        $property=Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $c.Keyword
        if ([string]$property.RegistryValue[0] -ne [string]$c.BeforeValues[0]) { throw 'Adapter settings changed since analysis. Generate a fresh plan.' }
        if ([string]$c.AfterValues[0] -notin @($property.ValidRegistryValues | ForEach-Object { [string]$_ })) { throw 'The installed driver does not support this value.' }
    }
    $id=[guid]::NewGuid().ToString('D'); $path=Join-Path $root ($id+'.json')
    $s=[pscustomobject]@{Version=1;Id=$id;CreatedAt=[DateTimeOffset]::Now.ToString('o');Adapter=$adapter.Name;
        AdapterId=$adapterId.ToString('D');AdapterDescription=$adapter.InterfaceDescription;State='Captured';
        Description='Adapter snapshot captured. No changes applied.';RollbackAt=$null;RestorePointSequence=$null;Changes=$changes}
    Save-Snapshot $s $path
    if ($Operation -eq 'snapshot') {
        [pscustomobject]@{Id=$id;State=$s.State;Message=$s.Description} | ConvertTo-Json -Compress
        return
    }
    # A new, verified restore point is mandatory. Do not alter Windows frequency policy.
    $description='AirScope '+$id
    try {
        Checkpoint-Computer -Description $description -RestorePointType MODIFY_SETTINGS -WarningAction Stop -ErrorAction Stop
        $point=Get-ComputerRestorePoint | Where-Object { $_.Description -eq $description } | Select-Object -Last 1
        if (!$point) { throw 'Windows did not create the requested restore point (System Protection or the 24-hour limit may apply).' }
        $s.RestorePointSequence=$point.SequenceNumber
    } catch {
        $s.State='Blocked'; $s.Description='No tuning applied: '+$_.Exception.Message
        Save-Snapshot $s $path
        throw $s.Description
    }
    # Fail before writing settings unless an independent scheduled rollback is installed.
    $rollbackCode='param([string]$Id)' + [Environment]::NewLine + 'function Restore-AirScope {' + (Get-Command Restore-AirScope).Definition + '}' + [Environment]::NewLine + 'Restore-AirScope -Id $Id -Scheduled'
    $rollbackPath=Join-Path $root 'Rollback.ps1'
    if ((Test-Path -LiteralPath $rollbackPath) -and ((Get-Item -LiteralPath $rollbackPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid rollback script path.' }
    $rollbackCode | Set-Content -LiteralPath $rollbackPath -Encoding UTF8
    $deadline=[DateTimeOffset]::Now.AddMinutes(3)
    $s.RollbackAt=$deadline.ToString('o'); $s.State='Applying'; $s.Description='Trial in progress; original settings will be restored unless kept.'
    Save-Snapshot $s $path
    $powerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action=New-ScheduledTaskAction -Execute $powerShell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$rollbackPath+'" -Id '+$id)
    $triggers=@((New-ScheduledTaskTrigger -Once -At $deadline.LocalDateTime),(New-ScheduledTaskTrigger -AtStartup))
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    try {
        $null=Register-ScheduledTask -TaskName ('AirScope-Revert-'+$id) -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force
    } catch { $s.State='Blocked';$s.Description='No tuning applied: rollback task could not be installed.';Save-Snapshot $s $path;throw }
    try {
        foreach ($c in $changes) { Set-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $c.Keyword -RegistryValue @($c.AfterValues) -NoRestart }
        Restart-NetAdapter -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -Confirm:$false
        foreach ($c in $changes) {
            $property=Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) -RegistryKeyword $c.Keyword
            if ([string]$property.RegistryValue[0] -ne [string]$c.AfterValues[0]) { throw 'A driver setting could not be verified.' }
        }
        $s.State='Pending'; Save-Snapshot $s $path
    } catch {
        $transaction.Dispose(); $transaction=$null
        $failure=$_.Exception.Message
        $null=Restore-AirScope -Id $id
        throw ('Tuning failed; original settings restored. '+$failure)
    }
    [pscustomobject]@{Id=$id;State=$s.State;Message='Trial applied and verified. Keep within three minutes, or Windows restores the snapshot automatically.'} | ConvertTo-Json -Compress
} finally { if ($transaction) { $transaction.Dispose() } }
