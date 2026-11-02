@echo off
rem Rebuilds the loader and the plugin (requires the .NET SDK) and puts them into bin\
cd /d "%~dp0plugin"
dotnet build Loader\SpaceMouseStudio.Loader.csproj -c Release -nologo || goto :error
dotnet build Plugin\SpaceMouseStudio.csproj -c Release -nologo || goto :error
echo.
echo Done. Run Install.bat afterwards to update the installation.
pause
exit /b 0
:error
echo Build failed.
pause
exit /b 1
