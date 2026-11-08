$ErrorActionPreference = 'Stop'
# Does not rebuild the host DLL loaded by PowerPoint; tests use isolated fake objects.
$exe = Join-Path $PSScriptRoot 'bin\PptEase.UnlockChooser.Tests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /main:UnlockChooserTests "/out:$exe" "/reference:$PSScriptRoot\bin\PptEase.Host.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ProtocolTests.cs') (Join-Path $PSScriptRoot 'UnlockChooserTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Unlock chooser tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Unlock chooser tests failed' }
