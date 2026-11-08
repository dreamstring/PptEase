param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Host\build-host.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Host build failed' }
dotnet publish (Join-Path $PSScriptRoot 'App\PptEase.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Manager publish failed' }
Write-Output "Published: $OutputDirectory"
