@echo off
rem Builds StatsOverlay.exe using the C# compiler included with Windows (.NET Framework 4.x).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /out:StatsOverlay.exe /win32icon:StatsOverlay.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll StatsOverlay.cs
if errorlevel 1 (echo Build failed. & exit /b 1)
echo Built StatsOverlay.exe
