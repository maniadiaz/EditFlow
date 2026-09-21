// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Playback;

/// <summary>
/// Un fotograma decodificado en memoria, en formato BGRA de 8 bits por canal.
/// </summary>
/// <remarks>
/// <para>
/// El formato es BGRA y no BGR de 24 bits por una razón medida: con BGR24 hay que
/// convertir cada fotograma antes de dibujarlo, y esa conversión por sí sola hunde un
/// video de 30 fps por debajo de 10. BGRA es lo que el mapa de bits de Avalonia consume
/// directamente, así que el fotograma pasa de FFmpeg a la pantalla sin tocarse.
/// </para>
/// <para>
/// Un fotograma de 854×480 ocupa 1,6 MB. A 30 por segundo eso son 48 MB/s de basura
/// para el recolector si se asignan y descartan; por eso los búferes se reutilizan
/// mediante <see cref="FramePool"/> en lugar de crearse nuevos.
/// </para>
/// </remarks>
public sealed class VideoFrame
{
    internal VideoFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    /// <summary>Píxeles en BGRA, de arriba a abajo, sin relleno entre filas.</summary>
    public byte[] Pixels { get; }

    /// <summary>Ancho en píxeles.</summary>
    public int Width { get; }

    /// <summary>Alto en píxeles.</summary>
    public int Height { get; }

    /// <summary>Bytes por fila.</summary>
    public int Stride => Width * 4;

    /// <summary>Instante del archivo origen al que corresponde este fotograma.</summary>
    public TimeSpan Timestamp { get; internal set; }

    /// <summary>Indica si el contenido es válido; falso mientras el búfer está libre.</summary>
    public bool IsValid { get; internal set; }
}

/// <summary>
/// Reserva de fotogramas reutilizables de un tamaño fijo.
/// </summary>
/// <remarks>
/// Todos los fotogramas de una reproducción tienen las mismas dimensiones, así que un
/// conjunto fijo de búferes evita por completo las asignaciones durante la reproducción.
/// Si se piden más de los que hay, se espera a que se devuelva uno: eso frena al
/// decodificador en lugar de dejar que se coma la memoria decodificando por delante.
/// </remarks>
public sealed class FramePool : IDisposable
{
    private readonly Stack<VideoFrame> _free = new();
    private readonly SemaphoreSlim _available;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Crea una reserva con un número fijo de fotogramas.</summary>
    /// <param name="capacity">Cuántos fotogramas pueden estar en circulación a la vez.</param>
    /// <param name="width">Ancho de cada fotograma.</param>
    /// <param name="height">Alto de cada fotograma.</param>
    public FramePool(int capacity, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Capacity = capacity;
        Width = width;
        Height = height;

        for (var i = 0; i < capacity; i++)
        {
            _free.Push(new VideoFrame(width, height));
        }

        _available = new SemaphoreSlim(capacity, capacity);
    }

    /// <summary>Número de fotogramas de la reserva.</summary>
    public int Capacity { get; }

    /// <summary>Ancho de los fotogramas.</summary>
    public int Width { get; }

    /// <summary>Alto de los fotogramas.</summary>
    public int Height { get; }

    /// <summary>Memoria total ocupada por la reserva, en bytes.</summary>
    public long MemoryBytes => (long)Capacity * Width * Height * 4;

    /// <summary>Toma un fotograma, esperando si todos están en uso.</summary>
    public async Task<VideoFrame> RentAsync(CancellationToken cancellationToken = default)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var frame = _free.Pop();
            frame.IsValid = false;
            return frame;
        }
    }

    /// <summary>Devuelve un fotograma a la reserva.</summary>
    public void Return(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            frame.IsValid = false;
            _free.Push(frame);
        }

        _available.Release();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _available.Dispose();
    }
}
