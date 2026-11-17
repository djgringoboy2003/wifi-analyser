$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$standalone=Join-Path $root 'artifacts\AirScope-win-x64\AirScope.exe'
$built=Join-Path $root 'src\AirScope.Windows\bin\Release\net10.0-windows\AirScope.exe'
if (Test-Path -LiteralPath $standalone) { Start-Process -FilePath $standalone -WindowStyle Hidden; exit 0 }
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0, or use the standalone Windows download.'
    exit 1
}
$env:DOTNET_CLI_HOME=Join-Path $root '.tools'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:NUGET_PACKAGES=Join-Path $root '.packages'
& dotnet build (Join-Path $root 'src\AirScope.Windows\AirScope.Windows.csproj') -c Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Start-Process -FilePath $built -WindowStyle Hidden
