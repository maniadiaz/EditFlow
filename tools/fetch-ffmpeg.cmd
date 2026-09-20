@echo off
rem Envoltorio para Windows.
rem
rem 'pwsh' es PowerShell 7 y NO viene instalado con Windows: solo esta Windows
rem PowerShell 5.1, cuyo ejecutable se llama powershell.exe. Documentar 'pwsh'
rem deja al usuario con un "no se reconoce como un comando" en el primer paso
rem del proyecto.
rem
rem El -ExecutionPolicy Bypass evita el otro tropiezo habitual: por defecto
rem Windows se niega a ejecutar scripts .ps1.
rem
rem Este archivo va en CRLF y sin acentos a proposito: cmd.exe interpreta mal
rem los .cmd guardados con saltos de linea LF o con caracteres no ASCII.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-ffmpeg.ps1" %*
exit /b %ERRORLEVEL%
