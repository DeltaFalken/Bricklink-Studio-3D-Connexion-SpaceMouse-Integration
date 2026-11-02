@echo off
rem Removes the SpaceMouse plugin from BrickLink Studio 2.0
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1" -Uninstall %*
pause
