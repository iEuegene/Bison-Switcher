@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
where dotnet >NUL 2>&1 || (echo Потрібен .NET 8 SDK: winget install Microsoft.DotNet.SDK.8 & pause & exit /b 1)
echo Тести...
dotnet test Tests\Kright.Tests.csproj -v q --nologo || (echo Тести впали — збірку зупинено. & pause & exit /b 1)
echo Збірка...
dotnet publish Kright.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-ua --nologo -v q || (echo Збірка впала, див. повідомлення вище. & pause & exit /b 1)
echo.
echo Готово: %~dp0publish-ua\Kright.exe
pause
