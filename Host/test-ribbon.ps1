$ErrorActionPreference = 'Stop'
# Does not rebuild the host DLL loaded by PowerPoint.
$exe = Join-Path $PSScriptRoot 'bin\PptEase.Ribbon.Tests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /main:RibbonTests "/out:$exe" "/reference:$PSScriptRoot\bin\PptEase.Host.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Xml.Linq.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ProtocolTests.cs') (Join-Path $PSScriptRoot 'RibbonTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Ribbon tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Ribbon tests failed' }
