// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Tests;

/// <summary>Limpieza de carpetas temporales de las pruebas de integración.</summary>
internal static class TempDirectory
{
    /// <summary>
    /// Borra la carpeta reintentando unos instantes.
    /// </summary>
    /// <remarks>
    /// Al soltar un reproductor, el proceso de FFmpeg que leía de ella se mata sin esperarle
    /// (esperar bloquearía la interfaz). En Windows el archivo sigue bloqueado unos
    /// milisegundos más, y con la máquina cargada borrar de inmediato falla sin que haya
    /// ningún error real.
    /// </remarks>
    public static void DeleteWithRetry(this DirectoryInfo directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                directory.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 30)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 30)
            {
                Thread.Sleep(100);
            }
        }
    }
}
