// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Playback;

namespace EditFlow.Engine.PreviewCache;

/// <summary>
/// Copia de preview: renderiza la timeline por trozos, en segundo plano, para reproducirla
/// después sin decodificar los originales.
/// </summary>
/// <remarks>
/// <para>
/// Cada trozo de unos segundos se renderiza con el mismo grafo que la exportación (video,
/// textos e imágenes ya compuestos) pero a la resolución de preview y con un códec rápido de
/// decodificar. Los archivos son temporales, viven en una carpeta aparte y <b>nunca reemplazan
/// ni tocan los originales</b>. Tampoco intervienen en la exportación, que siempre parte de los
/// archivos originales.
/// </para>
/// <para>
/// Cada trozo se identifica por una huella de lo que lo compone (<see cref="SectionHasher"/>): tras
/// una edición solo los trozos afectados quedan sin copia, y el resto se reutiliza tal cual.
/// </para>
/// </remarks>
public sealed class PreviewCacheManager : IDisposable
{
    // Cuántos trozos consecutivos se abren de una vez como un solo video. Más trozos alargarían
    // el arranque de la reproducción, que abre cada archivo para conocer su duración.
    private const int MaxRunSections = 24;

    private readonly FFmpegTools _tools;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly HashSet<string> _ready = [];
    private readonly HashSet<string> _queued = [];
    private readonly Dictionary<string, DateTime> _lastUsed = [];

    private List<Entry> _entries = [];
    private PreviewCacheSettings? _settings;
    private string? _activeHash;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _activeCancellation;
    private Task? _runTask;
    private TimeSpan _priority;
    private bool _disposed;

    private sealed record Entry(int Index, TimeSpan Start, TimeSpan End, string Hash, EditSequence Slice);

    /// <summary>Crea la copia de preview.</summary>
    /// <param name="tools">Ejecutables de FFmpeg.</param>
    /// <param name="directory">Carpeta donde se guardan los trozos.</param>
    /// <param name="maxBytes">Espacio máximo en disco; al superarlo se borran los trozos menos usados.</param>
    /// <param name="sectionLength">Duración de cada trozo.</param>
    public PreviewCacheManager(
        FFmpegTools tools,
        string directory,
        long maxBytes = 2L * 1024 * 1024 * 1024,
        TimeSpan? sectionLength = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        _tools = tools;
        CacheDirectory = directory;
        _maxBytes = maxBytes;
        SectionLength = sectionLength ?? DefaultSectionLength;
        ArgumentOutOfRangeException.ThrowIfLessThan(SectionLength, TimeSpan.FromSeconds(1));
    }

    /// <summary>Duración por defecto de cada trozo.</summary>
    public static TimeSpan DefaultSectionLength { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Carpeta por defecto, en la carpeta temporal del sistema.</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "editflow-preview-cache");

    /// <summary>Carpeta donde se guardan los trozos.</summary>
    public string CacheDirectory { get; }

    /// <summary>Duración de cada trozo.</summary>
    public TimeSpan SectionLength { get; }

    /// <summary>Se dispara cuando cambia el estado de algún trozo. Puede llegar desde cualquier hilo.</summary>
    public event EventHandler? Changed;

    /// <summary>Último error de renderizado, o <see langword="null"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Trozos de la timeline con su estado actual.</summary>
    public IReadOnlyList<CacheSection> Sections
    {
        get
        {
            lock (_gate)
            {
                return _entries
                    .Select(e => new CacheSection(e.Index, e.Start, e.End, e.Hash, StateOf(e.Hash)))
                    .ToList();
            }
        }
    }

    /// <summary>Recuento de trozos y espacio ocupado.</summary>
    public CacheSummary Summary
    {
        get
        {
            List<string> ready;
            int total;
            int readyCount;
            bool rendering;

            lock (_gate)
            {
                total = _entries.Count;
                readyCount = _entries.Count(e => _ready.Contains(e.Hash));
                ready = _entries.Select(e => e.Hash).Where(_ready.Contains).Distinct().ToList();
                rendering = _runTask is { IsCompleted: false };
            }

            long bytes = 0;
            foreach (var hash in ready)
            {
                try
                {
                    bytes += new FileInfo(PathFor(hash)).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Desapareció entre tanto: no cuenta.
                }
            }

            return new CacheSummary(total, readyCount, total - readyCount, rendering, bytes);
        }
    }

