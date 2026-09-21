// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EditFlow.Engine.Execution;

namespace EditFlow.Engine.Filmstrips;

/// <summary>
/// Fotogramas sueltos de cada video, uno cada pocos segundos, para dibujar las miniaturas
/// dentro de los clips de la timeline.
/// </summary>
/// <remarks>
/// <para>
/// Se generan de una pasada por archivo y en segundo plano, y se guardan en disco para las
/// siguientes sesiones. Mientras FFmpeg avanza, los fotogramas ya escritos se pueden dibujar:
/// la tira se va llenando de izquierda a derecha en lugar de aparecer de golpe al final.
/// </para>
/// <para>
/// El intervalo es fijo. Con más zoom del que cubre, algún fotograma se repite en varias
/// casillas contiguas; es preferible a generar miles de imágenes para un zoom que casi
/// nadie usa.
/// </para>
/// </remarks>
public sealed class FilmstripCache : IDisposable
{
    /// <summary>Segundos entre dos fotogramas guardados.</summary>
    public const double IntervalSeconds = 2;

    /// <summary>Alto de cada fotograma guardado, en píxeles.</summary>
    public const int FrameHeight = 54;

    private const int Version = 1;
    private const string DoneMarker = "done";

    private readonly FFmpegTools _tools;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Strip> _strips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Source, string Directory)> _queue = Channel.CreateUnbounded<(string, string)>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private bool _disposed;

    /// <summary>Crea la caché y arranca su hilo de trabajo.</summary>
    public FilmstripCache(FFmpegTools tools, string directory)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _tools = tools;
        _directory = directory;
        _worker = Task.Run(() => RunAsync(_lifetime.Token));
    }

    private sealed class Strip(string directory)
    {
        public string Directory { get; } = directory;

        public int Count;
    }

    /// <summary>Carpeta por defecto, en los datos locales del usuario.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EditFlow",
        "filmstrips");

    /// <summary>
    /// Avisa de que hay fotogramas nuevos de un archivo. Se invoca en un hilo del pool: quien
    /// toque la interfaz debe reenviarlo al suyo.
    /// </summary>
    public event Action<string>? Updated;

    /// <summary>Argumentos de FFmpeg que escriben un fotograma cada <see cref="IntervalSeconds"/>.</summary>
    public static IReadOnlyList<string> BuildArguments(string sourcePath, string outputPattern) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",

        // Dos hilos: es trabajo de fondo y no debe quitarle la CPU a la edición.
        "-threads", "2",
        "-i", sourcePath,
        "-an",
        "-vf", string.Create(
            CultureInfo.InvariantCulture,
            $"fps=1/{IntervalSeconds},scale=-2:{FrameHeight}"),
        "-q:v", "5",
        "-f", "image2",
        outputPattern,
    ];

    /// <summary>Ruta del fotograma de un archivo en una posición, si ya existe.</summary>
    /// <param name="sourcePath">Video de origen.</param>
    /// <param name="time">Instante dentro del video.</param>
    /// <returns>La imagen, o <see langword="null"/> si aún no se ha generado ese tramo.</returns>
    /// <remarks>Solo consulta memoria: puede llamarse al dibujar sin coste de disco.</remarks>
    public string? FrameAt(string sourcePath, TimeSpan time)
    {
        if (!_strips.TryGetValue(sourcePath, out var strip) || strip.Count == 0)
        {
            return null;
        }

        // Redondeo hacia arriba en el punto medio: con el redondeo bancario por defecto, 2,5 y 3,5
        // caerían en casillas pares y la tira se movería a saltos irregulares.
        var index = (int)Math.Round(Math.Max(time.TotalSeconds, 0) / IntervalSeconds, MidpointRounding.AwayFromZero);

        // Más allá del último fotograma disponible se usa el último: al final de un video
        // el redondeo puede pedir uno más de los que FFmpeg llegó a escribir.
        index = Math.Min(index, Volatile.Read(ref strip.Count) - 1);

        return Path.Combine(strip.Directory, string.Create(CultureInfo.InvariantCulture, $"{index + 1:D5}.jpg"));
    }

    /// <summary>Pide los fotogramas de un archivo si aún no los hay ni están en cola.</summary>
    /// <param name="sourcePath">Video del que se quieren los fotogramas.</param>
    /// <param name="decodeFrom">Archivo del que decodificar; una copia de edición es mucho más rápida que el original.</param>
    public void Request(string sourcePath, string? decodeFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (_disposed)
        {
            return;
        }

        var directory = DirectoryFor(sourcePath);
        if (directory is null)
        {
            return;
        }

        var strip = new Strip(directory);
        if (!_strips.TryAdd(sourcePath, strip))
        {
            return;
        }

        // Una carpeta completa de una sesión anterior se reutiliza tal cual.
        if (File.Exists(Path.Combine(directory, DoneMarker)))
        {
            strip.Count = CountFrames(directory);
            Updated?.Invoke(sourcePath);
            return;
        }

        _queue.Writer.TryWrite((decodeFrom ?? sourcePath, sourcePath));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (decodeFrom, sourcePath) in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessAsync(decodeFrom, sourcePath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre de la aplicación.
        }
    }

    private async Task ProcessAsync(string decodeFrom, string sourcePath, CancellationToken cancellationToken)
    {
        if (!_strips.TryGetValue(sourcePath, out var strip))
        {
            return;
        }

        try
        {
            // Una carpeta a medias de una ejecución interrumpida se empieza de cero: mezclar
            // fotogramas viejos con nuevos daría una tira con saltos.
            if (Directory.Exists(strip.Directory))
            {
                Directory.Delete(strip.Directory, recursive: true);
            }

            Directory.CreateDirectory(strip.Directory);

            using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var poller = PollAsync(sourcePath, strip, polling.Token);

            var result = await ProcessRunner.RunAsync(
                _tools.FFmpegPath,
                BuildArguments(decodeFrom, Path.Combine(strip.Directory, "%05d.jpg")),
                cancellationToken).ConfigureAwait(false);

            await polling.CancelAsync().ConfigureAwait(false);
            await poller.ConfigureAwait(false);

            strip.Count = CountFrames(strip.Directory);

            if (result.Succeeded && strip.Count > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(strip.Directory, DoneMarker), "ok", cancellationToken)
                    .ConfigureAwait(false);
            }

            Updated?.Invoke(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin miniaturas la timeline sigue funcionando con el color de siempre.
        }
    }

    // Avisa mientras FFmpeg trabaja para que la tira se vaya llenando a la vista.
    private async Task PollAsync(string sourcePath, Strip strip, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

                var count = CountFrames(strip.Directory);
                if (count != strip.Count)
                {
                    // El último archivo puede estar aún escribiéndose: se descarta para no
                    // dibujar una imagen truncada.
                    strip.Count = Math.Max(count - 1, 0);
                    Updated?.Invoke(sourcePath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Terminó el trabajo.
        }
    }

    private static int CountFrames(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.jpg").Count()
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Ruta, tamaño y fecha del archivo: si el video cambia, los fotogramas viejos dejan de
    // encontrarse. Es un hash para que la carpeta no revele qué videos tiene el usuario.
    private string? DirectoryFor(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            return null;
        }

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}|{FrameHeight}|{IntervalSeconds}|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Path.Combine(_directory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant());
    }

    /// <summary>Borra las carpetas que no se usan desde hace tiempo.</summary>
    /// <returns>Cuántas carpetas se eliminaron.</returns>
    public int TrimUnusedFor(TimeSpan age)
    {
        var root = new DirectoryInfo(_directory);
        if (!root.Exists)
        {
            return 0;
        }

        var removed = 0;
        var limit = DateTime.UtcNow - age;

        foreach (var folder in root.EnumerateDirectories())
        {
            try
            {
                if (folder.LastWriteTimeUtc < limit)
                {
                    folder.Delete(recursive: true);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // En uso: se intentará en otra limpieza.
            }
        }

        return removed;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Ya estaba cancelado.
        }

        _lifetime.Dispose();
    }
}
