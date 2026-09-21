// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Engine.Encoders;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Traduce unos <see cref="ExportSettings"/> a los argumentos de FFmpeg del codificador
/// elegido.
/// </summary>
/// <remarks>
/// Cada familia de codificadores expresa lo mismo de forma distinta, y las diferencias
/// no son cosméticas: el mismo <c>-crf</c> no existe en NVENC, y <c>-rc vbr_peak</c> solo
/// lo entiende AMF. Pasar una opción que el codificador no reconoce no siempre produce un
/// error: a menudo se ignora en silencio y la exportación sale con ajustes que nadie pidió.
///
/// Todos los valores de esta tabla se comprobaron contra <c>ffmpeg -h encoder=&lt;nombre&gt;</c>
/// con la build n9.0 que empaqueta el proyecto.
/// </remarks>
public static class FFmpegArgumentBuilder
{
    /// <summary>
    /// Construye los argumentos de salida: codificador, control de tasa, audio y contenedor.
    /// </summary>
    /// <exception cref="ArgumentException">Si los ajustes son incoherentes.</exception>
    public static IReadOnlyList<string> BuildOutputArguments(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        var arguments = new List<string> { "-c:v", settings.EncoderName };

        arguments.AddRange(RateControlArguments(settings));
        arguments.AddRange(SpeedArguments(settings));

        // Audio: AAC es el único códec que reproduce absolutamente todo.
        if (settings.IncludeAudio)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", Kbps(settings.AudioBitrateKbps)]);
        }
        else
        {
            arguments.Add("-an");
        }

        // El índice al principio solo existe en MP4 y MOV; Matroska no lo necesita.
        var faststart = settings.OptimizeForStreaming && settings.Container != ExportContainer.Mkv;

        if (settings.IsSegmented)
        {
            arguments.AddRange(SegmentArguments(settings, faststart));
            arguments.Add(settings.SegmentPattern);
            return arguments;
        }

