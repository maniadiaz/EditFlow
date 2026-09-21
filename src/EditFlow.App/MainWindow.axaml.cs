// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Probing;
using EditFlow.App.Playback;
using EditFlow.Engine.Playback;
using EditFlow.Engine.Proxies;

#if DEBUG
using Avalonia;
#endif

namespace EditFlow.App;

/// <summary>Ventana principal de EditFlow.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Una Window de Avalonia no se desecha por contrato: su ciclo de vida " +
                    "lo marca el evento Closing, donde se liberan LibVLC y el reproductor.")]
public partial class MainWindow : Window
{
    /// <summary>Saltos de los botones de retroceso y avance.</summary>
    /// <summary>Espacio máximo que ocupan las copias de edición antes de borrar las menos usadas.</summary>
    private const long ProxyCacheLimitBytes = 5L * 1024 * 1024 * 1024;

    private static readonly TimeSpan SmallJump = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LargeJump = TimeSpan.FromSeconds(30);

    private readonly UndoHistory _history = new();
    private readonly DispatcherTimer _positionTimer;
    private ProjectSession _session = null!;

    private FFmpegTools? _tools;
    private FFprobeService? _probe;
    private AudioClock? _audio;
    private VideoPlayer? _video;
    private ProxyManager? _proxies;
    private IReadOnlyList<EncoderInfo> _encoders = [];

    // Clip que el reproductor tiene cargado ahora mismo, y dónde empieza en la timeline.
    private Clip? _playingClip;
    private TimeSpan _playingClipStart;


    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif

        _session = new ProjectSession(this);
        _session.ProjectReplaced += (_, _) => OnProjectReplaced();
        _session.StateChanged += (_, _) => RefreshTitle();

        Timeline.UndoHistory = _history;
        Timeline.TimelineEdited += (_, _) => OnTimelineEdited();
        Timeline.PlayheadMoved += (_, position) => SeekTo(position);
        Timeline.SelectionChanged += (_, _) => ShowSelectedClip();

        NewProjectButton.Click += (_, _) => Apply(_session.New());
        OpenProjectButton.Click += async (_, _) => Apply(await _session.OpenAsync(CancellationToken.None));
        SaveProjectButton.Click += async (_, _) => Apply(await _session.SaveAsync(CancellationToken.None));
        ImportButton.Click += async (_, _) => await ImportAsync();
        ImportAudioButton.Click += async (_, _) => await ImportAudioAsync();
        ExportButton.Click += async (_, _) => await ShowExportDialogAsync();

        PlayPauseButton.Click += (_, _) => TogglePlayback();
        Back30Button.Click += (_, _) => SeekBy(-LargeJump);
        Back5Button.Click += (_, _) => SeekBy(-SmallJump);
        Forward5Button.Click += (_, _) => SeekBy(SmallJump);
        Forward30Button.Click += (_, _) => SeekBy(LargeJump);

        MediaList.SelectionChanged += (_, _) => PreviewSelectedMedia();

        // Los atajos se atienden en el túnel de entrada de la ventana, no en el control.
        // Un control personalizado solo recibe teclado cuando tiene el foco, y pulsar S
        // justo después de usar un botón no funcionaría.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _positionTimer.Tick += (_, _) => FollowPlayback();

        _mixTimer = new DispatcherTimer { Interval = MixDebounce };
        _mixTimer.Tick += async (_, _) =>
        {
            _mixTimer.Stop();
            await RenderMixAsync();
        };

        OnProjectReplaced();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>Montaje completo: pista de video y pistas de audio.</summary>
    private EditSequence Edit => _session.Current.Sequence;

    /// <summary>Pista principal de video.</summary>
    private VideoTimeline Sequence => Edit.Video;

