param([string]$ArtifactsDirectory=(Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ArtifactsDirectory)
$checks=Join-Path $root ('package-check-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $checks
$setup=Join-Path $root 'AirScope-Setup.exe'
$archive=Join-Path $root 'AirScope-win-x64.zip'
$payloadResult=Join-Path $checks 'payload.private.json'
$process=Start-Process -FilePath $setup -ArgumentList '--verify-payload',('"'+$payloadResult+'"') -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0 -or !(Get-Content -LiteralPath $payloadResult -Raw | ConvertFrom-Json).success) { throw 'Setup payload verification failed.' }

function Invoke-Installer([string]$Directory,[bool]$Remove=$false,[bool]$ExpectFailure=$false) {
    $arguments=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Install-AirScope.ps1'),'-PayloadPath',$archive,'-InstallDirectory',$Directory,'-NoRegistration','-NoShortcuts','-NoLaunch')
    if ($Remove) { $arguments+=@('-Uninstall') }
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $info.Arguments=($arguments | ForEach-Object { '"'+$_+'"' }) -join ' '
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $child=[Diagnostics.Process]::Start($info)
    try {
        $output=$child.StandardOutput.ReadToEndAsync();$errors=$child.StandardError.ReadToEndAsync();$child.WaitForExit()
        $message=$output.GetAwaiter().GetResult()+$errors.GetAwaiter().GetResult()
        if (($child.ExitCode -eq 0) -eq $ExpectFailure) { throw ('Unexpected installer result: '+$message) }
    } finally { $child.Dispose() }
}

$target=Join-Path $checks 'installed app'
Invoke-Installer $target
if (!(Test-Path -LiteralPath (Join-Path $target 'AirScope.exe'))) { throw 'Installed application is missing.' }
$sentinel=Join-Path $target 'user-file.txt'
[IO.File]::WriteAllText($sentinel,'preserve me')
Invoke-Installer $target # Updating a marked installation preserves unknown files.
if ([IO.File]::ReadAllText($sentinel) -ne 'preserve me') { throw 'Installation modified an unknown file.' }
Invoke-Installer $target -Remove $true
if ((Test-Path -LiteralPath (Join-Path $target 'AirScope.exe')) -or [IO.File]::ReadAllText($sentinel) -ne 'preserve me') { throw 'Uninstall did not preserve user files.' }
$unmarked=Join-Path $checks 'unmarked'
$null=New-Item -ItemType Directory -Path $unmarked
$unmarkedFile=Join-Path $unmarked 'AirScope.exe'
[IO.File]::WriteAllText($unmarkedFile,'unowned content')
Invoke-Installer $unmarked -ExpectFailure $true
Invoke-Installer $unmarked -Remove $true -ExpectFailure $true
if ([IO.File]::ReadAllText($unmarkedFile) -ne 'unowned content') { throw 'An unmarked directory was modified.' }
# Remove only the exact synthetic files/directories created above. No recursive cleanup.
foreach ($file in @($sentinel,$unmarkedFile,$payloadResult)) { Remove-Item -LiteralPath $file -Force }
[IO.Directory]::Delete($target);[IO.Directory]::Delete($unmarked);[IO.Directory]::Delete($checks)
Write-Output 'PASS setup payload, install, upgrade, uninstall preservation and unmarked-directory refusal.'
