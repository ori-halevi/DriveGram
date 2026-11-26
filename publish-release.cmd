@echo off
rem Publishes dist\DriveGram-Setup-<version>.exe as a GitHub release (public!) so installed copies offer it.
rem Usage: publish-release.cmd <version> <notes.md>
rem The notes get the installer's SHA-256 appended: DriveGram checks the download against it.
setlocal
cd /d "%~dp0"
if "%~2"=="" (echo usage: publish-release.cmd 1.6.0 notes.md & exit /b 1)
set VERSION=%~1
set SETUP=dist\DriveGram-Setup-%VERSION%.exe
if not exist "%SETUP%" (echo %SETUP% not found: build it first & exit /b 1)
if not exist "%~2" (echo %~2 not found & exit /b 1)
for /f "skip=1 tokens=1" %%h in ('certutil -hashfile "%SETUP%" SHA256') do if not defined HASH set HASH=%%h
if not defined HASH (echo could not hash %SETUP% & exit /b 1)
set NOTES=%TEMP%\drivegram-release-%VERSION%.md
copy /y "%~2" "%NOTES%" >nul
>>"%NOTES%" echo.
>>"%NOTES%" echo SHA256 (DriveGram-Setup-%VERSION%.exe): %HASH%
gh release create v%VERSION% "%SETUP%" --title "DriveGram %VERSION%" --notes-file "%NOTES%" || exit /b 1
echo Published v%VERSION% (SHA256 %HASH%)
