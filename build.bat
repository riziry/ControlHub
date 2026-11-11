@echo off
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /out:ControlHub.exe ^
 /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
 /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
 /r:System.Xml.dll /r:System.Management.dll ^
 App.cs Window.cs Pages.cs Fans.cs Battery.cs BatteryHistory.cs Effects.cs Prefs.cs Display.cs Activity.cs Core.cs Hardware.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo OK: ControlHub.exe
