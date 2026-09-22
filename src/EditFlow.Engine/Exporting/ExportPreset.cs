// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Encoders;

namespace EditFlow.Engine.Exporting;

/// <summary>Ajustes de exportación pensados para un destino concreto.</summary>
/// <param name="Name">Nombre que se muestra en la lista.</param>
/// <param name="Description">Para qué sirve, en una línea.</param>
/// <param name="Resolution">Resolución en horizontal; <paramref name="Portrait"/> la gira.</param>
/// <param name="Portrait">Si el video es vertical.</param>
/// <param name="FrameRate">Fotogramas por segundo.</param>
/// <param name="Codec">Códec de video.</param>
/// <param name="Quality">Calidad de 1 a 100 (control de tasa por calidad constante).</param>
/// <param name="Speed">Compromiso entre rapidez y tamaño.</param>
/// <param name="AudioBitrateKbps">Bitrate del audio.</param>
/// <param name="Container">Contenedor del archivo.</param>
public sealed record ExportPreset(
    string Name,
    string Description,
    VideoResolution Resolution,
    bool Portrait,
    double FrameRate,
    VideoCodec Codec,
    int Quality,
    EncodingSpeed Speed,
    int AudioBitrateKbps,
    ExportContainer Container)
{
    /// <summary>El primero de la lista no cambia nada: es el modo manual.</summary>
    public const string CustomName = "Personalizado";

    /// <summary>Ajustes predefinidos, sin contar el modo manual.</summary>
    public static IReadOnlyList<ExportPreset> All { get; } =
    [
        new("YouTube 1080p", "Full HD a 30 fps en H.264, lo que YouTube recomienda.",
            VideoResolution.P1080, false, 30, VideoCodec.H264, 70, EncodingSpeed.Balanced, 192, ExportContainer.Mp4),
        new("YouTube 4K", "Ultra HD en HEVC: mucha nitidez sin archivos enormes.",
            VideoResolution.P2160, false, 30, VideoCodec.Hevc, 70, EncodingSpeed.Balanced, 256, ExportContainer.Mp4),
        new("Instagram / TikTok vertical", "1080×1920 a 30 fps, para móvil.",
            VideoResolution.P1080, true, 30, VideoCodec.H264, 65, EncodingSpeed.Balanced, 192, ExportContainer.Mp4),
        new("WhatsApp pequeño", "480p muy comprimido, para enviar por chat.",
            VideoResolution.P480, false, 30, VideoCodec.H264, 40, EncodingSpeed.Balanced, 128, ExportContainer.Mp4),
        new("Máxima calidad", "4K a 60 fps casi sin pérdida; el archivo será grande.",
            VideoResolution.P2160, false, 60, VideoCodec.Hevc, 92, EncodingSpeed.Quality, 320, ExportContainer.Mp4),
    ];
}
