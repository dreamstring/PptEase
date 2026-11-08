$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-host.ps1')
$exe = Join-Path $PSScriptRoot 'bin/LocalizationTests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 "/out:$exe" "/reference:$PSScriptRoot/bin/PptEase.Host.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Xml.Linq.dll (Join-Path $PSScriptRoot 'LocalizationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Localization tests did not compile' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Localization tests failed' }
