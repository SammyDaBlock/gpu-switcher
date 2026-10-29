# Builds release\GpuSwitcher-Setup.exe: publishes a self-contained exe (no .NET install needed) and wraps it with Inno Setup.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish -c Release -o publish -p:SelfContained=true -p:EnableCompressionInSingleFile=true -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found. Install it: winget install JRSoftware.InnoSetup" }
& $iscc /Q installer\GpuSwitcher.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-Item release\GpuSwitcher-Setup.exe | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
