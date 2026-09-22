// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace EditFlow.Engine.Playback;

/// <summary>
/// Decodifica un archivo de video a fotogramas BGRA crudos, de forma continua.
/// </summary>
/// <remarks>
/// <para>
/// Un solo proceso de FFmpeg de larga vida emite los fotogramas uno tras otro. La medida
/// que justifica este diseño: arrancar <c>ffmpeg.exe</c> cuesta <b>43 ms</b>, mientras que
/// decodificar 480p sostenido alcanza <b>1209 fotogramas por segundo</b>. Lanzar un proceso
/// por fotograma —lo obvio para saltar a una posición— dedicaría el 70 % del tiempo a
/// arrancar procesos.
/// </para>
/// <para>
/// De ahí se sigue el reparto de responsabilidades: esta clase cubre la reproducción y el
/// relleno del búfer; saltar a una posición lejana significa crear otro lector, y ese coste
/// de 43 ms es aceptable para un salto, no para cada fotograma.
/// </para>
/// </remarks>
public sealed class FrameReader : IDisposable
{
    private readonly Process _process;
    private readonly Stream _output;
    private readonly int _frameBytes;
    private readonly double _frameRate;
    private readonly TimeSpan _start;

    private long _framesRead;
    private bool _disposed;

    /// <summary>Abre un lector de fotogramas desde un instante del archivo.</summary>
    /// <param name="tools">Ejecutables de FFmpeg.</param>
    /// <param name="path">Archivo de video.</param>
    /// <param name="start">Instante desde el que decodificar.</param>
    /// <param name="width">Ancho al que escalar.</param>
    /// <param name="height">Alto al que escalar.</param>
    /// <param name="frameRate">Fotogramas por segundo a los que normalizar.</param>
    /// <param name="hardwareDecoding">Si se pide a FFmpeg que decodifique con la tarjeta gráfica.</param>
    public FrameReader(
        FFmpegTools tools,
        string path,
        TimeSpan start,
        int width,
        int height,
        double frameRate,
        bool hardwareDecoding = false,
        string? colorFilter = null,
        string? transformFilter = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frameRate, 0);

        Width = width;
        Height = height;
        UsesHardwareDecoding = hardwareDecoding;
        _frameBytes = width * height * 4;
        _frameRate = frameRate;
        _start = start < TimeSpan.Zero ? TimeSpan.Zero : start;

        var startInfo = new ProcessStartInfo
        {
            FileName = tools.FFmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(path, _start, width, height, frameRate, hardwareDecoding, colorFilter, transformFilter))
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo };

        if (!_process.Start())
        {
            _process.Dispose();
            throw new InvalidOperationException($"No se pudo iniciar FFmpeg para '{path}'.");
        }

        // stderr se drena sin leerlo a fondo. Si nadie lo consume, su tubería se llena y
        // FFmpeg se bloquea a mitad de la decodificación sin dar ninguna señal.
        _ = Task.Run(() => DrainAsync(_process.StandardError.BaseStream));

        _output = _process.StandardOutput.BaseStream;
    }

    /// <summary>Ancho de los fotogramas que produce.</summary>
    public int Width { get; }

    /// <summary>Alto de los fotogramas que produce.</summary>
    public int Height { get; }

    /// <summary>Indica si se pidió decodificación por hardware.</summary>
    public bool UsesHardwareDecoding { get; }

    /// <summary>Instante del primer fotograma que produce.</summary>
    public TimeSpan Start => _start;

    /// <summary>Extensión de las listas de trozos que se leen como un solo video.</summary>
    public const string ConcatListExtension = ".ffconcat";

    internal static IReadOnlyList<string> BuildArguments(
        string path, TimeSpan start, int width, int height, double frameRate, bool hardwareDecoding = false,
        string? colorFilter = null, string? transformFilter = null)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error" };

        if (hardwareDecoding)
        {
            // 'auto' elige el mejor decodificador de la máquina —NVDEC, D3D11VA, VideoToolbox…— y
            // FFmpeg devuelve los fotogramas a memoria por su cuenta para escalarlos. Si el
            // archivo o el equipo no lo admiten, FFmpeg cae a software sin más; y si falla del
            // todo, VideoPlayer reintenta sin esta opción.
            arguments.AddRange(["-hwaccel", "auto"]);
        }

        // Una lista de trozos de copia de preview se lee como si fuera un único archivo continuo.
        if (path.EndsWith(ConcatListExtension, StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-f", "concat", "-safe", "0"]);
        }

        arguments.AddRange(BuildInputArguments(path, start, width, height, frameRate, colorFilter, transformFilter));
        return arguments;
    }

    private static string[] BuildInputArguments(
        string path, TimeSpan start, int width, int height, double frameRate, string? colorFilter,
        string? transformFilter) =>
    [
        // '-ss' antes de '-i' salta por índice en lugar de decodificar desde el principio.
        "-ss", start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
        "-i", path,

        // La normalización va aquí y no al mostrar: el reloj de reproducción cuenta
        // fotogramas, así que necesita que todos duren lo mismo aunque el origen tenga
        // una cadencia variable.
        "-vf", string.Create(CultureInfo.InvariantCulture,
            $"fps={frameRate:0.####},scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black")

            // El encuadre trabaja sobre el fotograma ya llevado a este tamaño: mismo sitio que en
            // el grafo de exportación.
            + (transformFilter is null ? string.Empty : "," + transformFilter)

            // El ajuste de color se aplica ya con la imagen a su tamaño de vista: es lo que menos cuesta.
            + (colorFilter is null ? string.Empty : ",format=yuv420p," + colorFilter),

        "-f", "rawvideo",
        "-pix_fmt", "bgra",
        "-",
    ];

    /// <summary>
    /// Lee el siguiente fotograma dentro del búfer indicado.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> si se leyó un fotograma completo; <see langword="false"/>
    /// al terminar el archivo.
    /// </returns>
    public async Task<bool> ReadIntoAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame.Pixels.Length != _frameBytes)
        {
            throw new ArgumentException(
                $"El fotograma es de {frame.Width}×{frame.Height} y el lector produce {Width}×{Height}.",
                nameof(frame));
        }

        // Una sola lectura no basta: una tubería devuelve lo que tenga disponible, que
        // casi nunca es un fotograma entero. Sin este bucle, los fotogramas salen
        // partidos y la imagen aparece desplazada o a franjas.
        var read = 0;
        while (read < _frameBytes)
        {
            var got = await _output
                .ReadAsync(frame.Pixels.AsMemory(read, _frameBytes - read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        frame.Timestamp = _start + TimeSpan.FromSeconds(_framesRead / _frameRate);
        frame.IsValid = true;
        _framesRead++;

        return true;
    }

    private static async Task DrainAsync(Stream stream)
    {
        try
        {
            var scratch = new byte[4096];
            while (await stream.ReadAsync(scratch).ConfigureAwait(false) > 0)
            {
                // El contenido no interesa; lo que importa es no dejar la tubería llena.
            }
        }
        catch (IOException)
        {
            // El proceso terminó mientras se drenaba.
        }
        catch (ObjectDisposedException)
        {
            // Idem.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
        catch (NotSupportedException)
        {
            // La plataforma no permite matar el árbol; el principal ya murió.
        }

        _process.Dispose();
    }
}
