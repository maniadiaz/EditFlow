// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace EditFlow.Engine.Playback;

/// <summary>
/// Reproduce video decodificándolo y entregando los fotogramas a su debido tiempo.
/// </summary>
/// <remarks>
/// <para>
/// El decodificador va muy por delante —742 fotogramas por segundo medidos a 480p, casi
/// 25 veces el tiempo real—, así que el trabajo de esta clase no es correr, sino
/// <b>frenar</b>: entregar cada fotograma en su instante y no antes.
/// </para>
/// <para>
/// El ritmo se mide contra un cronómetro y no contando esperas. Encadenar pausas de
/// 33 ms acumula el error de cada una, y un minuto de reproducción acaba desviado varios
/// segundos del audio.
/// </para>
/// </remarks>
public sealed class VideoPlayer : IDisposable
{
    private readonly FFmpegTools _tools;
    private readonly int _bufferedFrames;
    private readonly Lock _gate = new();

    // El formato de decodificación puede cambiar mientras la aplicación corre: al redimensionar la
    // ventana, al pasar a un clip de otra velocidad de fotogramas. Solo se aplica al abrir de
    // nuevo, cuando no hay ningún lector vivo que use el búfer anterior.
    private int _width;
    private int _height;
    private double _frameRate;
    private bool _hardware;
    private bool _hardwareFailed;
    private FramePool _pool;
    private (int Width, int Height, double FrameRate, bool Hardware) _requested;

    private FrameReader? _reader;
    private CancellationTokenSource? _decoding;
    private Task _decodeTask = Task.CompletedTask;
    private volatile bool _paused = true;

    // Abrir, saltar y arrastrar comparten el lector y el bucle de decodificación: si dos de
    // esas operaciones corrieran a la vez, una cerraría el lector mientras la otra lo crea.
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Lock _scrubGate = new();
    private (string Path, TimeSpan Position)? _scrubTarget;
    private volatile bool _wantPlay;
    private bool _scrubbing;
    private long _delivered;

    private string? _path;
    private TimeSpan _origin;
    private TimeSpan _position;
    private bool _disposed;

