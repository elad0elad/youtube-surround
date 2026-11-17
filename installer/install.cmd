@echo off
title YouTube Surround
echo Installing YouTube Surround...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 exit /b 1
echo.
echo YouTube Surround is installed.
