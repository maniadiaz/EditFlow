using System.Globalization;
using System.Text.Json;
using EditFlow.Core.Media;
using EditFlow.Engine.Execution;

namespace EditFlow.Engine.Probing;

/// <summary>
/// Lee los datos técnicos de un archivo de video con ffprobe.
/// </summary>
/// <remarks>
/// Se usa la salida JSON y no el formato <c>clave=valor</c>. Este último no indica a qué
/// flujo pertenece cada clave hasta que aparece <c>codec_type</c>, que llega <b>después</b>
/// de <c>codec_name</c>: un parser secuencial acaba atribuyendo al video el códec del
/// audio. El JSON agrupa cada flujo en su propio objeto y elimina esa ambigüedad.
/// </remarks>
public sealed class FFprobeService
{
    private readonly FFmpegTools _tools;

    /// <summary>Crea un lector que usará el ffprobe indicado.</summary>
    public FFprobeService(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>
    /// Lee los datos de un archivo de video.
    /// </summary>
    /// <exception cref="FileNotFoundException">Si el archivo no existe.</exception>
    /// <exception cref="InvalidOperationException">Si ffprobe falla o el archivo no tiene video.</exception>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encontró el archivo de video.", path);
        }

        string[] arguments =
        [
            "-hide_banner",
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            path,
        ];

        var result = await ProcessRunner
            .RunAsync(_tools.FFprobePath, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffprobe no pudo leer '{Path.GetFileName(path)}': {result.StandardError.Trim()}");
        }

        return Parse(result.StandardOutput, path);
    }

    /// <summary>Interpreta la salida JSON de ffprobe.</summary>
    /// <exception cref="InvalidOperationException">Si el archivo no contiene video.</exception>
    internal static MediaInfo Parse(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"ffprobe no devolvió flujos para '{Path.GetFileName(path)}'.");
        }

        JsonElement? video = null;
        var hasAudio = false;

        foreach (var stream in streams.EnumerateArray())
        {
            var type = GetString(stream, "codec_type");

            if (type == "video" && video is null)
            {
                // Las carátulas incrustadas también se declaran como flujo de video. Se
                // reconocen porque son una imagen fija; tomarlas por el video real daría
                // una duración y unas dimensiones equivocadas.
                if (!IsAttachedPicture(stream))
                {
                    video = stream;
                }
            }
            else if (type == "audio")
            {
                hasAudio = true;
            }
        }

        if (video is null)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(path)}' no contiene ningún flujo de video.");
        }

        var stream2 = video.Value;

        return new MediaInfo(
            Path: path,
            Duration: ReadDuration(root, stream2),
            Width: GetInt(stream2, "width"),
            Height: GetInt(stream2, "height"),
            FrameRate: ReadFrameRate(stream2),
            VideoCodec: GetString(stream2, "codec_name") ?? "unknown",
            HasAudio: hasAudio,
            Rotation: ReadRotation(stream2));
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.TryGetProperty("attached_pic", out var attached) &&
        attached.ValueKind == JsonValueKind.Number &&
        attached.GetInt32() == 1;

    private static TimeSpan ReadDuration(JsonElement root, JsonElement stream)
    {
        // La duración del contenedor es la fiable. Algunos flujos no la declaran, y en
        // otros formatos difiere de la real, así que el flujo solo sirve de reserva.
        if (root.TryGetProperty("format", out var format) &&
            TryReadSeconds(format, "duration", out var containerDuration))
        {
            return containerDuration;
        }

        return TryReadSeconds(stream, "duration", out var streamDuration)
            ? streamDuration
            : TimeSpan.Zero;
    }

    private static bool TryReadSeconds(JsonElement element, string property, out TimeSpan value)
    {
        value = TimeSpan.Zero;

        var raw = GetString(element, property);
        if (raw is null ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            double.IsNaN(seconds) || seconds < 0)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static double ReadFrameRate(JsonElement stream)
    {
        // Llega como fracción, por ejemplo "30000/1001" para los 29,97 fps de NTSC.
        // Redondearlo a 30 introduciría una deriva de audio de un segundo cada media hora.
        var raw = GetString(stream, "r_frame_rate") ?? GetString(stream, "avg_frame_rate");
        if (raw is null)
        {
            return 0;
        }

        var parts = raw.Split('/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0)
        {
            return 0;
        }

        return numerator / denominator;
    }

    private static int ReadRotation(JsonElement stream)
    {
        // FFmpeg moderno expone la rotación en side_data_list; las grabaciones antiguas
        // la llevan en el tag 'rotate'. Se consultan ambos porque conviven en el parque
        // de archivos reales.
        if (stream.TryGetProperty("side_data_list", out var sideData) &&
            sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out var rotation) &&
                    rotation.ValueKind == JsonValueKind.Number)
                {
                    return Normalise(rotation.GetInt32());
                }
            }
        }

        if (stream.TryGetProperty("tags", out var tags))
        {
            var legacy = GetString(tags, "rotate");
            if (legacy is not null &&
                int.TryParse(legacy, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees))
            {
                return Normalise(degrees);
            }
        }

        return 0;

        static int Normalise(int degrees)
        {
            var normalised = degrees % 360;
            return normalised < 0 ? normalised + 360 : normalised;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
