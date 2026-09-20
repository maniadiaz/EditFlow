namespace EditFlow.Engine.Encoders;

/// <summary>
/// Catálogo de los codificadores que EditFlow sabe manejar, y análisis de la
/// salida de <c>ffmpeg -encoders</c>.
/// </summary>
/// <remarks>
/// La lista es deliberadamente cerrada. FFmpeg expone más de doscientos codificadores,
/// pero cada uno necesita su propia traducción de calidad, bitrate y preset
/// (ver <c>FFmpegArgumentBuilder</c>). Ofrecer uno sin esa traducción produciría
/// exportaciones con parámetros ignorados en silencio.
/// </remarks>
public static class EncoderCatalog
{
    /// <summary>Codificadores soportados, en orden de preferencia dentro de cada códec.</summary>
    public static IReadOnlyList<EncoderDefinition> Known { get; } =
    [
        // --- H.264 -----------------------------------------------------------------
        new("h264_nvenc",        VideoCodec.H264, EncoderBackend.Nvenc,        "H.264 · NVIDIA NVENC"),
        new("h264_qsv",          VideoCodec.H264, EncoderBackend.QuickSync,    "H.264 · Intel Quick Sync"),
        new("h264_amf",          VideoCodec.H264, EncoderBackend.Amf,          "H.264 · AMD AMF"),
        new("h264_videotoolbox", VideoCodec.H264, EncoderBackend.VideoToolbox, "H.264 · Apple VideoToolbox"),
        new("h264_vaapi",        VideoCodec.H264, EncoderBackend.Vaapi,        "H.264 · VA-API"),
        new("libx264",           VideoCodec.H264, EncoderBackend.Software,     "H.264 · CPU (x264)"),

        // --- HEVC ------------------------------------------------------------------
        new("hevc_nvenc",        VideoCodec.Hevc, EncoderBackend.Nvenc,        "HEVC · NVIDIA NVENC"),
        new("hevc_qsv",          VideoCodec.Hevc, EncoderBackend.QuickSync,    "HEVC · Intel Quick Sync"),
        new("hevc_amf",          VideoCodec.Hevc, EncoderBackend.Amf,          "HEVC · AMD AMF"),
        new("hevc_videotoolbox", VideoCodec.Hevc, EncoderBackend.VideoToolbox, "HEVC · Apple VideoToolbox"),
        new("hevc_vaapi",        VideoCodec.Hevc, EncoderBackend.Vaapi,        "HEVC · VA-API"),
        new("libx265",           VideoCodec.Hevc, EncoderBackend.Software,     "HEVC · CPU (x265)"),

        // --- AV1 -------------------------------------------------------------------
        new("av1_nvenc",         VideoCodec.Av1,  EncoderBackend.Nvenc,        "AV1 · NVIDIA NVENC"),
        new("av1_qsv",           VideoCodec.Av1,  EncoderBackend.QuickSync,    "AV1 · Intel Quick Sync"),
        new("av1_amf",           VideoCodec.Av1,  EncoderBackend.Amf,          "AV1 · AMD AMF"),
        new("av1_vaapi",         VideoCodec.Av1,  EncoderBackend.Vaapi,        "AV1 · VA-API"),
        new("libsvtav1",         VideoCodec.Av1,  EncoderBackend.Software,     "AV1 · CPU (SVT-AV1)"),
    ];

    /// <summary>
    /// Extrae los nombres de los codificadores de video de la salida de <c>ffmpeg -encoders</c>.
    /// </summary>
    /// <remarks>
    /// El formato es una tabla precedida de una leyenda, separadas por una línea <c>------</c>:
    /// <code>
    ///  ------
    ///  V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
    ///  A..... aac                  AAC (Advanced Audio Coding)
    /// </code>
    /// La primera columna son banderas de capacidades, donde <c>V</c> indica video. Solo se
    /// recogen esas: lo que viene antes del separador es la leyenda, y confundirla con datos
    /// haría aparecer entradas como <c>=</c> o <c>Video</c> en la lista.
    /// </remarks>
    public static IReadOnlySet<string> ParseListedEncoders(string ffmpegOutput)
    {
        ArgumentNullException.ThrowIfNull(ffmpegOutput);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var pastHeader = false;

        foreach (var rawLine in ffmpegOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (!pastHeader)
            {
                if (line.Trim() == "------")
                {
                    pastHeader = true;
                }

                continue;
            }

            // Formato: un espacio, seis banderas, un espacio, el nombre.
            if (line.Length < 9 || line[0] != ' ' || line[1] != 'V')
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                names.Add(parts[1]);
            }
        }

        return names;
    }
}

/// <summary>Definición estática de un codificador conocido.</summary>
/// <param name="Name">Nombre en FFmpeg.</param>
/// <param name="Codec">Códec que produce.</param>
/// <param name="Backend">Motor que lo ejecuta.</param>
/// <param name="DisplayName">Etiqueta para la interfaz.</param>
public sealed record EncoderDefinition(
    string Name,
    VideoCodec Codec,
    EncoderBackend Backend,
    string DisplayName);
