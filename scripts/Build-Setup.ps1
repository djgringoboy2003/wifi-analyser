param([string]$ArchivePath=(Join-Path $PSScriptRoot '..\artifacts\AirScope-win-x64.zip'),[switch]$Offline)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$archive=[IO.Path]::GetFullPath($ArchivePath)
if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Build the portable archive before packaging setup.' }
$restore=@();if ($Offline) { $restore=@('-p:NuGetAudit=false','-p:RestoreIgnoreFailedSources=true') }
& dotnet publish (Join-Path $root 'src\AirScope.Setup\AirScope.Setup.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ('-p:AirScopePayloadPath='+$archive) -o (Join-Path $root 'artifacts\setup-build') @restore
if ($LASTEXITCODE -ne 0) { throw 'Native setup publish failed.' }
$target=Join-Path $root 'artifacts\AirScope-Setup.exe'
Copy-Item -LiteralPath (Join-Path $root 'artifacts\setup-build\AirScope-Setup.exe') -Destination $target -Force
Write-Output $target
