@echo off
rem RuleForge masaüstü uygulamasını açar (kendi penceresinde). Gerekirse önce derler.
chcp 65001 >nul
cd /d "%~dp0"
set APP=src\RuleForge.Masaustu\bin\Release\net48\RuleForgeApp.exe
if not exist "%APP%" dotnet build -c Release
if not exist "%APP%" (
  echo Derleme basarisiz oldu. Hata mesajini gonderin.
  pause
  exit /b 1
)
start "" "%APP%"
