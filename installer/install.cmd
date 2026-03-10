@echo off
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0Install-WinGemini.ps1"
exit /b %errorlevel%
