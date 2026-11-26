@echo off
rem Publishes dist\DriveGram-Setup-<version>.exe as a GitHub release (public!) so installed copies offer it.
rem Usage: publish-release.cmd <version> <notes.md>
rem  - the release is made at this repo's current commit, which must already be pushed (gh refuses otherwise);
rem  - the notes get the installer's SHA-256 appended (GitHub also records its own; DriveGram checks either).
setlocal
set NOTESIN=%~f2
cd /d "%~dp0"
if "%~2"=="" (echo usage: publish-release.cmd 1.6.0 notes.md & exit /b 1)
set VERSION=%~1
set SETUP=dist\DriveGram-Setup-%VERSION%.exe
if not exist "%SETUP%" (echo %SETUP% not found: build it first & exit /b 1)
if not exist "%NOTESIN%" (echo %NOTESIN% not found & exit /b 1)
for /f %%s in ('git rev-parse HEAD') do set COMMIT=%%s
git diff --quiet HEAD || (echo there are uncommitted changes: commit and push first & exit /b 1)
for /f "skip=1 tokens=1" %%h in ('certutil -hashfile "%SETUP%" SHA256') do if not defined HASH set HASH=%%h
if not defined HASH (echo could not hash %SETUP% & exit /b 1)
set NOTES=%TEMP%\drivegram-release-%VERSION%.md
copy /y "%NOTESIN%" "%NOTES%" >nul
>>"%NOTES%" echo.
>>"%NOTES%" echo SHA256 (DriveGram-Setup-%VERSION%.exe): %HASH%
gh release create v%VERSION% "%SETUP%" --target %COMMIT% --latest --title "DriveGram %VERSION%" --notes-file "%NOTES%" || exit /b 1
echo Published v%VERSION% at %COMMIT% (SHA256 %HASH%)
