// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;
using EditFlow.Engine.Playback;

namespace EditFlow.Engine.PreviewCache;

/// <summary>Con qué calidad se renderizan las copias de preview.</summary>
/// <param name="Width">Ancho de los fotogramas.</param>
/// <param name="Height">Alto de los fotogramas.</param>
/// <param name="FrameRate">Fotogramas por segundo, entero.</param>
/// <remarks>
/// La velocidad es un entero a propósito: con una fracción como 29,97 un trozo de 5 s no duraría
/// exactamente 5 s, y al encadenar decenas de trozos el video se iría desfasando del audio.
/// </remarks>
public sealed record PreviewCacheSettings(int Width, int Height, int FrameRate)
{
    /// <summary>Crea los ajustes para una altura y la velocidad de fotogramas de los videos.</summary>
    public static PreviewCacheSettings For(int height, double sourceFrameRate)
    {
        var even = Math.Max(height / 2 * 2, 2);
        var rate = sourceFrameRate > 1 ? (int)Math.Round(sourceFrameRate) : 30;
        return new PreviewCacheSettings(PlaybackResolution.WidthFor(even), even, Math.Clamp(rate, 24, 60));
    }
}

/// <summary>Estado de un trozo de la copia de preview.</summary>
public enum SectionState
{
    /// <summary>Sin copia: se reproduce decodificando el original (🔴).</summary>
    NeedsRender,

    /// <summary>Esperando su turno en un renderizado en curso (🟡).</summary>
    Queued,

    /// <summary>Renderizándose ahora mismo (🟡).</summary>
    Rendering,

    /// <summary>Copia lista: se reproduce sin decodificar el original (🟢).</summary>
    Ready,
}

/// <summary>Un trozo de la timeline y el estado de su copia.</summary>
/// <param name="Index">Posición del trozo, desde 0.</param>
/// <param name="Start">Inicio en la timeline.</param>
/// <param name="End">Fin en la timeline.</param>
/// <param name="Hash">Huella de todo lo que influye en su imagen.</param>
/// <param name="State">Estado actual.</param>
public sealed record CacheSection(int Index, TimeSpan Start, TimeSpan End, string Hash, SectionState State);

/// <summary>Varios trozos listos y consecutivos, que se reproducen como un solo video continuo.</summary>
/// <param name="Start">Inicio en la timeline.</param>
/// <param name="End">Fin en la timeline.</param>
/// <param name="Path">Lista de trozos (<c>.ffconcat</c>) que el reproductor abre como un archivo.</param>
/// <param name="Settings">Tamaño y velocidad de los fotogramas.</param>
public sealed record CacheRun(TimeSpan Start, TimeSpan End, string Path, PreviewCacheSettings Settings);

/// <summary>Resumen del estado de la copia de preview, para mostrarlo.</summary>
/// <param name="Total">Trozos en la timeline.</param>
/// <param name="Ready">Con copia lista.</param>
/// <param name="Pending">Sin copia, en cola o renderizándose.</param>
/// <param name="IsRendering">Si hay un renderizado en marcha.</param>
/// <param name="Bytes">Espacio que ocupa la copia en disco.</param>
public sealed record CacheSummary(int Total, int Ready, int Pending, bool IsRendering, long Bytes);
