// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Core.Media;

/// <summary>
/// Datos técnicos de un archivo de video, tal como los reporta ffprobe.
/// </summary>
/// <param name="Path">Ruta del archivo en disco.</param>
/// <param name="Duration">Duración total.</param>
/// <param name="Width">Ancho codificado, antes de aplicar rotación.</param>
/// <param name="Height">Alto codificado, antes de aplicar rotación.</param>
/// <param name="FrameRate">Fotogramas por segundo.</param>
/// <param name="VideoCodec">Códec de video, por ejemplo <c>h264</c>.</param>
/// <param name="HasAudio">Si el archivo trae pista de audio.</param>
/// <param name="Rotation">Rotación declarada en metadatos: 0, 90, 180 o 270 grados.</param>
public sealed record MediaInfo(
    string Path,
    TimeSpan Duration,
    int Width,
    int Height,
    double FrameRate,
    string VideoCodec,
    bool HasAudio,
    int Rotation = 0)
{
    /// <summary>Ancho tal como debe verse, ya aplicada la rotación.</summary>
    /// <remarks>
    /// Los videos grabados con móvil suelen almacenarse en horizontal con una rotación
    /// de 90 grados en los metadatos. Usar <see cref="Width"/> sin corregir haría que un
    /// video vertical se tratara como apaisado y se exportara tumbado.
    /// </remarks>
    public int DisplayWidth => IsQuarterTurned ? Height : Width;

    /// <summary>Alto tal como debe verse, ya aplicada la rotación.</summary>
    public int DisplayHeight => IsQuarterTurned ? Width : Height;

    /// <summary>Indica si el video se ve en vertical.</summary>
    public bool IsPortrait => DisplayHeight > DisplayWidth;

    /// <summary>Relación de aspecto visible.</summary>
    public double AspectRatio => DisplayHeight == 0 ? 0 : (double)DisplayWidth / DisplayHeight;

    private bool IsQuarterTurned => Rotation is 90 or 270 or -90 or -270;
}