        if (faststart)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.Add(settings.OutputPath);
        return arguments;
    }

    /// <summary>Argumentos que reparten la salida en archivos de la duración pedida.</summary>
    private static List<string> SegmentArguments(ExportSettings settings, bool faststart)
    {
        var seconds = settings.SegmentDuration!.Value.TotalSeconds
            .ToString("0.###", CultureInfo.InvariantCulture);

        // El muxer de segmentos corta en el primer fotograma clave posterior al instante pedido.
        // Sin ayuda, el codificador los coloca cuando le conviene y las partes saldrían de
        // duración irregular. Forzar uno exactamente en cada múltiplo hace que el corte caiga
        // donde se pidió.
        var arguments = new List<string> { "-force_key_frames", $"expr:gte(t,n_forced*{seconds})" };

        // NVENC y x265 solo convierten ese fotograma forzado en un IDR real si se les pide
        // expresamente; sin ello el corte se pospone hasta su siguiente fotograma clave natural.
        if (BackendOf(settings.EncoderName) == EncoderBackend.Nvenc
            || string.Equals(settings.EncoderName, "libx265", StringComparison.Ordinal))
        {
            arguments.AddRange(["-forced-idr", "1"]);
        }

        var format = settings.Container switch
        {
            ExportContainer.Mkv => "matroska",
            ExportContainer.Mov => "mov",
            _ => "mp4",
        };

        arguments.AddRange(
        [
            "-f", "segment",
            "-segment_time", seconds,

            // x265 retrasa unas centésimas sus marcas de tiempo; sin esta tolerancia el fotograma
            // clave de los 4 s llega "tarde" y el corte se salta a la siguiente ocasión.
            "-segment_time_delta", "0.05",
            "-segment_format", format,
            "-reset_timestamps", "1",
            "-segment_start_number", "1",
        ]);

        if (faststart)
        {
            arguments.AddRange(["-segment_format_options", "movflags=+faststart"]);
        }

        return arguments;
    }

    /// <summary>Comprueba que los ajustes tienen sentido antes de construir nada.</summary>
    /// <exception cref="ArgumentException">Si falta un dato obligatorio o está fuera de rango.</exception>
    public static void Validate(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.OutputPath))
        {
            throw new ArgumentException("Falta la ruta del archivo de salida.", nameof(settings));
        }

        if (settings.RateControl is RateControlMode.VariableBitrate or RateControlMode.ConstantBitrate
            && settings.VideoBitrateKbps <= 0)
        {
            throw new ArgumentException(
                $"El modo {settings.RateControl} exige un bitrate de video mayor que cero.",
                nameof(settings));
        }

        if (settings.FrameRate <= 0)
        {
            throw new ArgumentException("Los fotogramas por segundo deben ser mayores que cero.", nameof(settings));
        }

        if (settings.AudioBitrateKbps <= 0)
        {
            throw new ArgumentException("El bitrate de audio debe ser mayor que cero.", nameof(settings));
        }

        if (settings.SegmentDuration is { } segment && segment < ExportSettings.MinimumSegment)
        {
            throw new ArgumentException(
                "Cada parte debe durar al menos un segundo.", nameof(settings));
        }
    }

    private static List<string> RateControlArguments(ExportSettings settings)
    {
        var encoder = settings.EncoderName;
        var backend = BackendOf(encoder);
        var bitrate = settings.VideoBitrateKbps;
        var quality = QualityScale.ToNative(settings.Quality, encoder);

        return backend switch
        {
            EncoderBackend.Nvenc => settings.RateControl switch
            {
                // El '-b:v 0' NO es opcional. Sin él, NVENC aplica su bitrate por defecto
                // y el '-cq' se ignora sin aviso: la exportación sale con una calidad que
                // no guarda relación con la pedida.
                RateControlMode.ConstantQuality =>
                    ["-rc", "vbr", "-cq", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0"],

                RateControlMode.VariableBitrate =>
                    ["-rc", "vbr", "-b:v", Kbps(bitrate),
                     "-maxrate", Kbps(bitrate * 3 / 2), "-bufsize", Kbps(bitrate * 2)],

                _ => ["-rc", "cbr", "-b:v", Kbps(bitrate), "-bufsize", Kbps(bitrate * 2)],
            },

            EncoderBackend.QuickSync => settings.RateControl switch
            {
                RateControlMode.ConstantQuality =>
                    ["-global_quality", quality.ToString(CultureInfo.InvariantCulture), "-look_ahead", "1"],

                RateControlMode.VariableBitrate =>
                    ["-b:v", Kbps(bitrate), "-maxrate", Kbps(bitrate * 3 / 2)],

                _ => ["-b:v", Kbps(bitrate), "-maxrate", Kbps(bitrate)],
            },

            EncoderBackend.Amf => settings.RateControl switch
            {
                RateControlMode.ConstantQuality =>
                    ["-rc", "cqp",
                     "-qp_i", quality.ToString(CultureInfo.InvariantCulture),
                     "-qp_p", quality.ToString(CultureInfo.InvariantCulture)],

                RateControlMode.VariableBitrate =>
                    ["-rc", "vbr_peak", "-b:v", Kbps(bitrate), "-maxrate", Kbps(bitrate * 3 / 2)],

                _ => ["-rc", "cbr", "-b:v", Kbps(bitrate)],
            },

            EncoderBackend.VideoToolbox => settings.RateControl switch
            {
                RateControlMode.ConstantQuality =>
                    ["-q:v", quality.ToString(CultureInfo.InvariantCulture)],

                _ => ["-b:v", Kbps(bitrate)],
            },

            // SVT-AV1 no acepta las opciones genéricas de bitrate: '-maxrate' y
            // '-minrate' le hacen fallar con "Error setting encoder parameters".
            // Comprobado contra la build n9.0 que empaqueta el proyecto.
            _ when encoder == "libsvtav1" => settings.RateControl switch
            {
                RateControlMode.ConstantQuality =>
                    ["-crf", quality.ToString(CultureInfo.InvariantCulture)],

                // Solo el bitrate objetivo; cualquier acotación adicional lo rompe.
                RateControlMode.VariableBitrate => ["-b:v", Kbps(bitrate)],

                // El bitrate constante exige activarlo por la API propia de SVT-AV1, y
                // esta solo lo admite junto con 'pred-struct=1' (baja latencia), que
                // desactiva las referencias hacia adelante y reduce la compresión de
                // forma apreciable. Ver DescribeRateControlSupport.
                _ => ["-b:v", Kbps(bitrate), "-svtav1-params", "rc=2:pred-struct=1"],
            },

            // x264, x265 y VA-API sí entienden las opciones genéricas.
            _ => settings.RateControl switch
            {
                RateControlMode.ConstantQuality =>
                    ["-crf", quality.ToString(CultureInfo.InvariantCulture)],

                RateControlMode.VariableBitrate =>
                    ["-b:v", Kbps(bitrate),
                     "-maxrate", Kbps(bitrate * 3 / 2), "-bufsize", Kbps(bitrate * 2)],

                _ => ["-b:v", Kbps(bitrate), "-minrate", Kbps(bitrate),
                      "-maxrate", Kbps(bitrate), "-bufsize", Kbps(bitrate * 2)],
            },
        };
    }

    private static List<string> SpeedArguments(ExportSettings settings)
    {
        var encoder = settings.EncoderName;

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            // p1 es el más rápido y p7 el de mejor calidad. '-tune hq' es lo adecuado
            // para exportar a archivo; las demás afinaciones apuntan a emisión en directo.
            var preset = settings.Speed switch
            {
                EncodingSpeed.Fastest => "p1",
                EncodingSpeed.Fast => "p3",
                EncodingSpeed.Balanced => "p5",
                EncodingSpeed.Quality => "p6",
                _ => "p7",
            };

            return ["-preset", preset, "-tune", "hq"];
        }

        if (encoder == "libsvtav1")
        {
            // En SVT-AV1 la escala va al revés que en x264: 0 es lo más lento y mejor,
            // 13 lo más rápido. Invertirla produciría exportaciones lentísimas cuando
            // el usuario pide velocidad.
            var preset = settings.Speed switch
            {
                EncodingSpeed.Fastest => "10",
                EncodingSpeed.Fast => "8",
                EncodingSpeed.Balanced => "6",
                EncodingSpeed.Quality => "4",
                _ => "2",
            };

            return ["-preset", preset];
        }

        if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            var preset = settings.Speed switch
            {
                EncodingSpeed.Fastest => "veryfast",
                EncodingSpeed.Fast => "faster",
                EncodingSpeed.Balanced => "medium",
                EncodingSpeed.Quality => "slower",
                _ => "veryslow",
            };

            return ["-preset", preset];
        }

        if (encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            var quality = settings.Speed switch
            {
                EncodingSpeed.Fastest or EncodingSpeed.Fast => "speed",
                EncodingSpeed.Balanced => "balanced",
                _ => "quality",
            };

            return ["-quality", quality];
        }

        if (encoder is "libx264" or "libx265")
        {
            var preset = settings.Speed switch
            {
                EncodingSpeed.Fastest => "ultrafast",
                EncodingSpeed.Fast => "veryfast",
                EncodingSpeed.Balanced => "medium",
                EncodingSpeed.Quality => "slow",
                _ => "veryslow",
            };

            return ["-preset", preset];
        }

        // VideoToolbox y VA-API no exponen un preset de velocidad equivalente.
        return [];
    }

    /// <summary>Motor al que pertenece un codificador, deducido de su nombre.</summary>
    internal static EncoderBackend BackendOf(string encoderName)
    {
        foreach (var definition in EncoderCatalog.Known)
        {
            if (string.Equals(definition.Name, encoderName, StringComparison.Ordinal))
            {
                return definition.Backend;
            }
        }

        return EncoderBackend.Software;
    }

    private static string Kbps(int value) =>
        value.ToString(CultureInfo.InvariantCulture) + "k";
}
