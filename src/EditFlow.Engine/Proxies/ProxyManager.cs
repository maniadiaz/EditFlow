// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Channels;
using EditFlow.Core.Media;

namespace EditFlow.Engine.Proxies;

/// <summary>Fases por las que pasa la copia de edición de un medio.</summary>
public enum ProxyState
{
    /// <summary>Empieza a generarse.</summary>
    Started,

    /// <summary>Avanza.</summary>
    Progress,

    /// <summary>Ya se puede usar.</summary>
    Ready,

    /// <summary>No se pudo generar; se seguirá usando el original.</summary>
    Failed,
}

/// <summary>Novedad sobre la generación de copias.</summary>
/// <param name="State">Qué ocurrió.</param>
/// <param name="SourcePath">Original al que se refiere.</param>
/// <param name="Percentage">Avance de la copia en curso, de 0 a 100.</param>
/// <param name="Pending">Cuántos medios quedan por procesar, contando el actual.</param>
/// <param name="Error">Motivo, si falló.</param>
public sealed record ProxyUpdate(
    ProxyState State,
    string SourcePath,
    double Percentage,
    int Pending,
    string? Error = null);

/// <summary>
/// Genera las copias de edición en segundo plano, de una en una, y dice cuál usar para cada medio.
/// </summary>
/// <remarks>
/// <para>
/// De una en una a propósito: en un equipo de 8 GB, dos codificaciones simultáneas de 4K
/// dejarían sin memoria ni CPU a la propia edición. La cola sigue el orden de llegada, que es
/// el orden en que el usuario importa o abre los clips.
/// </para>
/// <para>
/// Mientras una copia no está lista, <see cref="Resolve"/> devuelve el original y todo
/// funciona, solo que menos fluido. La copia mejora la edición, nunca la bloquea.
/// </para>
/// </remarks>
public sealed class ProxyManager : IDisposable
{
    private readonly ProxyGenerator _generator;
    private readonly ProxyCache _cache;
    private readonly Channel<MediaInfo> _queue = Channel.CreateUnbounded<MediaInfo>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;

    private int _pending;
    private bool _disposed;

    /// <summary>Crea el gestor y arranca su hilo de trabajo.</summary>
    public ProxyManager(FFmpegTools tools, ProxyCache cache)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(cache);

        _generator = new ProxyGenerator(tools);
        _cache = cache;
        _worker = Task.Run(() => RunAsync(_lifetime.Token));
    }

    /// <summary>
    /// Novedades de generación. Se invoca en un hilo del pool: quien toque la interfaz debe
    /// reenviarlo a su propio hilo.
    /// </summary>
    public event Action<ProxyUpdate>? Updated;

    /// <summary>Medios pendientes de procesar, contando el que se está generando.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Ruta que debe usarse para mostrar un medio: su copia si existe, si no el original.</summary>
    public string Resolve(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (_cache.TryGet(sourcePath, out var proxy))
        {
            _cache.Touch(proxy);
            return proxy;
        }

        return sourcePath;
    }

    /// <summary>
    /// Pide la copia de un medio si le conviene y aún no la tiene ni está en cola.
    /// </summary>
    /// <returns><see langword="true"/> si se puso en cola.</returns>
    public bool Request(MediaInfo media)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (_disposed || !ProxyPolicy.NeedsProxy(media) || _cache.TryGet(media.Path, out _))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_known.Add(media.Path))
            {
                return false;
            }
        }

        Interlocked.Increment(ref _pending);
        _queue.Writer.TryWrite(media);
        return true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var media in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessAsync(media, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre de la aplicación.
        }
    }

    private async Task ProcessAsync(MediaInfo media, CancellationToken cancellationToken)
    {
        var output = _cache.PathFor(media.Path);
        string? error = null;

        if (output is null)
        {
            // El original desapareció mientras esperaba en la cola.
            error = "El archivo original ya no existe.";
        }
        else
        {
            Raise(ProxyState.Started, media.Path, 0);

            try
            {
                var last = -1.0;
                await _generator.GenerateAsync(
                    media.Path,
                    output,
                    media.Duration,
                    percentage =>
                    {
                        // Un aviso por punto porcentual basta; cientos por segundo solo
                        // llenarían de trabajo al hilo de interfaz.
                        if (percentage - last >= 1 || percentage >= 100)
                        {
                            last = percentage;
                            Raise(ProxyState.Progress, media.Path, percentage);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message;
            }
            catch (IOException ex)
            {
                error = ex.Message;
            }
        }

        // Ready y Failed cierran el medio: el contador que se comunica ya no lo incluye.
        Interlocked.Decrement(ref _pending);

        Raise(
            error is null ? ProxyState.Ready : ProxyState.Failed,
            media.Path,
            error is null ? 100 : 0,
            error);
    }

    private void Raise(ProxyState state, string source, double percentage, string? error = null) =>
        Updated?.Invoke(new ProxyUpdate(state, source, percentage, Volatile.Read(ref _pending), error));

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
            // El trabajo ya estaba cancelado.
        }

        _lifetime.Dispose();
    }
}
