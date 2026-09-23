@echo off
REM SPDX-FileCopyrightText: 2026 maniadiaz
REM SPDX-License-Identifier: GPL-3.0-or-later
REM
REM Envoltorio de check-ffmpeg.ps1, por lo mismo que fetch-ffmpeg.cmd: en Windows solo
REM esta Windows PowerShell 5.1 (no 'pwsh'), y la politica de ejecucion bloquea los
REM .ps1 por defecto.
REM
REM   tools\check-ffmpeg.cmd
REM   tools\check-ffmpeg.cmd -Path "%LOCALAPPDATA%\Programs\EditFlow\ffmpeg"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-ffmpeg.ps1" %*
exit /b %ERRORLEVEL%
