@echo off
setlocal
cd /d "%~dp0"
where dotnet >NUL 2>&1
if errorlevel 1 (
  echo .NET 8 SDK not found. Install it in PowerShell:
  echo     winget install Microsoft.DotNet.SDK.8
  echo then close this window and run build-ua.cmd again.
  pause
  exit /b 1
)
echo Running tests...
dotnet test Tests\Kright.Tests.csproj -v q --nologo
if errorlevel 1 (
  echo Tests failed - build stopped. Send a screenshot of the messages above.
  pause
  exit /b 1
)
echo Building...
dotnet publish Kright.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-ua --nologo -v q
if errorlevel 1 (
  echo Build failed. Send a screenshot of the messages above.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0publish-ua\Kright.exe
pause
