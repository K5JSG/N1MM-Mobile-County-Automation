@echo off
rem Starts N1MM+ (if it is not already running) and N1MM Mobile County Automation, then returns.
rem The program closes itself when N1MM+ closes (setting ExitWithN1mm), so nothing needs to wait here.
rem Set N1MM_EXE before running this file if N1MM+ is not in its usual folder.
setlocal
set "SYS=%SystemRoot%\System32"
if not defined N1MM_PROC set "N1MM_PROC=N1MMLogger.net.exe"
set "APP_PROC=N1MMMobileCountyAutomation.exe"
set "APP_EXE=%~dp0%APP_PROC%"

if not defined N1MM_EXE (
    if exist "%ProgramFiles(x86)%\N1MM Logger+\N1MMLogger.net.exe" set "N1MM_EXE=%ProgramFiles(x86)%\N1MM Logger+\N1MMLogger.net.exe"
)
if not defined N1MM_EXE (
    if exist "%ProgramFiles%\N1MM Logger+\N1MMLogger.net.exe" set "N1MM_EXE=%ProgramFiles%\N1MM Logger+\N1MMLogger.net.exe"
)

rem 1. N1MM+
call :running "%N1MM_PROC%"
if errorlevel 1 (
    if not defined N1MM_EXE (
        echo Could not find N1MM+. Set N1MM_EXE to the full path of N1MMLogger.net.exe and run this file again.
        pause
        exit /b 1
    )
    rem Started through Explorer, as a double-click would, so security software does not see cmd.exe launching it.
    start "" "%SystemRoot%\explorer.exe" "%N1MM_EXE%"
)

rem 2. County automation (only one copy)
call :running "%APP_PROC%"
if errorlevel 1 start "" "%APP_EXE%"
exit /b 0

rem :running "image.exe" -> errorlevel 0 if a process with that image name is running, 1 if not.
:running
"%SYS%\tasklist.exe" /fi "imagename eq %~1" /nh 2>nul | "%SYS%\find.exe" /i "%~1" >nul
exit /b %errorlevel%
