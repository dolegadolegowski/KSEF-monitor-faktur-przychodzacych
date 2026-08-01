@echo off
setlocal

set "PROJECT_ROOT=%~dp0.."
set "PROJECT=%PROJECT_ROOT%\src\KsefMonitor\KsefMonitor.csproj"
set "OUTPUT=%PROJECT_ROOT%\artifacts\win-x64"

dotnet publish "%PROJECT%" ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  --output "%OUTPUT%" ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishTrimmed=false

if errorlevel 1 (
  echo.
  echo Publikacja nie powiodla sie.
  exit /b 1
)

echo.
echo Gotowe: "%OUTPUT%\KSeFMonitor.exe"
endlocal
