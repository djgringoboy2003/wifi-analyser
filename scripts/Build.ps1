param([switch]$Publish,[switch]$SkipTests,[switch]$Offline,[switch]$NetworkTests,[switch]$Setup)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $root '.tools'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:NUGET_PACKAGES=Join-Path $root '.packages'
Push-Location $root
try {
    $restore=@();if ($Offline) { $restore=@('-p:NuGetAudit=false','-p:RestoreIgnoreFailedSources=true') }
    & dotnet build AirScope.slnx -c Release --verbosity minimal @restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        $checks=@();if ($NetworkTests) { $checks=@('--','--network') }
        & dotnet run --project tests\AirScope.Tests -c Release --no-build @checks
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\Test-Recovery.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Recovery fault-injection tests failed.' }
    }
    if ($Publish) {
        & dotnet publish src\AirScope.Windows\AirScope.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts\AirScope-win-x64 @restore
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\Sign-Artifact.ps1 -Path artifacts\AirScope-win-x64\AirScope.exe
        if ($LASTEXITCODE -ne 0) { throw 'Signing check failed.' }
        Copy-Item -LiteralPath README.md,LICENSE -Destination artifacts\AirScope-win-x64
        Copy-Item -LiteralPath scripts\Install-AirScope.ps1 -Destination artifacts\AirScope-win-x64
        Compress-Archive -Path artifacts\AirScope-win-x64\* -DestinationPath artifacts\AirScope-win-x64.zip -Force
        $hash=(Get-FileHash -LiteralPath artifacts\AirScope-win-x64.zip -Algorithm SHA256).Hash
        [xml]$properties=Get-Content -LiteralPath Directory.Build.props
        $version=[string]$properties.Project.PropertyGroup.Version
        $manifest=[ordered]@{Version=$version;Url=('https://github.com/djgringoboy2003/wifi-analyser/releases/download/v'+$version+'/AirScope-win-x64.zip');Sha256=$hash;FileName='AirScope-win-x64.zip';PublisherThumbprint=$env:AIRSCOPE_SIGNING_THUMBPRINT;Notes='See the release notes for features, validation and signing/recovery limitations.'}
        [IO.File]::WriteAllText((Join-Path $root 'artifacts\AirScope-update.json'),($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        $sums=@($hash.ToLowerInvariant()+'  AirScope-win-x64.zip')
        if ($Setup) {
            $setupArguments=@();if ($Offline) { $setupArguments=@('-Offline') }
            & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\Build-Setup.ps1 @setupArguments
            if ($LASTEXITCODE -ne 0) { throw 'Setup packaging failed.' }
            & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\Sign-Artifact.ps1 -Path artifacts\AirScope-Setup.exe
            if ($LASTEXITCODE -ne 0) { throw 'Setup signing failed.' }
            $sums+=@((Get-FileHash artifacts\AirScope-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()+'  AirScope-Setup.exe')
        }
        $sums | Set-Content -LiteralPath artifacts\SHA256SUMS.txt -Encoding ASCII
        Write-Output ('Portable SHA-256: '+$hash)
    }
} finally { Pop-Location }
