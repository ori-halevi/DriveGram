@echo off
rem Publishes dist\DriveGram-Setup-<version>.exe as a GitHub release (public!) so installed copies offer it.
rem Usage: publish-release.cmd <version> <notes.md>
rem Refuses unless: the installer was built from the current commit with nothing uncommitted (the .commit file
rem build-installer.cmd writes), nothing is uncommitted now, and that commit is already on GitHub.
rem The notes get the installer's SHA-256 appended (GitHub also records its own; DriveGram checks either).
setlocal
set NOTESIN=%~f2
cd /d "%~dp0"
if "%~2"=="" (echo usage: publish-release.cmd 1.6.0 notes.md & exit /b 1)
set VERSION=%~1
set SETUP=dist\DriveGram-Setup-%VERSION%.exe
if not exist "%SETUP%" (echo %SETUP% not found: build it first & exit /b 1)
if not exist "%NOTESIN%" (echo %NOTESIN% not found & exit /b 1)

git status --porcelain | findstr . >nul && (echo there are uncommitted or untracked files: commit them, build again, push & exit /b 1)
for /f %%s in ('git rev-parse HEAD') do set COMMIT=%%s
set BUILT=
if exist "dist\DriveGram-Setup-%VERSION%.commit" set /p BUILT=<"dist\DriveGram-Setup-%VERSION%.commit"
if not "%BUILT%"=="%COMMIT%" (echo the installer was not built from the current commit %COMMIT% [built: %BUILT%]: build again & exit /b 1)
git fetch -q origin || (echo could not reach GitHub & exit /b 1)
git merge-base --is-ancestor %COMMIT% origin/main || (echo commit %COMMIT% is not on GitHub yet: push first & exit /b 1)

set HASH=
for /f "skip=1 tokens=1" %%h in ('certutil -hashfile "%SETUP%" SHA256') do if not defined HASH set HASH=%%h
echo %HASH%| findstr /r /x "[0-9a-fA-F][0-9a-fA-F]*" >nul || (echo could not hash %SETUP% & exit /b 1)
if not "%HASH:~63,1%"=="" if "%HASH:~64,1%"=="" goto hashed
echo could not hash %SETUP% (got "%HASH%") & exit /b 1
:hashed
set NOTES=%TEMP%\drivegram-release-%VERSION%.md
copy /y "%NOTESIN%" "%NOTES%" >nul
>>"%NOTES%" echo.
>>"%NOTES%" echo SHA256 (DriveGram-Setup-%VERSION%.exe): %HASH%
gh release create v%VERSION% "%SETUP%" --target %COMMIT% --latest --title "DriveGram %VERSION%" --notes-file "%NOTES%" || exit /b 1
echo Published v%VERSION% at %COMMIT% (SHA256 %HASH%)
