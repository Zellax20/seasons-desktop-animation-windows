@echo off
cd /d "%~dp0"
dotnet publish -c Release -o dist
echo.
if exist dist\Seasons.exe (echo Done: dist\Seasons.exe) else (echo Build failed, see the messages above.)
pause
