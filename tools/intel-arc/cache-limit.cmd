@echo off
rem Double-click: runs cache-limit.ps1 next to this file with the driver scan (see there).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cache-limit.ps1" -ScanDriver %*
pause
