@echo off
setlocal

rem Double-clicking a .ps1 normally opens it in Notepad instead of running
rem it, and trust.ps1 (#Requires -RunAsAdministrator) refuses to work
rem without elevation anyway. This wrapper is what's actually meant to be
rem double-clicked: checks for admin rights, asks for them via UAC if
rem missing, then runs trust.ps1 from the elevated process (see plan
rem section 3.9, "Запуск trust.ps1"). English-only text on purpose — cmd.exe
rem misparses Cyrillic in .bat/.cmd files even with chcp 65001 (found by a
rem live test); trust.ps1 itself (UTF-8 with BOM) prints Russian output fine.

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo Administrator rights are required - Windows will ask for confirmation now...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0trust.ps1"

echo.
echo Press any key to close this window...
pause >nul
