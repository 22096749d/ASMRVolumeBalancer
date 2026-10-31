@echo off
setlocal
cd /d "%~dp0"
set "ASMR_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%ASMR_CSC%" set "ASMR_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%ASMR_CSC%" (
  echo .NET Framework C# compiler was not found.
  pause
  exit /b 1
)
"%ASMR_CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:..\app.ico /win32manifest:app.manifest /out:..\ASMRVolumeBalancer.exe /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Program.cs AudioEngine.cs PreviewPlayer.cs
if errorlevel 1 (
  pause
  exit /b 1
)
echo Build complete.
pause
