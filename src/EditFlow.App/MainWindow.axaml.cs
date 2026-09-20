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
    private readonly VideoTimeline _timeline = new();
    private readonly UndoHistory _history = new();
    private readonly List<MediaInfo> _mediaPool = [];
    private readonly DispatcherTimer _positionTimer;

    private FFmpegTools? _tools;
    private FFprobeService? _probe;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private IReadOnlyList<EncoderInfo> _encoders = [];

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif

        Timeline.Timeline = _timeline;
        Timeline.UndoHistory = _history;
        Timeline.TimelineEdited += (_, _) => RefreshTimelineStats();
        Timeline.PlayheadMoved += OnPlayheadMoved;
        Timeline.SelectionChanged += (_, _) => ShowSelectedClip();

        ImportButton.Click += async (_, _) => await ImportAsync();
        ExportButton.Click += async (_, _) => await ShowExportDialogAsync();
        PlayPauseButton.Click += (_, _) => TogglePlayback();
        MediaList.SelectionChanged += (_, _) => PreviewSelectedMedia();

        // Los atajos se atienden en el túnel de entrada de la ventana, no en el control.
        // Un control personalizado solo recibe teclado cuando tiene el foco, y pulsar S
        // justo después de usar un botón no funcionaría.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => UpdatePosition();

        Opened += OnOpened;
        Closing += OnClosing;
    }

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
            await ImportPathsAsync(Program.StartupFiles);
            return;
        }

        SetStatus("Listo. Importa uno o varios videos para empezar a montar.");
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

    /// <summary>Lee cada archivo y lo añade al montaje.</summary>
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
                _mediaPool.Add(info);
                MediaList.Items.Add(Path.GetFileName(info.Path));

                // Importar añade el clip a la timeline: el caso habitual es querer el
                // video en el montaje, y obligar a un segundo gesto para cada archivo
                // convierte "unir diez videos" en veinte acciones.
                _history.Do(new AppendClipCommand(_timeline, new Clip(info)));
                imported++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        RefreshTimelineStats();

        if (imported > 0 && MediaList.SelectedIndex < 0)
        {
            MediaList.SelectedIndex = 0;
        }

        SetStatus(failures.Count == 0
            ? $"{imported} video(s) importados y añadidos a la timeline."
            : $"{imported} importados. No se pudieron leer:" + Environment.NewLine +
              string.Join(Environment.NewLine, failures.Select(f => "  · " + f)));
    }

    private void PreviewSelectedMedia()
    {
        var index = MediaList.SelectedIndex;
        if (index < 0 || index >= _mediaPool.Count)
        {
            return;
        }

        var info = _mediaPool[index];
        ShowMediaInfo(info);
        PlayFile(info.Path, TimeSpan.Zero);
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

    private void OnPlayheadMoved(object? sender, TimeSpan position)
    {
        var located = _timeline.ClipAt(position);
        if (located is null)
        {
            return;
        }

        // El preview reproduce el archivo del clip que hay bajo el cabezal, saltando al
        // punto equivalente dentro de él. Es aproximado: no aplica la escala ni el
        // relleno de la exportación, pero permite ver dónde se está cortando.
        PlayFile(located.Value.Clip.Source.Path, located.Value.Clip.SourceIn + located.Value.Offset);
    }

    private void PlayFile(string path, TimeSpan offset)
    {
        if (_libVlc is null || _mediaPlayer is null)
        {
            return;
        }

        using var media = new Media(_libVlc, new Uri(path));
        _mediaPlayer.Play(media);
        _mediaPlayer.Time = (long)offset.TotalMilliseconds;
        PlayPauseButton.Content = "Pausar";
    }

    private void TogglePlayback()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            PlayPauseButton.Content = "Reproducir";
        }
        else
        {
            _mediaPlayer.Play();
            PlayPauseButton.Content = "Pausar";
        }
    }

    private void UpdatePosition()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        var position = _mediaPlayer.Time;
        var length = _mediaPlayer.Length;

        if (position < 0 || length <= 0)
        {
            return;
        }

        PositionLabel.Text =
            $"{FormatTime(TimeSpan.FromMilliseconds(position))} / {FormatTime(TimeSpan.FromMilliseconds(length))}";
    }

    // ---------------------------------------------------------------- exportar

    private async Task ShowExportDialogAsync()
    {
        if (_tools is null || _timeline.IsEmpty)
        {
            SetStatus("Añade al menos un clip a la timeline antes de exportar.");
            return;
        }

        var dialog = new Views.ExportWindow(_timeline, _tools, _encoders);
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

        switch (e.Key)
        {
            case Key.S when !control:
                SetStatus(Timeline.SplitAtPlayhead()
                    ? "Clip dividido."
                    : "No hay nada que dividir en esta posición.");
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

            case Key.Space:
                TogglePlayback();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- utilidades

    private void RefreshTimelineStats()
    {
        Timeline.Refresh();

        TimelineStats.Text =
            $"{_timeline.Clips.Count} clip(s) · {FormatTime(_timeline.Duration)}";

        ExportButton.IsEnabled = !_timeline.IsEmpty;
    }

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