    // ------------------------------------------------------------------ arranque

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Error al iniciar: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task StartAsync()
    {
        SetStatus("Buscando FFmpeg…");

        if (!FFmpegLocator.TryLocate(out var tools, out var searched))
        {
            SetStatus($"FFmpeg no encontrado. Ejecuta:  {FFmpegLocator.FetchCommand}" +
                      Environment.NewLine + string.Join(Environment.NewLine, searched));
            ImportButton.IsEnabled = false;
            ImportAudioButton.IsEnabled = false;
            return;
        }

        _tools = tools;
        _probe = new FFprobeService(tools);

        SetStatus("Detectando codificadores…");
        _encoders = await new EncoderDetector(tools).DetectAsync(CancellationToken.None);

        var available = _encoders.Where(enc => enc.IsAvailable).ToArray();
        var hardware = available.Where(enc => enc.IsHardware).ToArray();
        EncoderLabel.Text = hardware.Length > 0
            ? $"{hardware.Length} por GPU · {available.Length - hardware.Length} por CPU"
            : $"{available.Length} por CPU";

        _audio = new AudioClock();
        _video = new VideoPlayer(tools, width: 854, height: 480, frameRate: 30);

        // Los fotogramas llegan desde el hilo de decodificación. La superficie copia los
        // píxeles ahí mismo y solo envía el repintado al hilo de interfaz.
        _video.FrameReady = Video.Present;
        _video.Ended = () => Dispatcher.UIThread.Post(OnVideoEnded);

        // Las copias de edición se preparan en segundo plano, una a una. El preview usa el
        // original hasta que cada copia está lista, y entonces cambia solo.
        var cache = new ProxyCache(ProxyCache.DefaultDirectory);
        _proxies = new ProxyManager(tools, cache);
        _proxies.Updated += update => Dispatcher.UIThread.Post(() => OnProxyUpdate(update));
        _ = Task.Run(() => cache.TrimTo(ProxyCacheLimitBytes));
        RequestProxies();

        _positionTimer.Start();

        // El proyecto pudo cargarse antes de que hubiera FFmpeg: su mezcla se prepara ahora.
        if (!Sequence.IsEmpty)
        {
            InvalidateMix();
        }

        if (Program.StartupFiles.Count > 0)
        {
            await OpenStartupFilesAsync();
            return;
        }

        SetStatus("Listo. Importa videos, o abre un proyecto guardado.");
    }