    /// <summary>Indica si hay un renderizado en marcha.</summary>
    public bool IsRendering
    {
        get
        {
            lock (_gate)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>Ruta del archivo de un trozo.</summary>
    public string PathFor(string hash) => Path.Combine(CacheDirectory, hash + ".mp4");

    // ------------------------------------------------------------ estado de los trozos

    /// <summary>
    /// Recalcula los trozos tras una edición (o un cambio de ajustes).
    /// </summary>
    /// <remarks>
    /// Se llama en el hilo que edita la secuencia: cada trozo se copia aquí, así el renderizado
    /// posterior, que corre en otro hilo, nunca lee la secuencia viva. Los trozos cuya huella no
    /// cambió conservan su copia; un trozo que se estaba renderizando y ya no existe se cancela.
    /// </remarks>
    public void Update(EditSequence sequence, PreviewCacheSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var entries = new List<Entry>();

        if (settings is not null && !sequence.Video.IsEmpty)
        {
            var total = sequence.Video.Duration;

            // Un resto de unos milisegundos no merece un trozo propio: se une al anterior.
            var count = Math.Max(1, (int)Math.Ceiling((total - TimeSpan.FromMilliseconds(20)) / SectionLength));
            var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string Stamp(string path)
            {
                if (!stamps.TryGetValue(path, out var stamp))
                {
                    stamps[path] = stamp = SectionHasher.StampOf(path);
                }

                return stamp;
            }

            for (var i = 0; i < count; i++)
            {
                var start = SectionLength * i;
                var end = i == count - 1 ? total : start + SectionLength;
                var slice = SequenceSlicer.Slice(sequence, start, end);

                if (!slice.Video.IsEmpty)
                {
                    entries.Add(new Entry(i, start, end, SectionHasher.Compute(slice, settings, Stamp), slice));
                }
            }
        }

        lock (_gate)
        {
            _settings = settings;
            _entries = entries;

            var live = entries.Select(e => e.Hash).ToHashSet();
            _queued.RemoveWhere(h => !live.Contains(h));

            _ready.Clear();
            foreach (var hash in live)
            {
                if (File.Exists(PathFor(hash)))
                {
                    _ready.Add(hash);
                }
            }

            if (_activeHash is not null && !live.Contains(_activeHash))
            {
                _activeCancellation?.Cancel();
            }
        }

        RaiseChanged();
    }

    private SectionState StateOf(string hash)
    {
        if (hash == _activeHash)
        {
            return SectionState.Rendering;
        }

        if (_ready.Contains(hash))
        {
            return SectionState.Ready;
        }

        return _queued.Contains(hash) ? SectionState.Queued : SectionState.NeedsRender;
    }

    // -------------------------------------------------------------------- reproducción

    /// <summary>
    /// Busca los trozos listos que empiezan en el instante dado, para reproducirlos seguidos.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> si el trozo que contiene el instante no tiene copia lista: en ese
    /// caso se reproduce decodificando el original.
    /// </returns>
    public CacheRun? FindRun(TimeSpan position)
    {
        lock (_gate)
        {
            if (_settings is null)
            {
                return null;
            }

            var first = _entries.FindIndex(e => position >= e.Start && position < e.End);
            if (first < 0)
            {
                return null;
            }

            var paths = new List<string>();
            var last = first;

            for (var i = first; i < _entries.Count && i - first < MaxRunSections; i++)
            {
                var path = PathFor(_entries[i].Hash);

                // Si alguien borró la copia, deja de contar como lista.
                if (!_ready.Contains(_entries[i].Hash) || !File.Exists(path))
                {
                    _ready.Remove(_entries[i].Hash);
                    break;
                }

                paths.Add(path);
                last = i;
                _lastUsed[_entries[i].Hash] = DateTime.UtcNow;
            }

            if (paths.Count == 0)
            {
                return null;
            }

            return new CacheRun(_entries[first].Start, _entries[last].End, WriteRunList(paths), _settings);
        }
    }

    private string WriteRunList(List<string> paths)
    {
        var text = new StringBuilder("ffconcat version 1.0\n");
        foreach (var path in paths)
        {
            // Rutas con barras normales y las comillas simples escapadas: es lo que el formato admite.
            var escaped = path.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
            text.Append("file '").Append(escaped).Append("'\n");
        }

        var name = "run-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 10)
            .ToLowerInvariant() + FrameReader.ConcatListExtension;
        var listPath = Path.Combine(CacheDirectory, name);

        if (!File.Exists(listPath))
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(listPath, text.ToString(), new UTF8Encoding(false));
        }

        return listPath;
    }

    // ------------------------------------------------------------------- renderizado

    /// <summary>
    /// Pone en cola todos los trozos sin copia y empieza a renderizarlos, desde el más cercano
    /// al instante indicado hacia delante.
    /// </summary>
    public void StartRender(TimeSpan priority)
    {
        lock (_gate)
        {
            if (_settings is null || _disposed)
            {
                return;
            }

            _priority = priority;
            LastError = null;

            foreach (var entry in _entries)
            {
                if (!_ready.Contains(entry.Hash))
                {
                    _queued.Add(entry.Hash);
                }
            }

            if (_runTask is not { IsCompleted: false })
            {
                _runCancellation = new CancellationTokenSource();
                var token = _runCancellation.Token;
                _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }

        RaiseChanged();
    }

    /// <summary>Detiene el renderizado en curso; lo ya renderizado se conserva.</summary>
    public void CancelRender()
    {
        lock (_gate)
        {
            _runCancellation?.Cancel();
            _queued.Clear();
        }

        RaiseChanged();
    }

    /// <summary>Espera a que termine el renderizado en curso, si lo hay.</summary>
    public async Task WaitForRenderAsync()
    {
        Task? running;
        lock (_gate)
        {
            running = _runTask;
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                Entry? next;
                PreviewCacheSettings? settings;
                CancellationToken sectionToken;

                lock (_gate)
                {
                    settings = _settings;
                    next = PickNext();
                    if (next is null || settings is null)
                    {
                        break;
                    }

                    _activeHash = next.Hash;
                    _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    sectionToken = _activeCancellation.Token;
                }

                RaiseChanged();

                try
                {
                    await RenderSectionAsync(next, settings, sectionToken).ConfigureAwait(false);

                    lock (_gate)
                    {
                        _ready.Add(next.Hash);
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // La edición dejó obsoleto este trozo: se sigue con el siguiente.
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    LastError = ex.Message;
                }
                finally
                {
                    lock (_gate)
                    {
                        _queued.Remove(next.Hash);
                        _activeHash = null;
                        _activeCancellation?.Dispose();
                        _activeCancellation = null;
                    }

                    RaiseChanged();
                }

                EvictOverflow();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado por el usuario.
        }
        finally
        {
            lock (_gate)
            {
                _queued.Clear();
                _activeHash = null;
            }

            RaiseChanged();
        }
    }

    /// <summary>El siguiente trozo en cola: el primero desde el cabezal hacia delante, y después los anteriores.</summary>
    private Entry? PickNext()
    {
        Entry? before = null;

        foreach (var entry in _entries)
        {
            if (!_queued.Contains(entry.Hash) || _ready.Contains(entry.Hash))
            {
                continue;
            }

            if (entry.End > _priority)
            {
                return entry;
            }

            before ??= entry;
        }

        return before;
    }

    private async Task RenderSectionAsync(Entry entry, PreviewCacheSettings settings, CancellationToken cancellation)
    {
        Directory.CreateDirectory(CacheDirectory);

        var partial = Path.Combine(CacheDirectory, entry.Hash + ".part.mp4");
        var final = PathFor(entry.Hash);

        // Un códec ligero de decodificar y sin audio: la copia solo sirve para ver, y el sonido
        // sale de la mezcla de audio del preview.
        var export = new ExportSettings
        {
            OutputPath = partial,
            Resolution = new VideoResolution(settings.Width, settings.Height, "preview"),
            EncoderName = "libx264",
            FrameRate = settings.FrameRate,
            RateControl = RateControlMode.ConstantQuality,
            Quality = 70,
            Speed = EncodingSpeed.Fastest,
            IncludeAudio = false,
            OptimizeForStreaming = false,
        };

        var result = await new ExportJob(_tools)
            .RunAsync(entry.Slice, export, progress: null, cancellation)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "No se pudo renderizar el preview.");
        }

        File.Move(partial, final, overwrite: true);
    }

    // ------------------------------------------------------------------ mantenimiento

    /// <summary>Borra los trozos menos usados cuando la carpeta supera el espacio máximo.</summary>
    private void EvictOverflow()
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.mp4")
                .Where(f => !f.Name.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= _maxBytes)
            {
                return;
            }

            HashSet<string> current;
            lock (_gate)
            {
                current = _entries.Select(e => e.Hash).ToHashSet();
            }

            // Primero lo que ya no forma parte del montaje; después, lo menos usado.
            var order = files
                .OrderBy(f => current.Contains(Path.GetFileNameWithoutExtension(f.Name)) ? 1 : 0)
                .ThenBy(LastUse);

            foreach (var file in order)
            {
                if (total <= _maxBytes)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;

                    lock (_gate)
                    {
                        _ready.Remove(Path.GetFileNameWithoutExtension(file.Name));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // En uso por el reproductor: se borrará en otra ocasión.
                }
            }

            RaiseChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La carpeta desapareció o no se puede leer: no hay nada que limpiar.
        }
    }

    private DateTime LastUse(FileInfo file)
    {
        lock (_gate)
        {
            return _lastUsed.TryGetValue(Path.GetFileNameWithoutExtension(file.Name), out var used)
                ? used
                : file.LastWriteTimeUtc;
        }
    }

    /// <summary>Borra todas las copias. Detiene antes cualquier renderizado en curso.</summary>
    public void Clear()
    {
        CancelRender();

        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            foreach (var file in new DirectoryInfo(CacheDirectory).GetFiles())
            {
                try
                {
                    file.Delete();

                    lock (_gate)
                    {
                        _ready.Remove(Path.GetFileNameWithoutExtension(file.Name));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // En uso: se queda.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nada que hacer.
        }

        RaiseChanged();
    }

    /// <summary>Borra lo que lleva mucho tiempo sin tocarse: restos de sesiones anteriores.</summary>
    public void DeleteStale(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            var limit = DateTime.UtcNow - maxAge;
            foreach (var file in new DirectoryInfo(CacheDirectory).GetFiles().Where(f => f.LastWriteTimeUtc < limit))
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // En uso.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nada que hacer.
        }
    }

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Task? running;
        lock (_gate)
        {
            _disposed = true;
            _runCancellation?.Cancel();
            running = _runTask;
        }

        try
        {
            running?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Terminó cancelado.
        }
    }
}
