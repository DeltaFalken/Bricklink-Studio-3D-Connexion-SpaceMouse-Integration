@echo off
rem Installs the SpaceMouse plugin into BrickLink Studio 2.0 (asks once for administrator rights)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1" %*
pause
