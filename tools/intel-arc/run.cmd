@echo off
rem Double-click: runs measure.ps1 next to this file (see there).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0measure.ps1" %*
pause
