// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Encoders;

namespace EditFlow.Engine.Exporting;

/// <summary>Estimación del tamaño de un archivo exportado antes de exportarlo.</summary>
/// <remarks>
/// Con bitrate fijo la cuenta es exacta salvo por el contenedor. Con calidad constante no se
/// puede saber: depende de lo complicada que sea cada escena. Se parte del bitrate que se
/// sugiere para la resolución y el códec y se ajusta por la calidad elegida y por la velocidad
/// de fotogramas; el resultado es orientativo, y así se muestra.
/// </remarks>
public static class ExportEstimate
{
    /// <summary>Bitrate de video estimado, en kilobits por segundo.</summary>
    public static int VideoKbps(ExportSettings settings, VideoCodec codec)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.RateControl != RateControlMode.ConstantQuality)
        {
            return settings.VideoBitrateKbps;
        }

        var baseline = QualityScale.SuggestedBitrateKbps(settings.Resolution, codec);

        // Cada 25 puntos de calidad duplican el bitrate: 65 es el punto de referencia, 90 el doble
        // y 40 la mitad. Un video a 60 fps cuesta bastante menos del doble que a 30, porque
        // fotogramas seguidos se parecen mucho y se comprimen bien.
        var quality = Math.Pow(2, (settings.Quality - 65) / 25.0);
        var rate = Math.Pow(settings.FrameRate / 30.0, 0.75);

        return (int)Math.Round(baseline * quality * rate);
    }

    /// <summary>Tamaño estimado, en bytes, de exportar una duración con estos ajustes.</summary>
    public static long SizeBytes(ExportSettings settings, VideoCodec codec, TimeSpan duration)
    {
        var kilobitsPerSecond = VideoKbps(settings, codec)
            + (settings.IncludeAudio ? settings.AudioBitrateKbps : 0);

        return (long)(kilobitsPerSecond * 1000.0 / 8 * Math.Max(duration.TotalSeconds, 0));
    }
}
