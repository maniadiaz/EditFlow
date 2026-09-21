// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;

namespace EditFlow.Engine.Proxies;

/// <summary>Decide qué medios merecen una copia ligera para editar.</summary>
/// <remarks>
/// <para>
/// El preview se decodifica a 480p, pero eso no reduce el coste de <i>leer</i> el original:
/// un 4K obliga a decodificar los 8 millones de píxeles de cada fotograma para luego tirar
/// casi todos. Medido en este proyecto, un salto en un 4K tarda 517 ms y en su copia de 480p
/// 61 ms, y además ocupa muchísima menos memoria, que en una máquina de 8 GB con Windows
/// residente es lo que decide si la edición va fluida.
/// </para>
/// <para>
/// Un video ya pequeño y en un códec barato no gana nada con una copia: se decodifica igual
/// de rápido y solo se gastaría disco.
/// </para>
/// </remarks>
public static class ProxyPolicy
{
    /// <summary>Alto de la copia de edición.</summary>
    public const int ProxyHeight = 480;

    private const long HdPixels = 1280L * 720;
    private const long SdPixels = 854L * 480;

    private static readonly HashSet<string> HeavyCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hevc", "av1", "vp9", "prores", "dnxhd", "mpeg2video",
    };

    /// <summary>Indica si conviene generar una copia de edición de este medio.</summary>
    public static bool NeedsProxy(MediaInfo media)
    {
        ArgumentNullException.ThrowIfNull(media);

        // Un audio no tiene imagen que aligerar.
        if (media.Width <= 0 || media.Height <= 0)
        {
            return false;
        }

        var pixels = (long)media.Width * media.Height;

        if (pixels > HdPixels)
        {
            return true;
        }

        // Los códecs pesados cuestan mucho incluso a resoluciones modestas.
        return pixels > SdPixels && HeavyCodecs.Contains(media.VideoCodec);
    }
}
