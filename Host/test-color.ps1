$ErrorActionPreference = 'Stop'
# Deliberately does not rebuild or replace the host DLL currently loaded by Office.
$exe = Join-Path $PSScriptRoot 'bin\PptEase.Color.Tests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /main:ColorProtocolTests "/out:$exe" "/reference:$PSScriptRoot\bin\PptEase.Host.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ProtocolTests.cs') (Join-Path $PSScriptRoot 'ColorProtocolTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Color protocol tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Color protocol tests failed' }
