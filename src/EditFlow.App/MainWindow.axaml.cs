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
using LibVLCSharp.Shared;

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
    private static readonly TimeSpan SmallJump = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LargeJump = TimeSpan.FromSeconds(30);

    private readonly UndoHistory _history = new();
    private readonly DispatcherTimer _positionTimer;
    private ProjectSession _session = null!;

    private FFmpegTools? _tools;
    private FFprobeService? _probe;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private IReadOnlyList<EncoderInfo> _encoders = [];

    // Clip que el reproductor tiene cargado ahora mismo, y dónde empieza en la timeline.
    private Clip? _playingClip;
    private TimeSpan _playingClipStart;

    // Pausar justo después de Play() deja la imagen en negro: LibVLC todavía no ha
    // decodificado nada. La pausa se difiere hasta que el reproductor confirma que
    // los fotogramas están fluyendo.
    private bool _pauseOnceFramesFlow;

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
        Timeline.TimelineEdited += (_, _) => { _session.MarkDirty(); RefreshTimelineStats(); };
        Timeline.PlayheadMoved += (_, position) => SeekTo(position);
        Timeline.SelectionChanged += (_, _) => ShowSelectedClip();

        NewProjectButton.Click += (_, _) => Apply(_session.New());
        OpenProjectButton.Click += async (_, _) => Apply(await _session.OpenAsync(CancellationToken.None));
        SaveProjectButton.Click += async (_, _) => Apply(await _session.SaveAsync(CancellationToken.None));
        ImportButton.Click += async (_, _) => await ImportAsync();
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

        OnProjectReplaced();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private VideoTimeline Sequence => _session.Current.Timeline;

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

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        Video.MediaPlayer = _mediaPlayer;
        _positionTimer.Start();

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
        Timeline.Timeline = Sequence;

        MediaList.Items.Clear();
        foreach (var media in _session.Current.Media)
        {
            MediaList.Items.Add(Path.GetFileName(media.Path));
        }

        _history.Clear();
        _playingClip = null;
        Timeline.Playhead = TimeSpan.Zero;

        RefreshTimelineStats();
        RefreshTitle();
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

    private void PreviewSelectedMedia()
    {
        var index = MediaList.SelectedIndex;
        if (index < 0 || index >= _session.Current.Media.Count)
        {
            return;
        }

        ShowMediaInfo(_session.Current.Media[index]);
    }

    private void ShowMediaInfo(MediaInfo info) =>
        MediaPoolInfo.Text =
            $"{Path.GetFileName(info.Path)}{Environment.NewLine}" +
            $"{info.DisplayWidth}×{info.DisplayHeight}" +
            $"{(info.IsPortrait ? " (vertical)" : string.Empty)}{Environment.NewLine}" +
            $"{info.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps{Environment.NewLine}" +
            $"{FormatTime(info.Duration)}{Environment.NewLine}" +
            $"{info.VideoCodec}{(info.HasAudio ? " + audio" : " · sin audio")}";

    private void ShowSelectedClip()
    {
        var clip = Timeline.SelectedClip;
        if (clip is null)
        {
            return;
        }

        ShowMediaInfo(clip.Source);
        SetStatus($"Seleccionado: {clip}");
    }

    // ------------------------------------------------------------- reproducción

    /// <summary>Mueve el cabezal a un instante de la timeline y ajusta el reproductor.</summary>
    private void SeekTo(TimeSpan position)
    {
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (clamped > Sequence.Duration)
        {
            clamped = Sequence.Duration;
        }

        Timeline.Playhead = clamped;

        var located = Sequence.ClipAt(clamped);
        if (located is null || _mediaPlayer is null)
        {
            return;
        }

        var clip = located.Value.Clip;
        var offset = clip.SourceIn + located.Value.Offset;

        if (ReferenceEquals(clip, _playingClip))
        {
            // Dentro del mismo archivo basta con mover la posición: recargar el medio
            // provocaría un parpadeo negro en cada salto.
            _mediaPlayer.Time = (long)offset.TotalMilliseconds;
            return;
        }

        LoadClip(clip, offset);
    }

    /// <summary>Salta relativo a la posición actual.</summary>
    private void SeekBy(TimeSpan delta) => SeekTo(Timeline.Playhead + delta);

    private void LoadClip(Clip clip, TimeSpan offset)
    {
        if (_libVlc is null || _mediaPlayer is null)
        {
            return;
        }

        var wasPlaying = _mediaPlayer.IsPlaying;

        using var media = new Media(_libVlc, new Uri(clip.Source.Path));
        _mediaPlayer.Play(media);
        _mediaPlayer.Time = (long)offset.TotalMilliseconds;

        _playingClip = clip;
        _playingClipStart = Sequence.IndexOf(clip) >= 0 ? Sequence.StartOf(clip) : TimeSpan.Zero;

        _pauseOnceFramesFlow = !wasPlaying;
        PlayPauseButton.Content = wasPlaying ? "Pausar" : "Reproducir";
    }

    private void TogglePlayback()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        // Si aún no hay nada cargado, empezar por donde esté el cabezal.
        if (_playingClip is null)
        {
            SeekTo(Timeline.Playhead);
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            PlayPauseButton.Content = "Reproducir";
        }
        else
        {
            _pauseOnceFramesFlow = false;
            _mediaPlayer.Play();
            PlayPauseButton.Content = "Pausar";
        }
    }

    /// <summary>
    /// Sigue la reproducción moviendo el cabezal y encadenando clips.
    /// </summary>
    /// <remarks>
    /// El reproductor solo conoce el archivo que tiene cargado, no el montaje. Traducir
    /// su posición a la de la timeline es lo que hace que el cabezal avance solo y que
    /// al terminar un clip empiece el siguiente, en vez de detenerse en cada corte.
    /// </remarks>
    private void FollowPlayback()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        UpdatePositionLabels();

        // La pausa diferida: en cuanto hay tiempo transcurrido hay un fotograma en
        // pantalla, así que ya se puede detener sin dejarlo todo en negro.
        if (_pauseOnceFramesFlow && _mediaPlayer.Time > 0)
        {
            _pauseOnceFramesFlow = false;
            _mediaPlayer.SetPause(true);
            PlayPauseButton.Content = "Reproducir";
            return;
        }

        if (_playingClip is null || !_mediaPlayer.IsPlaying)
        {
            return;
        }

        var inFile = TimeSpan.FromMilliseconds(Math.Max(_mediaPlayer.Time, 0));
        var withinClip = inFile - _playingClip.SourceIn;

        if (withinClip >= _playingClip.Duration)
        {
            AdvanceToNextClip();
            return;
        }

        Timeline.Playhead = _playingClipStart + withinClip;
    }

    private void AdvanceToNextClip()
    {
        if (_playingClip is null)
        {
            return;
        }

        var next = Sequence.IndexOf(_playingClip) + 1;

        if (next <= 0 || next >= Sequence.Clips.Count)
        {
            _mediaPlayer?.SetPause(true);
            PlayPauseButton.Content = "Reproducir";
            Timeline.Playhead = Sequence.Duration;
            return;
        }

        var clip = Sequence.Clips[next];
        LoadClip(clip, clip.SourceIn);
        _mediaPlayer?.Play();
        PlayPauseButton.Content = "Pausar";
    }

    private void UpdatePositionLabels()
    {
        PositionLabel.Text = $"{FormatTime(Timeline.Playhead)} / {FormatTime(Sequence.Duration)}";
    }

    // ---------------------------------------------------------------- exportar

    private async Task ShowExportDialogAsync()
    {
        if (_tools is null || Sequence.IsEmpty)
        {
            SetStatus("Añade al menos un clip a la timeline antes de exportar.");
            return;
        }

        var dialog = new Views.ExportWindow(Sequence, _tools, _encoders);
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

    private void RefreshTimelineStats()
    {
        Timeline.Refresh();

        TimelineStats.Text = $"{Sequence.Clips.Count} clip(s) · {FormatTime(Sequence.Duration)}";
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

        // El orden importa: soltar la vista antes que el reproductor evita que LibVLC
        // siga dibujando sobre una ventana nativa que ya no existe.
        Video.MediaPlayer = null;
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
