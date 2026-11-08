$ErrorActionPreference = 'Stop'
# Does not rebuild or register the host. Calls only an isolated CCW in this test process.
$exe = Join-Path $PSScriptRoot 'bin\PptEase.ComAbi.Tests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 "/out:$exe" "/reference:$PSScriptRoot\bin\PptEase.Host.dll" /reference:System.dll /reference:System.Core.dll (Join-Path $PSScriptRoot 'ComAbiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'COM ABI tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'COM ABI tests failed' }
