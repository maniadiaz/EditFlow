// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using EditFlow.Engine;

namespace EditFlow.Engine.Tests;

public class FFmpegLocatorTests
{
    [Fact]
    public void Windows_is_not_told_to_run_pwsh()
    {
        // 'pwsh' es PowerShell 7 y no viene instalado con Windows: el ejecutable del
        // sistema es powershell.exe. Sugerirlo deja al usuario con un "no se reconoce
        // como un comando" en el primer paso del proyecto, que es exactamente lo que
        // pasó con la versión 0.1.0.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        Assert.DoesNotContain("pwsh", FFmpegLocator.FetchCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fetch-ffmpeg.cmd", FFmpegLocator.FetchCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Unix_platforms_are_told_to_run_the_script_directly()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        Assert.Contains("fetch-ffmpeg.ps1", FFmpegLocator.FetchCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_is_never_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(FFmpegLocator.FetchCommand));
    }

    [Fact]
    public void The_search_always_reports_where_it_looked()
    {
        // Cuando FFmpeg no aparece, saber dónde se buscó es la diferencia entre
        // corregirlo en un minuto y no saber por dónde empezar. La lista se llena
        // tanto si se encuentra como si no; el PATH solo aparece cuando la búsqueda
        // llega hasta él, porque es el último recurso.
        var found = FFmpegLocator.TryLocate(out var tools, out var searched);

        Assert.NotEmpty(searched);

        if (found)
        {
            Assert.True(File.Exists(tools.FFmpegPath), tools.FFmpegPath);
            Assert.True(File.Exists(tools.FFprobePath), tools.FFprobePath);
            Assert.False(string.IsNullOrWhiteSpace(tools.Origin));
        }
        else
        {
            // Sin encontrarlo, se agotan todas las ubicaciones, PATH incluido.
            Assert.Contains(searched, s => s.Contains("PATH", StringComparison.OrdinalIgnoreCase));
        }
    }
}
