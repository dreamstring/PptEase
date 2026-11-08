$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-host.ps1') | Out-Null
$exe = Join-Path $PSScriptRoot 'bin\PptEase.Host.Tests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 "/out:$exe" "/reference:$PSScriptRoot\bin\PptEase.Host.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ProtocolTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed' }
