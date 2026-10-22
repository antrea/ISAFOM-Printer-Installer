@echo off
setlocal
powershell.exe -NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Estrai_Configurazione_Kyocera.ps1" -VerificaIdentita
