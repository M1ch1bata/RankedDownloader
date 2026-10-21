@echo off
setlocal
rem Build osu!MapsDownloader with the built-in .NET Framework compiler.
rem Output: this folder, plus a copy in D:\vibe\artifacts\RankedDownloader\

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found - .NET Framework 4.x is required.
  exit /b 1
)

set "OUT=%~dp0osu!MapsDownloader.exe"
pushd "%~dp0"

"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /win32manifest:app.manifest /out:"%OUT%" ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.dll /r:System.Core.dll ^
  AssemblyInfo.cs Program.cs MainForm.cs Downloader.cs SayobotApi.cs TaskCollector.cs SmokeTest.cs AppStore.cs UpdateChecker.cs UpdateApplier.cs

if errorlevel 1 (
  echo [ERROR] BUILD FAILED
  echo         If the exe is running, close osu!MapsDownloader.exe and run build.cmd again.
  popd
  exit /b 1
)
echo [OK] %OUT%
popd

if not exist "D:\vibe\artifacts\RankedDownloader" mkdir "D:\vibe\artifacts\RankedDownloader" 2>nul
if exist "D:\vibe\artifacts\RankedDownloader" (
  copy /y "%OUT%" "D:\vibe\artifacts\RankedDownloader\" >nul
  echo [OK] D:\vibe\artifacts\RankedDownloader\osu!MapsDownloader.exe
)
endlocal
