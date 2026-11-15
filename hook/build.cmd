@echo off
rem Builds GpuSwitcherHook.exe with the .NET Framework compiler built into Windows (tiny exe, no runtime to ship)
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize /out:"%~dp0..\publish\GpuSwitcherHook.exe" "%~dp0Hook.cs"
