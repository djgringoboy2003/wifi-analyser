param([Parameter(Mandatory=$true)][string]$Path,[string]$CertificateThumbprint=$env:AIRSCOPE_SIGNING_THUMBPRINT,[string]$TimestampUrl=$env:AIRSCOPE_TIMESTAMP_URL,[switch]$Required)
$ErrorActionPreference='Stop'
if (!$CertificateThumbprint) { if ($Required) { throw 'A signing certificate is required but not configured.' }; Write-Output 'Unsigned build: publisher-dependent automation and update application remain gated.'; exit 0 }
if ($CertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'Set AIRSCOPE_SIGNING_THUMBPRINT to the Windows certificate thumbprint (40 hex characters).' }
$cert=Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $CertificateThumbprint -and $_.HasPrivateKey } | Select-Object -First 1
if (!$cert -or $cert.NotAfter -le (Get-Date)) { throw 'The configured signing certificate/private key is unavailable or expired.' }
$kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$tool=Get-ChildItem -Path (Join-Path $kit '*\x64\signtool.exe') -File | Sort-Object FullName -Descending | Select-Object -First 1
if (!$tool) { throw 'Install the Windows SDK signing tools before signing.' }
$arguments=@('sign','/fd','SHA256','/sha1',$CertificateThumbprint)
if ($TimestampUrl) { if ($TimestampUrl -notmatch '^https?://') { throw 'Invalid timestamp URL.' };$arguments+=@('/tr',$TimestampUrl,'/td','SHA256') }
$arguments+=@([IO.Path]::GetFullPath($Path))
& $tool.FullName @arguments
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
$signature=Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw 'The resulting signature is not trusted and valid.' }
