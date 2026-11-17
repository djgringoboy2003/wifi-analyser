param(
    [string]$PayloadPath=(Join-Path $PSScriptRoot 'AirScope-win-x64.zip'),
    [string]$InstallDirectory=(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\AirScope'),
    [switch]$Uninstall,[switch]$NoLaunch,[switch]$NoShortcuts,[switch]$NoRegistration,[switch]$DesktopShortcut
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
try {
$target=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($target) -or $target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')) { throw 'Choose an application directory, not a drive root.' }
$check=$target
while ($check) {
    if ((Test-Path -LiteralPath $check) -and ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation paths cannot contain directory links.' }
    $parent=[IO.Path]::GetDirectoryName($check); if ($parent -eq $check) { break }; $check=$parent
}
$executable=Join-Path $target 'AirScope.exe'
$marker=Join-Path $target '.airscope-install.json'
$owned=@('AirScope.exe','AirScope.exe.previous','AirScope.exe.new','README.md','LICENSE','Install-AirScope.ps1','Uninstall-AirScope.cmd','.airscope-install.json')
foreach ($name in $owned) { $file=Join-Path $target $name; if ((Test-Path -LiteralPath $file) -and ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Application files cannot be links.' } }
foreach ($name in @('AirScope.exe.new','README.md.new','LICENSE.new')) { $file=Join-Path $target $name; if ((Test-Path -LiteralPath $file) -and ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Staged application files cannot be links.' } }
if (Test-Path -LiteralPath $marker) {
    $installation=Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($installation.Product -ne 'AirScope.per-user.v1' -or $installation.Directory -ne $target) { throw 'The installation marker does not match this directory.' }
} elseif ($Uninstall) { throw 'This folder is not a recognised AirScope installation. No files were removed.' }
elseif ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) { throw 'Choose an empty folder or an existing AirScope installation.' }
foreach ($process in @(Get-Process -Name AirScope -ErrorAction SilentlyContinue)) {
    try { if ($process.Path -eq $executable) { throw 'Close the installed AirScope window/tray monitor before continuing.' } }
    catch [ComponentModel.Win32Exception] { }
}
$recovery=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AirScope'
if (Test-Path -LiteralPath $recovery) {
    foreach ($file in Get-ChildItem -LiteralPath $recovery -Filter '*.json' -File) {
        try { $snapshot=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json } catch { continue }
        if ($snapshot.State -in @('Pending','Applying','RollbackFailed')) { throw 'Resolve pending recovery trials in AirScope before installation or removal.' }
    }
}
$registration='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AirScope'
$menu=Join-Path ([Environment]::GetFolderPath('Programs')) 'AirScope.lnk'
$desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) 'AirScope.lnk'
if ($Uninstall) {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (Get-ScheduledTask -TaskName ('AirScope-Protected-'+$sid) -ErrorAction SilentlyContinue) { throw 'Remove the protected worker in AirScope Automation before uninstalling.' }
    if (!$NoRegistration -and (Test-Path -LiteralPath $registration)) {
        $registered=Get-ItemProperty -LiteralPath $registration
        if ($registered.InstallLocation -eq $target) { Remove-Item -LiteralPath $registration -Force }
    }
    if (!$NoShortcuts) {
        $shell=New-Object -ComObject WScript.Shell
        foreach ($link in @($menu,$desktop)) { if (Test-Path -LiteralPath $link) { $shortcut=$shell.CreateShortcut($link); if ($shortcut.TargetPath -eq $executable) { Remove-Item -LiteralPath $link -Force } } }
    }
    if (!$NoRegistration) {
        $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $value=(Get-ItemProperty -LiteralPath $run -Name AirScope -ErrorAction SilentlyContinue).AirScope
        if ($value -eq ('"'+$executable+'" --background')) { Remove-ItemProperty -LiteralPath $run -Name AirScope }
    }
    # Known files only: never recursively delete an installation, user history or recovery storage.
    foreach ($name in $owned) {
        $path=Join-Path $target $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) { [IO.Directory]::Delete($target) }
    Write-Output 'AirScope removed. Local history, settings and protected recovery snapshots were preserved.'
    exit 0
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PayloadPath))
try {
    $app=@($zip.Entries | Where-Object { $_.FullName -eq 'AirScope.exe' })
    if ($app.Count -ne 1 -or $app[0].Length -gt 500000000) { throw 'The package does not contain one supported AirScope executable.' }
    $null=New-Item -ItemType Directory -Path $target -Force
    [pscustomobject]@{Product='AirScope.per-user.v1';Directory=$target} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
    foreach ($name in @('AirScope.exe','README.md','LICENSE')) {
        $entry=$zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
        if ($entry) {
            $path=Join-Path $target $name
            if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid application file path.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,($path+'.new'),$true)
            Move-Item -LiteralPath ($path+'.new') -Destination $path -Force
        }
    }
} finally { $zip.Dispose() }
$installer=Join-Path $target 'Install-AirScope.ps1'
if ([IO.Path]::GetFullPath($PSCommandPath) -ne $installer) { Copy-Item -LiteralPath $PSCommandPath -Destination $installer -Force }
$uninstallText='@echo off'+[Environment]::NewLine+'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-AirScope.ps1" -InstallDirectory "%~dp0." -Uninstall'+[Environment]::NewLine+'if errorlevel 1 pause'
[IO.File]::WriteAllText((Join-Path $target 'Uninstall-AirScope.cmd'),$uninstallText,[Text.Encoding]::ASCII)
if (!$NoShortcuts) {
    $shell=New-Object -ComObject WScript.Shell
    $links=@($menu); if ($DesktopShortcut) { $links+=@($desktop) }
    foreach ($link in $links) { $shortcut=$shell.CreateShortcut($link);$shortcut.TargetPath=$executable;$shortcut.WorkingDirectory=$target;$shortcut.Description='AirScope Wi-Fi intelligence';$shortcut.Save() }
}
if (!$NoRegistration) {
    $null=New-Item -Path $registration -Force
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
    New-ItemProperty -LiteralPath $registration -Name DisplayName -Value 'AirScope' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $version -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $registration -Name Publisher -Value 'djgringoboy2003' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $registration -Name InstallLocation -Value $target -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $registration -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "'+$installer+'" -InstallDirectory "'+$target+'" -Uninstall') -PropertyType String -Force | Out-Null
}
Write-Output ('Installed AirScope in '+$target)
if (!$NoLaunch) { Start-Process -FilePath $executable -WindowStyle Hidden }
} catch {
    if (!$NoLaunch) { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show($_.Exception.Message,'AirScope setup') | Out-Null }
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
