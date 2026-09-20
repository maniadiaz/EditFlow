using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using EditFlow.Engine;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Probing;
using LibVLCSharp.Shared;

#if DEBUG
using Avalonia;
#endif

namespace EditFlow.App;

/// <summary>
/// Ventana principal de EditFlow.
/// </summary>
/// <remarks>
/// El reproductor se usa a través de LibVLCSharp de forma directa mientras la interfaz
/// toma forma. Antes de la Fase 2 pasará a estar detrás de <c>IPreviewPlayer</c>, porque
/// esa fase necesita superponer texto sobre el video y el <c>VideoView</c> no lo permite
/// (ver la sección 13 de <c>docs/PLAN.md</c>).
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Una Window de Avalonia no se desecha por contrato: su ciclo de vida " +
                    "lo marca el evento Closing, donde se liberan LibVLC y el reproductor.")]
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _positionTimer;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif

        // El reproductor no emite una señal por fotograma, así que la posición se
        // consulta periódicamente. Cuatro veces por segundo basta para que el contador
        // se vea fluido sin cargar el hilo de interfaz.
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePosition();

        PlayPauseButton.Click += (_, _) => TogglePlayback();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task StartAsync()
    {
        SetStatus("Buscando FFmpeg…");

        if (!FFmpegLocator.TryLocate(out var tools, out var searched))
        {
            SetStatus("FFmpeg no encontrado. Ejecuta:  pwsh tools/fetch-ffmpeg.ps1" +
                      Environment.NewLine +
                      string.Join(Environment.NewLine, searched));
            return;
        }

        SetStatus("Preparando video de muestra…");
        var samplePath = await CreateSampleVideoAsync(tools);

        var info = await new FFprobeService(tools).ProbeAsync(samplePath, CancellationToken.None);
        Dispatcher.UIThread.Post(() => MediaPoolInfo.Text =
            $"{Path.GetFileName(info.Path)}{Environment.NewLine}" +
            $"{info.DisplayWidth}×{info.DisplayHeight}{Environment.NewLine}" +
            $"{info.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps{Environment.NewLine}" +
            $"{FormatTime(info.Duration)}{Environment.NewLine}" +
            $"{info.VideoCodec}{(info.HasAudio ? " + audio" : " · sin audio")}");

        SetStatus("Detectando codificadores…");
        var encoders = await new EncoderDetector(tools).DetectAsync(CancellationToken.None);
        var available = encoders.Where(e => e.IsAvailable).ToArray();
        var hardware = available.Where(e => e.IsHardware).ToArray();

        Dispatcher.UIThread.Post(() => EncoderLabel.Text =
            hardware.Length > 0
                ? $"{hardware.Length} por GPU · {available.Length - hardware.Length} por CPU"
                : $"{available.Length} por CPU");

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        Video.MediaPlayer = _mediaPlayer;

        using var media = new Media(_libVlc, new Uri(samplePath));
        _mediaPlayer.Play(media);
        _positionTimer.Start();

        SetStatus(
            "Motor operativo. Codificadores disponibles:" + Environment.NewLine +
            string.Join(Environment.NewLine, available.Select(e => "  · " + e.DisplayName)) +
            Environment.NewLine + Environment.NewLine +
            "Pendiente: la timeline con clips y el diálogo de exportación.");
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

        // LibVLC informa en milisegundos y devuelve valores negativos mientras no hay
        // medio cargado.
        var position = _mediaPlayer.Time;
        var length = _mediaPlayer.Length;

        if (position < 0 || length <= 0)
        {
            return;
        }

        PositionLabel.Text =
            $"{FormatTime(TimeSpan.FromMilliseconds(position))} / {FormatTime(TimeSpan.FromMilliseconds(length))}";
    }

    private static string FormatTime(TimeSpan value) =>
        value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Genera un video de muestra con el FFmpeg que empaqueta el proyecto.</summary>
    /// <remarks>Provisional: desaparece cuando el panel de medios permita importar archivos.</remarks>
    private static async Task<string> CreateSampleVideoAsync(FFmpegTools tools)
    {
        var directory = Path.Combine(Path.GetTempPath(), "editflow-sample");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "sample.mp4");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=20",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest",
            path,
        ];

        var result = await ProcessRunner.RunAsync(tools.FFmpegPath, arguments, CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"No se pudo generar el video de muestra: {result.StandardError}");
        }

        return path;
    }

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

    private void SetStatus(string text) =>
        Dispatcher.UIThread.Post(() => StatusLabel.Text = text);
}
