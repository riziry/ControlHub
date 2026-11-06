@echo off
rem Builds the read-only diagnostic probes. They load ..\lib\InsydeDCHU.dll (run setup-lib.ps1 first).
rem LedTiming.cs is NOT built here: it writes keyboard brightness (timing test for developers).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
for %%f in (Probe FanTable Charge Slots Pref Wmi18 Timing) do (
  "%FW%\csc.exe" /nologo /platform:x64 /out:%%f.exe %%f.cs || (echo BUILD FAILED: %%f & exit /b 1)
)
echo OK: probes built
