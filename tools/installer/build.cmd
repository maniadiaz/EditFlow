@echo off
REM SPDX-FileCopyrightText: 2026 maniadiaz
REM SPDX-License-Identifier: GPL-3.0-or-later
REM
REM Envoltorio de build.ps1, por lo mismo que fetch-ffmpeg.cmd: en Windows solo esta
REM Windows PowerShell 5.1 (no 'pwsh'), y la politica de ejecucion bloquea los .ps1
REM por defecto.
REM
REM   tools\installer\build.cmd                    usa la version del ultimo tag
REM   tools\installer\build.cmd 0.6.0              version explicita
REM   tools\installer\build.cmd 0.6.0 -SkipPublish reutiliza lo ya publicado
REM
REM Los argumentos se pasan tal cual: -Version es posicional en build.ps1.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
