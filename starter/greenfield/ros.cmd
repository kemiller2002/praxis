@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ros.ps1" %*
exit /b %ERRORLEVEL%
