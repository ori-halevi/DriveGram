@echo off
rem Builds dist\DriveGram-Setup-<version>.exe : self-contained app + WinFsp, Hebrew installer.
setlocal
cd /d "%~dp0"
set VERSION=1.2.0
set WINFSP=winfsp-2.1.25156.msi
set WINFSP_SHA256=073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a
set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" (echo Inno Setup 6 not found: winget install JRSoftware.InnoSetup & exit /b 1)

if exist publish rmdir /s /q publish
echo === publishing (self-contained, win-x64) ===
dotnet publish src\DriveGram.Tray -c Release -r win-x64 --self-contained -p:Version=%VERSION% -o publish -v q || exit /b 1
dotnet publish src\DriveGram -c Release -r win-x64 --self-contained -p:Version=%VERSION% -o publish -v q || exit /b 1
copy /y assets\*.ico publish\ >nul

if not exist installer\redist\%WINFSP% (
  echo === downloading %WINFSP% ===
  if not exist installer\redist mkdir installer\redist
  curl -L --fail -o installer\redist\%WINFSP% https://github.com/winfsp/winfsp/releases/download/v2.1/%WINFSP% || exit /b 1
)
certutil -hashfile installer\redist\%WINFSP% SHA256 | findstr /i /c:"%WINFSP_SHA256%" >nul || (echo WinFsp installer hash mismatch! & exit /b 1)

echo === compiling installer ===
"%ISCC%" /Q /DAppVersion=%VERSION% installer\DriveGram.iss || exit /b 1
echo.
echo Done: dist\DriveGram-Setup-%VERSION%.exe
