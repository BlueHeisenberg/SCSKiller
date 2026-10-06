@echo off
rem Right-click, Run as administrator: runs prebuilt-test.ps1 next to this file (see there).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prebuilt-test.ps1" %*
pause
