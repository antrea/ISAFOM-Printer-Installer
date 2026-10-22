@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Estrai_Configurazione_Kyocera.ps1" -Sentinella
pause
