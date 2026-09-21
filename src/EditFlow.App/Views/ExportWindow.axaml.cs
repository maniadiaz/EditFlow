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
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Exporting;

namespace EditFlow.App.Views;

/// <summary>
/// Diálogo de exportación: ajustes predefinidos, resolución, códec, motor, control de tasa,
/// contenedor, división en partes y un resumen de lo que se va a generar.
/// </summary>
/// <remarks>
/// Muestra el comando de FFmpeg que va a ejecutarse. Cuando una exportación sale mal,
/// poder copiar el comando y reproducirlo en una terminal convierte un problema opaco
/// en uno que se puede investigar.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "El CancellationTokenSource vive solo durante una exportación y se " +
                    "libera en el finally de ExportAsync. Una Window de Avalonia no se " +
                    "desecha por contrato; el cierre se atiende en el evento Closing.")]
public partial class ExportWindow : Window
{
    private readonly EditSequence _timeline;
    private readonly FFmpegTools _tools;
    private readonly IReadOnlyList<EncoderInfo> _encoders;

    private CancellationTokenSource? _cancellation;

    // Verdadero mientras un ajuste predefinido reescribe los controles: esos cambios no deben
    // devolver la lista de ajustes a "Personalizado".
    private bool _syncing;

    /// <summary>Constructor sin parámetros para el diseñador de Avalonia.</summary>
    public ExportWindow() : this(new EditSequence(), new FFmpegTools("ffmpeg", "ffprobe", "diseñador"), [])
    {
    }

    /// <summary>Crea el diálogo para una timeline concreta.</summary>
    public ExportWindow(EditSequence timeline, FFmpegTools tools, IReadOnlyList<EncoderInfo> encoders)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(encoders);

        _timeline = timeline;
        _tools = tools;
        _encoders = encoders;

        InitializeComponent();
        PopulateOptions();
        WireEvents();
        SuggestOutputPath();
        RefreshCommandPreview();

