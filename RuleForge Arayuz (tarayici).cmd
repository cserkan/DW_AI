@echo off
rem RuleForge arayüzünü açar (tarayıcıda). Kapatmak için bu pencereyi kapatın.
chcp 65001 >nul
cd /d "%~dp0"
if not exist "src\RuleForge.Cli\bin\Release\net48\ruleforge.exe" dotnet build -c Release
"src\RuleForge.Cli\bin\Release\net48\ruleforge.exe" arayuz
pause
