param([switch]$Publish,[switch]$SkipTests)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $root '.tools'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:NUGET_PACKAGES=Join-Path $root '.packages'
Push-Location $root
try {
    & dotnet build AirScope.slnx -c Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & dotnet run --project tests\AirScope.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\Test-Recovery.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Recovery fault-injection tests failed.' }
    }
    if ($Publish) {
        & dotnet publish src\AirScope.Windows\AirScope.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts\AirScope-win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath README.md,LICENSE -Destination artifacts\AirScope-win-x64
        Compress-Archive -Path artifacts\AirScope-win-x64\* -DestinationPath artifacts\AirScope-win-x64.zip -Force
        Get-FileHash -LiteralPath artifacts\AirScope-win-x64.zip -Algorithm SHA256 | Format-List
    }
} finally { Pop-Location }
