@echo off
rem Read-only hardware report for a compatibility issue. Nothing is written to the laptop.
rem Output: probe\diag_report.txt  (attach it to the GitHub issue)
setlocal
cd /d "%~dp0"
if not exist Probe.exe call build.bat || exit /b 1
set OUT=diag_report.txt
echo ControlHub diagnostic report > %OUT%
echo ==== system >> %OUT%
powershell -NoProfile -Command "$c=Get-CimInstance Win32_ComputerSystem; $b=Get-CimInstance Win32_BIOS; $p=Get-CimInstance Win32_BaseBoard; 'Manufacturer: '+$c.Manufacturer; 'Model: '+$c.Model; 'SystemFamily: '+$c.SystemFamily; 'Board: '+$p.Manufacturer+' '+$p.Product; 'BIOS: '+$b.SMBIOSBIOSVersion+' ('+$b.ReleaseDate+')'; 'OS: '+(Get-CimInstance Win32_OperatingSystem).Caption+' '+[Environment]::OSVersion.Version; 'CPU: '+(Get-CimInstance Win32_Processor).Name; 'GPU: '+((Get-CimInstance Win32_VideoController).Name -join ', '); $k=Get-AppxPackage CLEVOCO.FnhotkeysandOSD; 'FnKey app: '+$(if($k){$k.Version}else{'NOT INSTALLED'}); $s=Get-Process CC40 -ErrorAction SilentlyContinue; 'CC40 running: '+[bool]$s; Test-Path 'C:\ComboKey\profile_Fan.xml' | %% { 'profile_Fan.xml present: '+$_ }" >> %OUT%
for %%f in (Probe FanTable Charge Slots Pref Wmi18) do (
  echo.>> %OUT%
  echo ==== %%f>> %OUT%
  "%~dp0%%f.exe" "%TEMP%\controlhub_probe.txt" >> %OUT% 2>&1
)
del "%TEMP%\controlhub_probe.txt" 2>nul
echo.
echo Report written to %~dp0%OUT%
