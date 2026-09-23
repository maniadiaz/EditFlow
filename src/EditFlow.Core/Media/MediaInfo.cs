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
    /// <summary>
    /// Un hueco: tiempo sin imagen (negro) en la pista principal, sin archivo detrás.
    /// </summary>
    /// <remarks>
    /// Aparece al subir un trozo a una capa superior: la pista principal conserva su duración y
    /// nada de lo que hay después cambia de sitio. Su duración es enorme a propósito, para que
    /// recortarlo o dividirlo no tropiece con el límite de «el archivo».
    /// </remarks>
    public static MediaInfo Gap { get; } = new(string.Empty, TimeSpan.FromHours(24), 0, 0, 0, "gap", false);

    /// <summary>
    /// Indica que el archivo no se encontró al abrir el proyecto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un medio ausente <b>no</b> se descarta: se conserva con los datos técnicos que quedaron
    /// guardados —duración, tamaño, cadencia— para que sus clips sigan existiendo en el montaje,
    /// con su sitio, sus cortes y sus ajustes. Antes se tiraban, y eso convertía mover una carpeta
    /// en perder el trabajo: al reconectar el archivo ya no quedaba nada a lo que reconectarlo.
    /// </para>
    /// <para>
    /// Lo que un medio ausente no puede es decodificarse: el preview lo muestra en negro con un
    /// aviso y la exportación se niega a empezar hasta que se reconecte.
    /// </para>
    /// </remarks>
    public bool IsOffline { get; init; }

    /// <summary>Indica si es un hueco y no un archivo.</summary>
    public bool IsGap => Path.Length == 0;

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
