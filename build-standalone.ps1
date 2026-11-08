param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$payload = Join-Path $OutputDirectory 'payload'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $payload
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') $payload
$zip = Join-Path $OutputDirectory 'payload.zip'
Compress-Archive -Path "$payload/*" -DestinationPath $zip -Force
dotnet publish (Join-Path $PSScriptRoot 'Launcher/PptEase.Launcher.csproj') -c Release -r win-x64 -p:PayloadPath=$zip -o (Join-Path $OutputDirectory 'single-exe')
if ($LASTEXITCODE -ne 0) { throw 'Standalone build failed' }
