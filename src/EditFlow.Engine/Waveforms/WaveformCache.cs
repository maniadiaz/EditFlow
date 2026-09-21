// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace EditFlow.Engine.Waveforms;

/// <summary>
/// Formas de onda de los archivos del proyecto: se calculan en segundo plano, una vez, y se
/// guardan en disco para las siguientes sesiones.
/// </summary>
/// <remarks>
/// Calcular la forma de onda de una hora de audio tarda unos segundos, así que nunca se hace
/// al dibujar. La interfaz pregunta por <see cref="TryGet"/>, que responde al instante con lo
/// que haya, y se redibuja cuando <see cref="Ready"/> avisa de que llegó una nueva. Como las
/// copias de edición, se generan de una en una para no competir con el preview.
/// </remarks>
public sealed class WaveformCache : IDisposable
{
    private const int Version = 1;

    private readonly WaveformExtractor _extractor;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly Task _worker;
    private bool _disposed;

    /// <summary>Crea la caché y arranca su hilo de trabajo.</summary>
    public WaveformCache(FFmpegTools tools, string directory)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _extractor = new WaveformExtractor(tools);
        _directory = directory;
        _worker = Task.Run(() => RunAsync(_lifetime.Token));
    }

    /// <summary>Carpeta por defecto, en los datos locales del usuario.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EditFlow",
        "waveforms");

    /// <summary>
    /// Avisa de que la forma de onda de un archivo ya está disponible. Se invoca en un hilo del
    /// pool: quien toque la interfaz debe reenviarlo al suyo.
    /// </summary>
    public event Action<string>? Ready;

    /// <summary>Picos del archivo, o <see langword="null"/> si aún no se han calculado.</summary>
    /// <remarks>Si hay una copia en disco de una sesión anterior, se carga y se devuelve.</remarks>
    public byte[]? TryGet(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (_memory.TryGetValue(sourcePath, out var cached))
        {
            return cached;
        }

        var file = FileFor(sourcePath);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        try
        {
            var loaded = File.ReadAllBytes(file);
            if (loaded.Length == 0)
            {
                return null;
            }

            _memory[sourcePath] = loaded;
            return loaded;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Picos ya cargados en memoria, sin tocar el disco. Es lo que puede llamarse al dibujar.</summary>
    /// <remarks>
    /// <see cref="TryGet"/> mira también en disco, y hacerlo en cada fotograma de la interfaz
    /// para un archivo aún sin calcular sería una comprobación de archivo por cada repintado.
    /// </remarks>
    public byte[]? PeaksIfLoaded(string sourcePath) =>
        _memory.TryGetValue(sourcePath, out var peaks) ? peaks : null;

    /// <summary>Pide la forma de onda de un archivo si aún no la hay ni está en cola.</summary>
    public void Request(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (_disposed || TryGet(sourcePath) is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_requested.Add(sourcePath))
            {
                return;
            }
        }

        _queue.Writer.TryWrite(sourcePath);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre de la aplicación.
        }
    }

    private async Task ProcessAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var peaks = await _extractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false);
            if (peaks.Length == 0)
            {
                return;
            }

            _memory[path] = peaks;
            Save(path, peaks);
            Ready?.Invoke(path);
        }
        catch (InvalidOperationException)
        {
            // Un archivo sin audio legible se queda sin forma de onda; no es un error del
            // usuario y no hay nada que mostrar.
        }
        catch (IOException)
        {
        }
    }

    private void Save(string sourcePath, byte[] peaks)
    {
        var file = FileFor(sourcePath);
        if (file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var temp = file + ".partial";
            File.WriteAllBytes(temp, peaks);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin copia en disco se recalculará la próxima vez; no es motivo de fallo.
        }
    }

    // El nombre sale de la ruta, el tamaño y la fecha del archivo: si el audio cambia, la
    // forma de onda vieja deja de encontrarse. Es un hash para no revelar qué archivos hay.
    private string? FileFor(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            return null;
        }

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Path.Combine(_directory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".peaks");
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