    /// <summary>Crea un reproductor que decodifica al tamaño indicado.</summary>
    /// <param name="tools">Ejecutables de FFmpeg.</param>
    /// <param name="width">Ancho de decodificación.</param>
    /// <param name="height">Alto de decodificación.</param>
    /// <param name="frameRate">Fotogramas por segundo de reproducción.</param>
    /// <param name="bufferedFrames">
    /// Cuántos fotogramas pueden estar en circulación. Cada uno de 854×480 ocupa 1,6 MB.
    /// </param>
    public VideoPlayer(
        FFmpegTools tools,
        int width = 854,
        int height = 480,
        double frameRate = 30,
        int bufferedFrames = 4)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _tools = tools;
        _bufferedFrames = bufferedFrames;
        _width = width;
        _height = height;
        _frameRate = frameRate;
        _pool = new FramePool(bufferedFrames, width, height);
        _requested = (width, height, frameRate, false);
    }

    /// <summary>Archivo abierto ahora, o <see langword="null"/>.</summary>
    public string? CurrentPath => _path;

    /// <summary>
    /// Pide otro formato de decodificación para las próximas aperturas.
    /// </summary>
    /// <param name="width">Ancho de los fotogramas.</param>
    /// <param name="height">Alto de los fotogramas.</param>
    /// <param name="frameRate">Fotogramas por segundo; lo natural es el del propio video.</param>
    /// <param name="hardwareDecoding">Si se debe intentar decodificar con la tarjeta gráfica.</param>
    /// <remarks>
    /// No afecta a lo que ya está decodificándose: se aplica al siguiente <c>Scrub</c> o
    /// <see cref="OpenAsync"/>. Cambiar el tamaño de los búferes con un lector vivo escribiría
    /// fotogramas de un tamaño en búferes de otro.
    /// </remarks>
    public void Configure(int width, int height, double frameRate, bool hardwareDecoding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frameRate, 0);

        lock (_gate)
        {
            _requested = (width, height, frameRate, hardwareDecoding);
        }
    }

    /// <summary>
    /// Filtros de color que se aplican a lo que se abra a partir de ahora, o <see langword="null"/> para ninguno.
    /// </summary>
    /// <remarks>Como el resto de la configuración, se aplica al abrir el siguiente archivo o salto.</remarks>
    public string? ColorFilter { get; set; }

    /// <summary>
    /// Filtros de encuadre (zoom, posición, rotación) que se aplican a lo que se abra a partir de
    /// ahora, o <see langword="null"/> para ninguno.
    /// </summary>
    /// <remarks>Como el resto de la configuración, se aplica al abrir el siguiente archivo o salto.</remarks>
    public string? TransformFilter { get; set; }

    /// <summary>
    /// Recorte por color (pantalla verde) que se aplica a lo que se abra a partir de ahora, o
    /// <see langword="null"/> para ninguno.
    /// </summary>
    /// <remarks>
    /// Solo lo usa una capa superpuesta: recortar el fondo del video principal dejaría ver el negro
    /// del lienzo, no otra imagen.
    /// </remarks>
    public string? KeyFilter { get; set; }

    /// <summary>
    /// Se invoca con cada fotograma que toca mostrar.
    /// </summary>
    /// <remarks>
    /// Corre en el hilo de decodificación, no en el de interfaz. El fotograma solo es
    /// válido durante la llamada: al volver se recicla, así que quien lo reciba debe
    /// copiarlo si necesita conservarlo.
    /// </remarks>
    public Action<VideoFrame>? FrameReady { get; set; }

    /// <summary>Se invoca al llegar al final del archivo.</summary>
    public Action? Ended { get; set; }

    /// <summary>
    /// Reloj al que sincronizarse. Si es <see langword="null"/>, el reproductor marca su
    /// propio ritmo con un cronómetro.
    /// </summary>
    /// <remarks>
    /// Cuando hay audio, manda el audio y el video lo sigue. Un fotograma repetido o
    /// saltado pasa desapercibido; un corte en el audio se oye siempre. Si el video
    /// llevara el reloj habría que estirar o encoger el audio para seguirlo, y eso se nota.
    /// </remarks>
    public Func<TimeSpan>? MasterClock { get; set; }

    /// <summary>Fotogramas descartados por llegar tarde respecto al reloj maestro.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>
    /// Retraso a partir del cual un fotograma se descarta en lugar de mostrarse.
    /// </summary>
    /// <remarks>
    /// Mostrar un fotograma muy atrasado no recupera la sincronía: la empeora, porque el
    /// siguiente llegará aún más tarde. Descartarlo permite alcanzar al audio.
    /// </remarks>
    private static readonly TimeSpan MaximumLag = TimeSpan.FromMilliseconds(120);

    /// <summary>Indica si está reproduciendo.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Instante del último fotograma entregado.</summary>
    public TimeSpan Position
    {
        get { lock (_gate) { return _position; } }
    }

    /// <summary>Memoria que ocupan los fotogramas en circulación.</summary>
    public long BufferBytes => _pool.MemoryBytes;

    /// <summary>Abre un archivo y se sitúa en una posición, en pausa.</summary>
    public async Task OpenAsync(string path, TimeSpan position, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Una petición de arrastre pendiente se refería al archivo anterior.
        lock (_scrubGate)
        {
            _scrubTarget = null;
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await OpenCoreAsync(path, position).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Muestra lo que hay en una posición de un archivo, descartando peticiones anteriores no
    /// atendidas: gana siempre la última.
    /// </summary>
    /// <param name="path">Archivo, que puede ser el que ya está abierto u otro.</param>
    /// <param name="position">Instante a mostrar.</param>
    /// <remarks>
    /// <para>
    /// Si se estaba reproduciendo —o se pide reproducir mientras se atiende—, sigue reproduciendo
    /// al llegar: lo que cuenta es la última orden de <see cref="Play"/> o <see cref="Pause"/>.
    /// </para>
    /// <para>
    /// Arrastrar el cabezal pide una posición nueva decenas de veces por segundo, y cada una
    /// obliga a arrancar un FFmpeg. Atender todas las peticiones las encolaría: el video iría
    /// cada vez más retrasado respecto al ratón, mostrando lugares por los que ya se pasó. Aquí
    /// se atiende una a la vez y, mientras se atiende, solo se recuerda la más reciente.
    /// </para>
    /// <para>
    /// Retorna al momento: el trabajo ocurre en segundo plano.
    /// </para>
    /// </remarks>
    public void Scrub(string path, TimeSpan position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_disposed)
        {
            return;
        }

        lock (_scrubGate)
        {
            _scrubTarget = (path, position);

            if (_scrubbing)
            {
                return;
            }

            _scrubbing = true;
        }

        _ = Task.Run(ScrubLoopAsync);
    }

    private async Task ScrubLoopAsync()
    {
        try
        {
            while (!_disposed)
            {
                (string Path, TimeSpan Position) target;

                lock (_scrubGate)
                {
                    if (_scrubTarget is not { } next)
                    {
                        return;
                    }

                    target = next;
                    _scrubTarget = null;
                }

                await _operation.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var before = Interlocked.Read(ref _delivered);
                    await OpenCoreAsync(target.Path, target.Position).ConfigureAwait(false);

                    if (_wantPlay)
                    {
                        Play();
                    }

                    // Se espera a que llegue el primer fotograma antes de atender la siguiente
                    // petición: arrancar otro lector antes lo mataría sin haber mostrado nada, y
                    // arrastrando deprisa la imagen no se actualizaría nunca.
                    var waited = Stopwatch.StartNew();
                    while (Interlocked.Read(ref _delivered) == before && waited.ElapsedMilliseconds < 800 && !_disposed)
                    {
                        await Task.Delay(3).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _operation.Release();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Se cerró el reproductor mientras se atendía una petición.
        }
        finally
        {
            lock (_scrubGate)
            {
                _scrubbing = false;

                // Una petición llegada justo al terminar no debe quedarse sin atender.
                if (_scrubTarget is not null && !_disposed)
                {
                    _scrubbing = true;
                    _ = Task.Run(ScrubLoopAsync);
                }
            }
        }
    }

    private async Task OpenCoreAsync(string path, TimeSpan position)
    {
        await StopDecodingAsync().ConfigureAwait(false);
        ApplyRequestedFormat();

        _path = path;
        _origin = position < TimeSpan.Zero ? TimeSpan.Zero : position;

        lock (_gate)
        {
            _position = _origin;
        }

        StartDecoding(playing: false);

        // Un solo fotograma para que la imagen no quede en negro mientras está en pausa.
        await Task.Yield();
    }

    /// <summary>Salta a otro instante del mismo archivo.</summary>
    /// <remarks>
    /// Un salto obliga a crear otro lector, y arrancar FFmpeg cuesta 43 ms. Es aceptable
    /// para un salto puntual, pero no para arrastrar el cabezal fotograma a fotograma:
    /// ese caso necesitará un búfer de fotogramas alrededor de la posición.
    /// </remarks>
    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (_path is null)
        {
            return;
        }

        var wasPlaying = IsPlaying;
        await OpenAsync(_path, position, cancellationToken).ConfigureAwait(false);

        if (wasPlaying)
        {
            Play();
        }
    }

    /// <summary>Reanuda la reproducción.</summary>
    /// <summary>Fotogramas entregados desde que se creó el reproductor. Sirve para saber si ya llegó uno nuevo.</summary>
    public long FramesDelivered => Interlocked.Read(ref _delivered);

    /// <summary>Reanuda la reproducción.</summary>
    public void Play()
    {
        _wantPlay = true;

        if (_disposed || _path is null)
        {
            return;
        }

        IsPlaying = true;
        _paused = false;
    }

    /// <summary>Detiene la reproducción sin perder la posición.</summary>
    public void Pause()
    {
        _wantPlay = false;
        IsPlaying = false;
        _paused = true;
    }

    private void ApplyRequestedFormat()
    {
        (int Width, int Height, double FrameRate, bool Hardware) wanted;
        lock (_gate)
        {
            wanted = _requested;
        }

        _frameRate = wanted.FrameRate;
        _hardware = wanted.Hardware;

        if (wanted.Width == _width && wanted.Height == _height)
        {
            return;
        }

        // Sin lector vivo (se acaba de detener), nadie usa ya los fotogramas del búfer anterior.
        var old = _pool;
        _pool = new FramePool(_bufferedFrames, wanted.Width, wanted.Height);
        _width = wanted.Width;
        _height = wanted.Height;
        old.Dispose();
    }

    private void StartDecoding(bool playing)
    {
        var token = new CancellationTokenSource();
        _decoding = token;

        IsPlaying = playing;
        _paused = !playing;

        var path = _path!;
        var origin = _origin;

        // Se fija el formato de esta ejecución: el bucle no debe leer campos que otra apertura
        // podría cambiar mientras él corre.
        var format = (Width: _width, Height: _height, FrameRate: _frameRate, Pool: _pool,
            Hardware: _hardware && !_hardwareFailed);

        _decodeTask = Task.Run(() => DecodeWithFallbackAsync(path, origin, format, token.Token), token.Token);
    }

    // Decodificar por hardware falla en más casos de los que parece —códecs sin soporte, 10 bits,
    // controladores antiguos—. Si el intento por hardware no da ni un fotograma, se repite por
    // software y se recuerda para no volver a intentarlo en esta sesión.
    private async Task DecodeWithFallbackAsync(
        string path,
        TimeSpan origin,
        (int Width, int Height, double FrameRate, FramePool Pool, bool Hardware) format,
        CancellationToken cancellationToken)
    {
        var retryWithoutHardware = await DecodeLoopAsync(path, origin, format, cancellationToken).ConfigureAwait(false);

        if (retryWithoutHardware && !cancellationToken.IsCancellationRequested)
        {
            _hardwareFailed = true;
            await DecodeLoopAsync(path, origin, format with { Hardware = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <returns><see langword="true"/> si la decodificación por hardware terminó sin dar ningún fotograma.</returns>
    private async Task<bool> DecodeLoopAsync(
        string path,
        TimeSpan origin,
        (int Width, int Height, double FrameRate, FramePool Pool, bool Hardware) format,
        CancellationToken cancellationToken)
    {
        FrameReader? reader = null;
        var frameRate = format.FrameRate;
        var pool = format.Pool;
        var producedAny = false;

        try
        {
            reader = new FrameReader(
                _tools, path, origin, format.Width, format.Height, frameRate, format.Hardware, ColorFilter, TransformFilter, KeyFilter);
            _reader = reader;

            var clock = new Stopwatch();
            var delivered = 0L;
            var first = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await pool.RentAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    if (!await reader.ReadIntoAsync(frame, cancellationToken).ConfigureAwait(false))
                    {
                        if (format.Hardware && !producedAny)
                        {
                            return true;
                        }

                        Ended?.Invoke();
                        return false;
                    }

                    producedAny = true;

                    if (first)
                    {
                        // El primer fotograma se muestra siempre, aunque esté en pausa:
                        // así el preview no queda en negro al abrir o al saltar.
                        first = false;
                        Deliver(frame);
                        continue;
                    }

                    // En pausa se espera de forma asíncrona, no bloqueando el hilo.
                    // Una espera bloqueante dentro de un bucle asíncrono es una fuente
                    // clásica de bloqueos al cerrar: el hilo queda detenido y no atiende
                    // la cancelación, así que nadie puede liberarlo.
                    while (_paused && !cancellationToken.IsCancellationRequested)
                    {
                        clock.Reset();
                        await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (MasterClock is { } master)
                    {
                        var lead = frame.Timestamp - master();

                        if (lead < -MaximumLag)
                        {
                            // Demasiado tarde: mostrarlo no recupera la sincronía, la
                            // empeora. Se descarta para alcanzar al audio.
                            DroppedFrames++;
                            continue;
                        }

                        // Se consulta el reloj en cada vuelta en lugar de esperar el
                        // retraso completo de una vez: el audio puede acelerarse o
                        // frenar, y una espera larga no se enteraría.
                        while (lead > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
                        {
                            var slice = lead > TimeSpan.FromMilliseconds(20)
                                ? TimeSpan.FromMilliseconds(20)
                                : lead;

                            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
                            lead = frame.Timestamp - master();
                        }
                    }
                    else
                    {
                        if (!clock.IsRunning)
                        {
                            clock.Restart();
                            delivered = 0;
                        }

                        // Contra el cronómetro, no encadenando esperas: encadenarlas
                        // acumula el error de cada una y la imagen se desvía.
                        delivered++;
                        var due = TimeSpan.FromSeconds(delivered / frameRate);
                        var wait = due - clock.Elapsed;

                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    Deliver(frame);
                }
                finally
                {
                    pool.Return(frame);
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Quien detiene la reproducción cierra el lector para desbloquear una lectura en
            // curso; si el bucle estaba justo entre lecturas, lo encuentra ya cerrado.
            // Es la misma parada normal, no un fallo.
        }
        finally
        {
            reader?.Dispose();
            if (ReferenceEquals(_reader, reader))
            {
                _reader = null;
            }
        }

        return false;
    }

    private void Deliver(VideoFrame frame)
    {
        lock (_gate)
        {
            _position = frame.Timestamp;
        }

        Interlocked.Increment(ref _delivered);
        FrameReady?.Invoke(frame);
    }

    private async Task StopDecodingAsync()
    {
        var token = _decoding;
        if (token is null)
        {
            return;
        }

        IsPlaying = false;
        _paused = false;

        // Primero se cancela y después se cierra el lector. En ese orden, el bucle que
        // encuentre el lector cerrado ya sabe que es por una parada y no por un fallo; al
        // revés, un salto durante una carga alta de CPU dejaba el reproductor sin decodificador.
        await token.CancelAsync().ConfigureAwait(false);

        // Cerrar el lector es lo que de verdad desbloquea el bucle. Una lectura en curso
        // sobre la tubería de un proceso no atiende la cancelación en Windows: solo
        // termina cuando la tubería se cierra, y eso ocurre al matar FFmpeg.
        _reader?.Dispose();

        try
        {
            await _decodeTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Esperado.
        }
        catch (TimeoutException)
        {
            // El bucle no respondió. El proceso ya está muerto y la reserva se libera
            // igualmente, así que seguir adelante es preferible a colgar la aplicación.
        }

        token.Dispose();
        _decoding = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Nada de esperar aquí: bloquear el hilo llamante para aguardar a una tarea
        // asíncrona es la otra receta habitual de bloqueo. Se cancela, se mata el
        // proceso y el bucle termina por su cuenta.
        _decoding?.Cancel();
        _reader?.Dispose();
        _pool.Dispose();
    }
}