    /// <summary>
    /// Procesa lo que llegó por línea de comandos.
    /// </summary>
    /// <remarks>
    /// Un <c>.editflow</c> se abre como proyecto; cualquier otra cosa se importa como
    /// medio. Así, asociar la extensión en el sistema hace que doble clic abra el montaje.
    /// </remarks>
    private async Task OpenStartupFilesAsync()
    {
        var projects = Program.StartupFiles
            .Where(f => f.EndsWith(Core.Projects.ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (projects.Length > 0)
        {
            Apply(await _session.OpenAsync(projects[0], CancellationToken.None));
            return;
        }

        await ImportPathsAsync(Program.StartupFiles);
    }

    private void OnProjectReplaced()
    {
        Timeline.Sequence = Edit;

        MediaList.Items.Clear();
        foreach (var media in _session.Current.Media)
        {
            MediaList.Items.Add(Path.GetFileName(media.Path));
        }

        _history.Clear();
        _playingClip = null;
        _playing = false;
        PlayPauseButton.Content = "Reproducir";
        _video?.Pause();
        _audio?.Stop();
        Video.Clear();
        Timeline.Playhead = TimeSpan.Zero;

        RefreshTimelineStats();
        RefreshTitle();
        RequestProxies();

        // Sin esto el preview queda en negro al abrir un proyecto: nada ha cargado todavía
        // el primer fotograma. Si aún no ha arrancado el reproductor, lo hará StartAsync.
        if (_video is not null && !Sequence.IsEmpty)
        {
            SeekTo(TimeSpan.Zero);
        }
    }

    // ---------------------------------------------------------------- importar

    private async Task ImportAsync()
    {
        if (_probe is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar video",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Video")
                {
                    Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v", "*.wmv", "*.flv"],
                },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();

        await ImportPathsAsync(paths);
    }

    /// <summary>Lee cada archivo y lo añade al proyecto y al montaje.</summary>
    private async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (_probe is null)
        {
            return;
        }

        var imported = 0;
        var failures = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                var info = await _probe.ProbeAsync(path, CancellationToken.None);
                var media = _session.Current.AddMedia(info);
                _proxies?.Request(media);

                if (_session.Current.Media.Count > MediaList.Items.Count)
                {
                    MediaList.Items.Add(Path.GetFileName(media.Path));
                }

                // Importar añade el clip a la timeline: el caso habitual es querer el
                // video en el montaje, y obligar a un segundo gesto para cada archivo
                // convierte "unir diez videos" en veinte acciones.
                _history.Do(new AppendClipCommand(Sequence, new Clip(media)));
                imported++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _session.MarkDirty();
        RefreshTimelineStats();

        if (imported > 0 && MediaList.SelectedIndex < 0)
        {
            MediaList.SelectedIndex = 0;
        }

        // Dejar el preview en negro tras importar obliga a un clic extra para ver algo.
        // Cargar el primer fotograma, en pausa, da la confirmación visual de que el
        // material entró bien.
        if (imported > 0 && _playingClip is null)
        {
            SeekTo(TimeSpan.Zero);
        }

        SetStatus(failures.Count == 0
            ? $"{imported} video(s) importados y añadidos a la timeline."
            : $"{imported} importados. No se pudieron leer:" + Environment.NewLine +
              string.Join(Environment.NewLine, failures.Select(f => "  · " + f)));
    }

    /// <summary>Importa música o voz y la coloca en una pista de audio, en el cabezal.</summary>
    private async Task ImportAudioAsync()
    {
        if (_probe is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar audio",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio")
                {
                    Patterns = ["*.mp3", "*.wav", "*.aac", "*.m4a", "*.flac", "*.ogg", "*.opus", "*.wma"],
                },
            ],
        });

        var added = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null)
            {
                continue;
            }

            try
            {
                var info = await _probe.ProbeMediaAsync(path, CancellationToken.None);
                var media = _session.Current.AddMedia(info);

                if (_session.Current.Media.Count > MediaList.Items.Count)
                {
                    MediaList.Items.Add(Path.GetFileName(media.Path));
                }

                var start = Timeline.Playhead;
                var track = Edit.AudioTracks.FirstOrDefault(t => !t.IsLocked && t.CanPlace(start, media.Duration));

                if (track is null)
                {
                    // Sin hueco en ninguna pista se crea otra. Son dos pasos en el
                    // historial, lo que permite deshacer solo el clip y conservar la pista.
                    var create = new AddAudioTrackCommand(Edit);
                    _history.Do(create);
                    track = create.Result!;
                }

                _history.Do(new AddAudioClipCommand(
                    track, new AudioClip(media, TimeSpan.Zero, media.Duration, start)));
                added++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _session.MarkDirty();
        RefreshTimelineStats();

        SetStatus(failures.Count == 0
            ? $"{added} audio(s) añadidos en la posición del cabezal."
            : $"{added} añadidos. No se pudieron leer:" + Environment.NewLine +
              string.Join(Environment.NewLine, failures.Select(f => "  · " + f)));
    }

    private void PreviewSelectedMedia()
    {
        var index = MediaList.SelectedIndex;
        if (index < 0 || index >= _session.Current.Media.Count)
        {
            return;
        }

        ShowMediaInfo(_session.Current.Media[index]);
    }

    private void ShowMediaInfo(MediaInfo info)
    {
        // Un audio no tiene imagen: mostrar 0×0 y 0 fps sería absurdo.
        MediaPoolInfo.Text = info.Width == 0
            ? $"{Path.GetFileName(info.Path)}{Environment.NewLine}" +
              $"solo audio{Environment.NewLine}" +
              $"{FormatTime(info.Duration)}"
            : $"{Path.GetFileName(info.Path)}{Environment.NewLine}" +
              $"{info.DisplayWidth}×{info.DisplayHeight}" +
              $"{(info.IsPortrait ? " (vertical)" : string.Empty)}{Environment.NewLine}" +
              $"{info.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps{Environment.NewLine}" +
              $"{FormatTime(info.Duration)}{Environment.NewLine}" +
              $"{info.VideoCodec}{(info.HasAudio ? " + audio" : " · sin audio")}";
    }

    private void ShowSelectedClip()
    {
        if (Timeline.SelectedClip is { } clip)
        {
            ShowMediaInfo(clip.Source);
            SetStatus($"Seleccionado: {clip}");
        }
        else if (Timeline.SelectedAudio is { } audio)
        {
            ShowMediaInfo(audio.Source);
            SetStatus($"Audio seleccionado: {audio}");
        }
    }

    // ------------------------------------------------------------- reproducción

    // La mezcla de audio del montaje se renderiza a un archivo y ese archivo es el reloj
    // maestro: su posición ES la de la timeline. El video solo lo sigue, clip a clip.
    private static readonly TimeSpan MixDebounce = TimeSpan.FromMilliseconds(500);

    private static string MixDirectory =>
        Path.Combine(Path.GetTempPath(), "editflow-preview", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    private readonly DispatcherTimer _mixTimer;
    private CancellationTokenSource? _mixRender;
    private string? _mixPath;
    private int _mixCounter;

    // La mezcla cargada corresponde a lo que hay ahora en la timeline.
    private bool _mixReady;
    private bool _playing;

    /// <summary>Mueve el cabezal a un instante de la timeline y ajusta el reproductor.</summary>
    private void SeekTo(TimeSpan position)
    {
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (clamped > Edit.Duration)
        {
            clamped = Edit.Duration;
        }

        Timeline.Playhead = clamped;

        if (_video is null)
        {
            return;
        }

        if (_mixReady)
        {
            _audio?.SeekTo(clamped);
        }

        ShowFrameAt(clamped);
    }

    /// <summary>Coloca la imagen en un instante: carga el clip que corresponda o salta dentro de él.</summary>
    private void ShowFrameAt(TimeSpan position)
    {
        if (_video is null)
        {
            return;
        }

        var located = Sequence.ClipAt(position);
        if (located is null)
        {
            // Pasado el último clip solo suena la música: la imagen queda en negro, como
            // en la exportación.
            ShowNoVideo();
            return;
        }

        var clip = located.Value.Clip;
        var offset = clip.SourceIn + located.Value.Offset;

        if (ReferenceEquals(clip, _playingClip))
        {
            // Dentro del mismo archivo basta con mover la posición del video.
            _ = _video.SeekAsync(offset, CancellationToken.None);
            return;
        }

        LoadClip(clip, offset);
    }

    private void ShowNoVideo()
    {
        if (_playingClip is null)
        {
            return;
        }

        _video?.Pause();
        Video.Clear();
        _playingClip = null;
    }

    /// <summary>Salta relativo a la posición actual.</summary>
    private void SeekBy(TimeSpan delta) => SeekTo(Timeline.Playhead + delta);

    private void LoadClip(Clip clip, TimeSpan offset)
    {
        if (_video is null)
        {
            return;
        }

        _playingClip = clip;
        _playingClipStart = Sequence.StartOf(clip);
        UpdateVideoClock();

        // La copia de 480p solo se usa para mostrar: el sonido sale de la mezcla y la
        // exportación lee siempre el original.
        var displayPath = _proxies?.Resolve(clip.Source.Path) ?? clip.Source.Path;
        _ = _video.OpenAsync(displayPath, offset, CancellationToken.None);

        if (_playing)
        {
            _video.Play();
        }
    }

    /// <summary>
    /// Decide a quién sigue el video: a la mezcla si está lista, a su propio ritmo si no.
    /// </summary>
    /// <remarks>
    /// Mientras se renderiza la mezcla tras una edición el video corre solo, en silencio, en
    /// vez de quedarse esperando a un reloj que aún no existe.
    /// </remarks>
    private void UpdateVideoClock()
    {
        if (_video is null)
        {
            return;
        }

        var clip = _playingClip;
        var audio = _audio;

        if (clip is null || audio is null || !_mixReady)
        {
            _video.MasterClock = null;
            return;
        }

        var sourceIn = clip.SourceIn;
        var start = _playingClipStart;
        _video.MasterClock = () => sourceIn + (audio.Position - start);
    }

    // ------------------------------------------------------ copias de edición

    /// <summary>Pide la copia de edición de todo lo que el proyecto usa y aún no la tiene.</summary>
    private void RequestProxies()
    {
        if (_proxies is null)
        {
            return;
        }

        foreach (var media in _session.Current.Media)
        {
            _proxies.Request(media);
        }
    }

    private void OnProxyUpdate(ProxyUpdate update)
    {
        var name = Path.GetFileName(update.SourcePath);

        switch (update.State)
        {
            case ProxyState.Started:
                SetStatus($"Preparando copia de edición de {name}…");
                break;

            case ProxyState.Progress:
                var queued = update.Pending > 1 ? $" · {update.Pending - 1} más en cola" : string.Empty;
                SetStatus($"Preparando copia de edición de {name}: " +
                          $"{update.Percentage.ToString("0", CultureInfo.InvariantCulture)} %{queued}");
                break;

            case ProxyState.Ready:
                SetStatus(update.Pending == 0
                    ? "Copias de edición listas: el preview va más fluido."
                    : $"Copia de {name} lista · {update.Pending} más en cola.");
                SwitchToProxy(update.SourcePath);
                break;

            case ProxyState.Failed:
                SetStatus($"No se pudo preparar la copia de {name}; se sigue usando el original. {update.Error}");
                break;
        }
    }

    /// <summary>Si el clip que se está viendo acaba de recibir su copia, pasa a usarla sin cortes.</summary>
    private void SwitchToProxy(string sourcePath)
    {
        if (_playingClip is null
            || !string.Equals(_playingClip.Source.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var position = Timeline.Playhead;
        _playingClip = null;
        ShowFrameAt(position);
    }

    // ---------------------------------------------------------- mezcla del preview

    /// <summary>La timeline cambió: la mezcla cargada ya no vale y hay que renderizar otra.</summary>
    private void InvalidateMix()
    {
        _mixReady = false;
        _mixRender?.Cancel();

        // La música de la mezcla vieja no debe seguir sonando sobre un montaje distinto.
        _audio?.Pause();
        UpdateVideoClock();

        if (Sequence.IsEmpty)
        {
            _mixTimer.Stop();
            _audio?.Stop();
            return;
        }

        // Varias ediciones seguidas se agrupan en un único renderizado.
        _mixTimer.Stop();
        _mixTimer.Start();
    }

    private async Task RenderMixAsync()
    {
        if (_tools is null || _audio is null || Sequence.IsEmpty)
        {
            return;
        }

        _mixRender?.Cancel();
        var source = _mixRender = new CancellationTokenSource();
        var token = source.Token;

        // Un nombre nuevo cada vez: mientras suena el anterior, Windows no deja sobrescribirlo.
        var path = Path.Combine(MixDirectory, $"mix-{++_mixCounter}.flac");

        try
        {
            var mix = await new PreviewMixRenderer(_tools).RenderAsync(Edit, path, token);

            if (token.IsCancellationRequested)
            {
                DeleteQuietly(path);
                return;
            }

            LoadMix(mix);
        }
        catch (OperationCanceledException)
        {
            // Una edición posterior se hizo cargo.
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void LoadMix(PreviewMix mix)
    {
        if (_audio is null)
        {
            return;
        }

        var previous = _mixPath;

        _audio.Open(mix.Path, Timeline.Playhead, hasAudio: true);
        _audio.Volume = 100;
        _mixPath = mix.Path;
        _mixReady = true;

        UpdateVideoClock();

        if (_playing)
        {
            _audio.Play();
        }

        DeleteQuietly(previous);
    }

    private static void DeleteQuietly(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Sigue abierto por el reproductor: se limpia al cerrar la aplicación.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }

    // ------------------------------------------------------------- transporte

    private bool IsPlaying => _playing;

    private void StartPlayback()
    {
        _playing = true;

        if (_mixReady)
        {
            _audio?.Play();
        }

        if (_playingClip is not null)
        {
            _video?.Play();
        }

        PlayPauseButton.Content = "Pausar";
    }

    private void StopPlayback()
    {
        _playing = false;
        _audio?.Pause();
        _video?.Pause();
        PlayPauseButton.Content = "Reproducir";
    }

    private void TogglePlayback()
    {
        if (_video is null)
        {
            return;
        }

        if (_playing)
        {
            StopPlayback();
            return;
        }

        // Al final del montaje, reproducir vuelve a empezar.
        if (Timeline.Playhead >= Edit.Duration)
        {
            SeekTo(TimeSpan.Zero);
        }
        else if (_playingClip is null)
        {
            // Aún no hay nada cargado: empezar por donde esté el cabezal.
            SeekTo(Timeline.Playhead);
        }

        StartPlayback();
    }

    /// <summary>
    /// Sigue la reproducción moviendo el cabezal y encadenando clips.
    /// </summary>
    /// <remarks>
    /// Con la mezcla lista, su posición es la de la timeline y solo hay que averiguar qué
    /// clip toca. Sin ella (justo tras una edición) se deduce de la posición del video.
    /// </remarks>
    private void FollowPlayback()
    {
        UpdatePositionLabels();

        if (!_playing || _video is null)
        {
            return;
        }

        TimeSpan position;

        if (_mixReady && _audio is not null)
        {
            position = _audio.HasEnded ? Edit.Duration : _audio.Position;
        }
        else if (_playingClip is not null)
        {
            position = _playingClipStart + (_video.Position - _playingClip.SourceIn);
        }
        else
        {
            return;
        }

        if (position >= Edit.Duration)
        {
            StopPlayback();
            Timeline.Playhead = Edit.Duration;
            return;
        }

        var located = Sequence.ClipAt(position);

        if (located is null)
        {
            ShowNoVideo();
        }
        else if (!ReferenceEquals(located.Value.Clip, _playingClip))
        {
            var clip = located.Value.Clip;
            LoadClip(clip, clip.SourceIn + located.Value.Offset);
        }

        Timeline.Playhead = position;
    }

    /// <summary>El archivo de video llegó a su fin.</summary>
    /// <remarks>
    /// Con la mezcla lista no hay nada que hacer: el reloj sigue y ya elegirá el clip
    /// siguiente. Sin ella, el fin del archivo es la única señal de que toca el próximo.
    /// </remarks>
    private void OnVideoEnded()
    {
        if (_mixReady || !_playing || _playingClip is null)
        {
            return;
        }

        var next = Sequence.IndexOf(_playingClip) + 1;

        if (next <= 0 || next >= Sequence.Clips.Count)
        {
            return;
        }

        var clip = Sequence.Clips[next];
        LoadClip(clip, clip.SourceIn);
    }

    private void UpdatePositionLabels()
    {
        PositionLabel.Text = $"{FormatTime(Timeline.Playhead)} / {FormatTime(Edit.Duration)}";
    }

    // ---------------------------------------------------------------- exportar

    private async Task ShowExportDialogAsync()
    {
        if (_tools is null || Sequence.IsEmpty)
        {
            SetStatus("Añade al menos un clip a la timeline antes de exportar.");
            return;
        }

        var dialog = new Views.ExportWindow(Edit, _tools, _encoders);
        await dialog.ShowDialog(this);
    }

    // ---------------------------------------------------------------- atajos

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Escribir en un cuadro de texto no debe disparar atajos de edición.
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.S when control && shift:
                _ = SaveAsAsync();
                e.Handled = true;
                break;

            case Key.S when control:
                _ = SaveAsync();
                e.Handled = true;
                break;

            case Key.S:
                SetStatus(Timeline.SplitAtPlayhead()
                    ? "Clip dividido."
                    : "No hay nada que dividir en esta posición.");
                e.Handled = true;
                break;

            case Key.O when control:
                _ = OpenAsync();
                e.Handled = true;
                break;

            case Key.N when control:
                Apply(_session.New());
                e.Handled = true;
                break;

            case Key.Delete or Key.Back:
                SetStatus(Timeline.DeleteSelected()
                    ? "Clip eliminado."
                    : "Selecciona un clip para eliminarlo.");
                e.Handled = true;
                break;

            case Key.Z when control:
                SetStatus(Timeline.Undo() ? "Deshecho." : "No hay nada que deshacer.");
                e.Handled = true;
                break;

            case Key.Y when control:
                SetStatus(Timeline.Redo() ? "Rehecho." : "No hay nada que rehacer.");
                e.Handled = true;
                break;

            case Key.E when control:
                _ = ShowExportDialogAsync();
                e.Handled = true;
                break;

            case Key.Left:
                SeekBy(shift ? -LargeJump : -SmallJump);
                e.Handled = true;
                break;

            case Key.Right:
                SeekBy(shift ? LargeJump : SmallJump);
                e.Handled = true;
                break;

            case Key.Home:
                SeekTo(TimeSpan.Zero);
                e.Handled = true;
                break;

            case Key.End:
                SeekTo(Sequence.Duration);
                e.Handled = true;
                break;

            case Key.Space:
                TogglePlayback();
                e.Handled = true;
                break;
        }
    }

    private async Task SaveAsync() => Apply(await _session.SaveAsync(CancellationToken.None));

    private async Task SaveAsAsync() => Apply(await _session.SaveAsAsync(CancellationToken.None));

    private async Task OpenAsync() => Apply(await _session.OpenAsync(CancellationToken.None));

    private void Apply(ProjectActionResult result)
    {
        if (!string.IsNullOrEmpty(result.Message))
        {
            SetStatus(result.Message);
        }
    }

    // ---------------------------------------------------------------- utilidades

    /// <summary>Tras una edición: marca el proyecto y recarga imagen y audio del montaje nuevo.</summary>
    private void OnTimelineEdited()
    {
        _session.MarkDirty();
        RefreshTimelineStats();

        // El clip cargado pudo cambiar de recorte, de sitio o desaparecer.
        _playingClip = null;
        if (_video is not null && !Sequence.IsEmpty)
        {
            SeekTo(Timeline.Playhead);
        }
        else
        {
            ShowNoVideo();
        }
    }

    private void RefreshTimelineStats()
    {
        Timeline.Refresh();
        InvalidateMix();

        TimelineStats.Text = $"{Sequence.Clips.Count} clip(s) · {Edit.AudioTracks.Count} pista(s) de audio · {FormatTime(Edit.Duration)}";
        ExportButton.IsEnabled = !Sequence.IsEmpty;
        UpdatePositionLabels();
    }

    private void RefreshTitle() => Title = _session.WindowTitle;

    private static string FormatTime(TimeSpan value) =>
        value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    private void SetStatus(string text) => StatusLabel.Text = text;

    private void OnClosing(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        _mixTimer.Stop();
        _mixRender?.Cancel();

        _proxies?.Dispose();
        _video?.Dispose();
        _audio?.Dispose();
        Video.Dispose();

        try
        {
            if (Directory.Exists(MixDirectory))
            {
                Directory.Delete(MixDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Lo que quede en la carpeta temporal lo limpia el sistema.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