        Closing += OnClosing;
    }

    /// <summary>
    /// Cancela la exportación en curso si la ventana se cierra.
    /// </summary>
    /// <remarks>
    /// El botón de cerrar se oculta mientras se exporta, pero la X del marco y Alt+F4
    /// siguen funcionando. Sin esto, FFmpeg seguiría codificando sin ninguna ventana que
    /// lo mostrara ni forma de detenerlo salvo el administrador de tareas.
    /// </remarks>
    private void OnClosing(object? sender, EventArgs e) => _cancellation?.Cancel();

    // ------------------------------------------------------------------ opciones

    private void PopulateOptions()
    {
        PresetBox.Items.Add(ExportPreset.CustomName);
        foreach (var preset in ExportPreset.All)
        {
            PresetBox.Items.Add(preset.Name);
        }

        PresetBox.SelectedIndex = 0;

        foreach (var container in new[] { "MP4 (compatible con todo)", "MKV (tolerante a cortes)", "MOV (edición y Apple)" })
        {
            ContainerBox.Items.Add(container);
        }

        ContainerBox.SelectedIndex = 0;

        SplitUnitBox.Items.Add("segundos");
        SplitUnitBox.Items.Add("minutos");
        SplitUnitBox.SelectedIndex = 0;

        foreach (var resolution in VideoResolution.Presets)
        {
            ResolutionBox.Items.Add(resolution.ToString());
        }

        ResolutionBox.SelectedIndex = 2; // 1080p

        foreach (var rate in new[] { "24", "25", "30", "50", "60" })
        {
            FrameRateBox.Items.Add(rate);
        }

        FrameRateBox.SelectedIndex = 2; // 30

        foreach (var codec in new[] { "H.264 (máxima compatibilidad)", "HEVC (archivos menores)", "AV1 (el más eficiente)" })
        {
            CodecBox.Items.Add(codec);
        }

        CodecBox.SelectedIndex = 0;

        foreach (var mode in new[]
                 {
                     "Calidad constante (recomendado)",
                     "Bitrate variable (tamaño aproximado)",
                     "Bitrate constante (tamaño predecible)",
                 })
        {
            RateControlBox.Items.Add(mode);
        }

        RateControlBox.SelectedIndex = 0;

        foreach (var speed in new[] { "Más rápida", "Rápida", "Equilibrada", "Calidad", "Máxima calidad" })
        {
            SpeedBox.Items.Add(speed);
        }

        SpeedBox.SelectedIndex = 2;

        foreach (var bitrate in new[] { "128", "192", "256", "320" })
        {
            AudioBitrateBox.Items.Add(bitrate);
        }

        AudioBitrateBox.SelectedIndex = 1;

        RefreshEncoders();
    }

    private void WireEvents()
    {
        ResolutionBox.SelectionChanged += (_, _) => { SuggestBitrate(); Changed(); };
        FrameRateBox.SelectionChanged += (_, _) => Changed();
        CodecBox.SelectionChanged += (_, _) => { RefreshEncoders(); SuggestBitrate(); Changed(); };
        EncoderBox.SelectionChanged += (_, _) => { RefreshTradeoff(); Changed(); };
        RateControlBox.SelectionChanged += (_, _) => { RefreshRateControlFields(); RefreshTradeoff(); Changed(); };
        SpeedBox.SelectionChanged += (_, _) => Changed();
        AudioBitrateBox.SelectionChanged += (_, _) => Changed();
        QualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                RefreshQualityLabel();
                Changed();
            }
        };
        BitrateBox.ValueChanged += (_, _) => Changed();

        PresetBox.SelectionChanged += (_, _) => OnPresetChosen();
        PortraitCheck.IsCheckedChanged += (_, _) => Changed();
        AudioCheck.IsCheckedChanged += (_, _) => { RefreshDependentControls(); Changed(); };
        WebCheck.IsCheckedChanged += (_, _) => Changed();
        ContainerBox.SelectionChanged += (_, _) => OnContainerChosen();
        SplitCheck.IsCheckedChanged += (_, _) => { RefreshDependentControls(); Changed(); };
        SplitAmountBox.ValueChanged += (_, _) => Changed();
        SplitUnitBox.SelectionChanged += (_, _) => Changed();

        BrowseButton.Click += async (_, _) => await ChooseOutputAsync();
        ExportButton.Click += async (_, _) => await ExportAsync();
        CancelButton.Click += (_, _) => _cancellation?.Cancel();
        CloseButton.Click += (_, _) => Close();

        BlockWheelSelection(PresetBox, ResolutionBox, FrameRateBox, CodecBox, EncoderBox, RateControlBox,
                            SpeedBox, AudioBitrateBox, ContainerBox, SplitUnitBox);

        RefreshQualityLabel();
        RefreshRateControlFields();
        RefreshDependentControls();
    }

    /// <summary>Un ajuste cambió: el modo predefinido ya no describe lo elegido.</summary>
    private void Changed()
    {
        if (!_syncing && PresetBox.SelectedIndex != 0)
        {
            _syncing = true;
            PresetBox.SelectedIndex = 0;
            PresetHint.Text = string.Empty;
            _syncing = false;
        }

        RefreshCommandPreview();
    }

    private void OnPresetChosen()
    {
        if (_syncing || PresetBox.SelectedIndex <= 0)
        {
            return;
        }

        var preset = ExportPreset.All[PresetBox.SelectedIndex - 1];
        PresetHint.Text = preset.Description;

        _syncing = true;
        try
        {
            ResolutionBox.SelectedIndex = Math.Max(
                Array.FindIndex(VideoResolution.Presets.ToArray(), r => r.Height == preset.Resolution.Height), 0);
            PortraitCheck.IsChecked = preset.Portrait;
            FrameRateBox.SelectedIndex = Math.Max(FrameRateBox.Items.IndexOf(
                preset.FrameRate.ToString("0", CultureInfo.InvariantCulture)), 0);
            CodecBox.SelectedIndex = preset.Codec switch
            {
                VideoCodec.Hevc => 1,
                VideoCodec.Av1 => 2,
                _ => 0,
            };
            RateControlBox.SelectedIndex = 0;
            QualitySlider.Value = preset.Quality;
            SpeedBox.SelectedIndex = (int)preset.Speed;
            AudioCheck.IsChecked = true;
            AudioBitrateBox.SelectedIndex = Math.Max(AudioBitrateBox.Items.IndexOf(
                preset.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)), 0);
            ContainerBox.SelectedIndex = (int)preset.Container;
        }
        finally
        {
            _syncing = false;
        }

        // Los motores disponibles dependen del códec, que acaba de cambiar.
        RefreshEncoders();
        RefreshRateControlFields();
        RefreshQualityLabel();
        RefreshTradeoff();
        SyncOutputExtension();
        RefreshDependentControls();
        RefreshCommandPreview();
    }

    private void OnContainerChosen()
    {
        SyncOutputExtension();
        RefreshDependentControls();
        Changed();
    }

    /// <summary>Activa o desactiva los controles que solo tienen sentido según otros.</summary>
    private void RefreshDependentControls()
    {
        AudioBitrateBox.IsEnabled = AudioCheck.IsChecked == true;

        var mkv = SelectedContainer() == ExportContainer.Mkv;
        WebCheck.IsEnabled = !mkv;

        var split = SplitCheck.IsChecked == true;
        SplitRow.IsVisible = split;
        SplitLabel.IsVisible = split;

        ContainerHint.Text = SelectedContainer() switch
        {
            ExportContainer.Mkv => "MKV no necesita la optimización para web y resiste mejor los cortes, " +
                                   "pero muchos programas y móviles no lo abren directamente.",
            ExportContainer.Mov => "MOV es el contenedor de QuickTime: cómodo para seguir editando en Mac.",
            _ => "MP4 se reproduce en prácticamente cualquier dispositivo y web.",
        };
    }

    private ExportContainer SelectedContainer() => ContainerBox.SelectedIndex switch
    {
        1 => ExportContainer.Mkv,
        2 => ExportContainer.Mov,
        _ => ExportContainer.Mp4,
    };

    private static string ExtensionFor(ExportContainer container) => container switch
    {
        ExportContainer.Mkv => ".mkv",
        ExportContainer.Mov => ".mov",
        _ => ".mp4",
    };

    /// <summary>Cambia la extensión del archivo de salida para que coincida con el contenedor.</summary>
    private void SyncOutputExtension()
    {
        if (!string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            OutputBox.Text = Path.ChangeExtension(OutputBox.Text, ExtensionFor(SelectedContainer()));
        }
    }

    /// <summary>
    /// Impide que la rueda del ratón cambie el valor de una lista desplegable.
    /// </summary>
    /// <remarks>
    /// Por defecto, girar la rueda con el puntero sobre un ComboBox cambia su selección.
    /// En un formulario esto significa que desplazarse para leer altera ajustes por el
    /// camino, sin que nada lo señale: se puede acabar exportando a 1440p a 60 fps
    /// habiendo elegido 1080p a 30. Aquí la rueda simplemente no hace nada sobre las
    /// listas, que es mucho menos sorprendente que un cambio silencioso.
    /// </remarks>
    private static void BlockWheelSelection(params ComboBox[] boxes)
    {
        foreach (var box in boxes)
        {
            box.AddHandler(
                PointerWheelChangedEvent,
                (_, e) => e.Handled = true,
                RoutingStrategies.Tunnel);
        }
    }

    /// <summary>
    /// Rellena la lista de motores con los que funcionan para el códec elegido.
    /// </summary>
    /// <remarks>
    /// Los no disponibles no se ofrecen, pero sí se enumeran debajo con su motivo. Que
    /// desaparezcan sin explicación dejaría al usuario preguntándose por qué no puede
    /// usar su tarjeta gráfica.
    /// </remarks>
    private void RefreshEncoders()
    {
        var codec = SelectedCodec();
        var forCodec = _encoders.Where(e => e.Codec == codec).ToArray();

        EncoderBox.Items.Clear();
        foreach (var encoder in forCodec.Where(e => e.IsAvailable))
        {
            EncoderBox.Items.Add(encoder.DisplayName);
        }

        // Preferir la GPU: es lo que el usuario espera cuando la tiene, y es entre cinco
        // y veinte veces más rápida que la CPU.
        var available = forCodec.Where(e => e.IsAvailable).ToArray();
        var preferred = Array.FindIndex(available, e => e.IsHardware);
        EncoderBox.SelectedIndex = available.Length == 0 ? -1 : Math.Max(preferred, 0);

        var unavailable = forCodec.Where(e => !e.IsAvailable).ToArray();
        UnavailableLabel.Text = unavailable.Length == 0
            ? string.Empty
            : "No disponibles:" + Environment.NewLine +
              string.Join(Environment.NewLine,
                  unavailable.Select(e => $"  · {e.DisplayName} — {e.UnavailableReason}"));

        ExportButton.IsEnabled = available.Length > 0 && !_timeline.Video.IsEmpty;
    }

    private void RefreshRateControlFields()
    {
        var constantQuality = RateControlBox.SelectedIndex == 0;
        QualityRow.IsVisible = constantQuality;
        BitrateRow.IsVisible = !constantQuality;

        if (!constantQuality)
        {
            SuggestBitrate();
        }
    }

    private void RefreshTradeoff()
    {
        var encoder = SelectedEncoderName();
        if (encoder is null)
        {
            TradeoffLabel.IsVisible = false;
            return;
        }

        var support = RateControlCapabilities.Describe(encoder, SelectedRateControl());
        TradeoffLabel.Text = support.Note ?? string.Empty;
        TradeoffLabel.IsVisible = support.Note is not null;
    }

    private void RefreshQualityLabel()
    {
        var encoder = SelectedEncoderName();
        var quality = (int)QualitySlider.Value;

        // Se muestra también el valor nativo: quien conoce el CRF quiere verlo, y deja
        // claro que la escala de 1 a 100 no es el parámetro que recibe FFmpeg.
        QualityValue.Text = encoder is null
            ? quality.ToString(CultureInfo.InvariantCulture)
            : $"{quality}  ({QualityScale.ToNative(quality, encoder)})";
    }

    private void SuggestBitrate() =>
        BitrateBox.Value = QualityScale.SuggestedBitrateKbps(SelectedResolution(), SelectedCodec());

    private void SuggestOutputPath()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var name = $"EditFlow-{DateTime.Now:yyyyMMdd-HHmmss}.mp4";
        OutputBox.Text = Path.Combine(string.IsNullOrEmpty(videos) ? Path.GetTempPath() : videos, name);
    }

    // ----------------------------------------------------------------- lectura

    private VideoResolution SelectedResolution()
    {
        var resolution = VideoResolution.Presets[Math.Max(ResolutionBox.SelectedIndex, 0)];
        return PortraitCheck.IsChecked == true ? resolution.AsPortrait() : resolution;
    }

    private TimeSpan? SelectedSegment()
    {
        if (SplitCheck.IsChecked != true)
        {
            return null;
        }

        var amount = (double)(SplitAmountBox.Value ?? 60);
        return TimeSpan.FromSeconds(SplitUnitBox.SelectedIndex == 1 ? amount * 60 : amount);
    }

    private VideoCodec SelectedCodec() => CodecBox.SelectedIndex switch
    {
        1 => VideoCodec.Hevc,
        2 => VideoCodec.Av1,
        _ => VideoCodec.H264,
    };

    private RateControlMode SelectedRateControl() => RateControlBox.SelectedIndex switch
    {
        1 => RateControlMode.VariableBitrate,
        2 => RateControlMode.ConstantBitrate,
        _ => RateControlMode.ConstantQuality,
    };

    private EncodingSpeed SelectedSpeed() => SpeedBox.SelectedIndex switch
    {
        0 => EncodingSpeed.Fastest,
        1 => EncodingSpeed.Fast,
        3 => EncodingSpeed.Quality,
        4 => EncodingSpeed.Slowest,
        _ => EncodingSpeed.Balanced,
    };

    private string? SelectedEncoderName()
    {
        if (EncoderBox.SelectedItem is not string displayName)
        {
            return null;
        }

        return _encoders.FirstOrDefault(e => e.DisplayName == displayName)?.Name;
    }

    private ExportSettings? BuildSettings()
    {
        var encoder = SelectedEncoderName();
        if (encoder is null || string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            return null;
        }

        return new ExportSettings
        {
            OutputPath = OutputBox.Text,
            Resolution = SelectedResolution(),
            EncoderName = encoder,
            FrameRate = double.Parse((string)FrameRateBox.SelectedItem!, CultureInfo.InvariantCulture),
            RateControl = SelectedRateControl(),
            Quality = (int)QualitySlider.Value,
            VideoBitrateKbps = (int)(BitrateBox.Value ?? 10_000),
            Speed = SelectedSpeed(),
            AudioBitrateKbps = int.Parse((string)AudioBitrateBox.SelectedItem!, CultureInfo.InvariantCulture),
            IncludeAudio = AudioCheck.IsChecked == true,
            OptimizeForStreaming = WebCheck.IsChecked == true,
            SegmentDuration = SelectedSegment(),
        };
    }

    private void RefreshCommandPreview()
    {
        var settings = BuildSettings();
        RefreshSplitAndSummary(settings);

        if (settings is null || _timeline.Video.IsEmpty)
        {
            CommandBox.Text = "(Elige un motor y añade clips a la timeline)";
            return;
        }

        try
        {
            using var command = ExportCommandBuilder.Build(_timeline, settings);
            CommandBox.Text = command.ToDisplayString(_tools.FFmpegPath);
        }
        catch (ArgumentException ex)
        {
            CommandBox.Text = "Ajustes incompletos: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ resumen

    private void RefreshSplitAndSummary(ExportSettings? settings)
    {
        var total = _timeline.Duration;

        if (settings is null)
        {
            SplitLabel.Text = string.Empty;
            SummaryLabel.Text = "Elige un motor de codificación y un archivo de salida para ver el resumen.";
            return;
        }

        var parts = 1;
        if (settings.SegmentDuration is { } segment)
        {
            parts = ExportSettings.SegmentCount(total, segment);
            SplitLabel.Text = DescribeParts(settings, total, segment, parts);
        }
        else
        {
            SplitLabel.Text = string.Empty;
        }

        var codec = SelectedCodec();
        var bytes = ExportEstimate.SizeBytes(settings, codec, total);
        var estimate = "≈ " + FormatSize(bytes) + " (orientativo)";
        if (parts > 1)
        {
            estimate = $"≈ {FormatSize(bytes)} en total, unos {FormatSize(bytes / parts)} por video (orientativo)";
        }

        var audio = settings.IncludeAudio
            ? $"AAC {settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)} kbps"
            : "sin audio";

        var output = parts > 1
            ? $"{parts.ToString(CultureInfo.InvariantCulture)} videos: " +
              Path.GetFileName(settings.SegmentPath(1)) + " … " + Path.GetFileName(settings.SegmentPath(parts))
            : Path.GetFileName(settings.OutputPath);

        SummaryLabel.Text =
            $"Duración del montaje: {FormatTime(total)}" + Environment.NewLine +
            $"Video: {settings.Resolution.Width}×{settings.Resolution.Height} · " +
            $"{settings.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps · " +
            $"{EncoderBox.SelectedItem} · {ExtensionFor(settings.Container).TrimStart('.').ToUpperInvariant()}" + Environment.NewLine +
            $"Audio: {audio}" + Environment.NewLine +
            $"Tamaño: {estimate}" + Environment.NewLine +
            $"Se guardará como: {output}";
    }

    private static string DescribeParts(ExportSettings settings, TimeSpan total, TimeSpan segment, int parts)
    {
        if (parts == 1)
        {
            return $"El montaje dura {FormatTime(total)}, menos que una parte de {FormatTime(segment)}: " +
                   "se generará un solo video.";
        }

        var last = total - (segment * (parts - 1));
        var names = parts == 2
            ? $"{Path.GetFileName(settings.SegmentPath(1))} y {Path.GetFileName(settings.SegmentPath(2))}"
            : $"{Path.GetFileName(settings.SegmentPath(1))} … {Path.GetFileName(settings.SegmentPath(parts))}";

        return $"Se generarán {parts.ToString(CultureInfo.InvariantCulture)} videos de {FormatTime(segment)} cada uno " +
               $"(el último dura {FormatTime(last)}): {names}";
    }

    private static string FormatSize(long bytes)
    {
        var megabytes = bytes / (1024.0 * 1024.0);
        return megabytes >= 1024
            ? (megabytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : megabytes.ToString(megabytes >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " MB";
    }

    // ---------------------------------------------------------------- exportar

    private async Task ChooseOutputAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar video",
            DefaultExtension = ExtensionFor(SelectedContainer()).TrimStart('.'),
            SuggestedFileName = Path.GetFileName(OutputBox.Text ?? "EditFlow.mp4"),
            FileTypeChoices =
            [
                new FilePickerFileType("MP4") { Patterns = ["*.mp4"] },
                new FilePickerFileType("MKV") { Patterns = ["*.mkv"] },
                new FilePickerFileType("MOV") { Patterns = ["*.mov"] },
            ],
        });

        var path = file?.TryGetLocalPath();
        if (path is not null)
        {
            OutputBox.Text = path;

            // El contenedor sale de la extensión escrita; si no es ninguna conocida, se le añade la elegida.
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var index = extension switch { ".mp4" => 0, ".mkv" => 1, ".mov" => 2, _ => -1 };
            if (index < 0)
            {
                SyncOutputExtension();
            }
            else if (index != ContainerBox.SelectedIndex)
            {
                _syncing = true;
                ContainerBox.SelectedIndex = index;
                _syncing = false;
                RefreshDependentControls();
            }

            RefreshCommandPreview();
        }
    }

    private async Task ExportAsync()
    {
        var settings = BuildSettings();
        if (settings is null)
        {
            ProgressLabel.Text = "Faltan ajustes: elige un motor y una ruta de salida.";
            return;
        }

        SetExporting(true);

        _cancellation = new CancellationTokenSource();
        var job = new ExportJob(_tools);
        var progress = new Progress<ExportProgress>(ShowProgress);

        try
        {
            var result = await job.RunAsync(_timeline, settings, progress, _cancellation.Token);

            if (result.Succeeded)
            {
                var files = result.Files is { Count: > 0 } produced ? produced : [result.OutputPath];
                var bytes = files.Sum(f => new FileInfo(f).Length);
                Progress.Value = 100;

                var what = files.Count > 1
                    ? $"{files.Count.ToString(CultureInfo.InvariantCulture)} videos · {FormatSize(bytes)} en total"
                    : FormatSize(bytes);

                ProgressLabel.Text =
                    $"Listo en {result.Elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s · {what}" +
                    Environment.NewLine +
                    (files.Count > 1
                        ? Path.GetDirectoryName(files[0]) + Environment.NewLine +
                          string.Join(", ", files.Select(Path.GetFileName))
                        : files[0]);
            }
            else
            {
                ProgressLabel.Text = "La exportación falló:" + Environment.NewLine + result.ErrorMessage;
            }
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            ProgressLabel.Text = "Exportación cancelada. No se dejó ningún archivo parcial.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ProgressLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetExporting(false);
            SpeedLabel.Text = string.Empty;
        }
    }

    private void ShowProgress(ExportProgress progress)
    {
        Progress.Value = progress.Percentage;

        var remaining = progress.Remaining is { } left
            ? $" · quedan {FormatTime(left)}"
            : string.Empty;

        ProgressLabel.Text =
            $"{progress.Percentage.ToString("0.#", CultureInfo.InvariantCulture)} % · " +
            $"{FormatTime(progress.Processed)} de {FormatTime(progress.Total)}{remaining}";

        SpeedLabel.Text = progress.Speed > 0
            ? $"{progress.Speed.ToString("0.0", CultureInfo.InvariantCulture)}x"
            : string.Empty;
    }

    private void SetExporting(bool exporting)
    {
        ExportButton.IsVisible = !exporting;
        CloseButton.IsVisible = !exporting;
        CancelButton.IsVisible = exporting;

        // Cambiar los ajustes a mitad de exportación no afectaría al proceso en curso,
        // pero dejaría el comando mostrado sin relación con lo que se está generando.
        foreach (var control in new Control[]
                 {
                     PresetBox, ResolutionBox, PortraitCheck, FrameRateBox, CodecBox, EncoderBox,
                     RateControlBox, QualitySlider, BitrateBox, SpeedBox, AudioCheck, ContainerBox,
                     SplitCheck, SplitAmountBox, SplitUnitBox, BrowseButton,
                 })
        {
            control.IsEnabled = !exporting;
        }

        if (!exporting)
        {
            RefreshDependentControls();
        }
        else
        {
            AudioBitrateBox.IsEnabled = false;
            WebCheck.IsEnabled = false;
        }
    }

    private static string FormatTime(TimeSpan value) =>
        value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
}
