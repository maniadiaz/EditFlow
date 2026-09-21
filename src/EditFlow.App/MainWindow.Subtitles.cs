// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Speech;

namespace EditFlow.App;

// Subtítulos automáticos: transcribir el sonido del montaje con Whisper, en el propio equipo, y dejar
// el resultado como textos editables en una capa nueva.
public partial class MainWindow
{
    private CancellationTokenSource? _subtitleCancellation;

    private WhisperModel SelectedSubtitleModel =>
        WhisperModel.All[Math.Max(SubtitleModelBox.SelectedIndex, 0)];

    private SpeechLanguage SelectedSubtitleLanguage =>
        SpeechLanguage.All[Math.Max(SubtitleLanguageBox.SelectedIndex, 0)];

    private void WireSubtitles()
    {
        foreach (var language in SpeechLanguage.All)
        {
            SubtitleLanguageBox.Items.Add(language.Label);
        }

        foreach (var model in WhisperModel.All)
        {
            SubtitleModelBox.Items.Add(model.Label);
        }

        SubtitleLanguageBox.SelectedIndex = 0;

        // Si el modelo preciso ya está descargado, es el que se propone: transcribe bastante mejor.
        SubtitleModelBox.SelectedIndex = WhisperSetup.HasModel(WhisperModel.Small) ? 1 : 0;

        SubtitleModelBox.SelectionChanged += (_, _) => RefreshSubtitleSetup();
        GenerateSubtitlesButton.Click += async (_, _) => await GenerateSubtitlesAsync();
        CancelSubtitlesButton.Click += (_, _) => _subtitleCancellation?.Cancel();

        RefreshSubtitleSetup();
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Dice qué hay instalado y cuánto habría que descargar.</summary>
    private void RefreshSubtitleSetup()
    {
        var model = SelectedSubtitleModel;

        if (!WhisperSetup.IsSupported)
        {
            SubtitleSetupNote.Text = "Por ahora los subtítulos automáticos solo están disponibles en Windows de 64 bits.";
            GenerateSubtitlesButton.IsEnabled = false;
            return;
        }

        var needRuntime = !WhisperSetup.IsRuntimeInstalled();
        var needModel = !WhisperSetup.HasModel(model);
        var bytes = (needRuntime ? WhisperSetup.RuntimeDownloadBytes : 0) + (needModel ? model.Bytes : 0);

        if (bytes == 0)
        {
            SubtitleSetupNote.Text = $"Whisper y el modelo «{model.Label}» ya están instalados.";
            GenerateSubtitlesButton.Content = "Generar subtítulos";
        }
        else
        {
            SubtitleSetupNote.Text = "La primera vez se descarga Whisper (código abierto, licencia MIT) y el modelo, " +
                                     $"unos {Megabytes(bytes)} en total. Se guardan en tu carpeta de datos y no se vuelven a bajar.";
            GenerateSubtitlesButton.Content = $"Descargar ({Megabytes(bytes)}) y generar";
        }

        GenerateSubtitlesButton.IsEnabled = _subtitleCancellation is null;
    }

    /// <summary>Si el montaje tiene algún sonido que transcribir.</summary>
    private bool HasAnyAudio() =>
        Sequence.Clips.Any(c => c.HasOwnAudio)
        || Edit.AudioTracks.Any(t => t.Clips.Any(a => !a.IsMuted))
        || Edit.OverlayTracks.Any(t => !t.IsHidden && t.Items.Any(i => i is { Kind: OverlayKind.Video, PlaysAudio: true }));

    /// <summary>
    /// Deja el resultado a la vista en el propio panel: la barra de estado se pasa por alto con facilidad, y
    /// una generación que termina sin añadir nada parecía «no hacer nada».
    /// </summary>
    private void ShowSubtitleResult(string text, bool success)
    {
        SubtitleResultText.Text = text;
        SubtitleResultBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(success ? "#14301f" : "#3a2a12"));
        SubtitleResultBox.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(success ? "#2f8f56" : "#b8862f"));
        SubtitleResultBox.IsVisible = true;
        SetStatus(text);
    }

    private void SetSubtitlesBusy(bool busy)
    {
        GenerateSubtitlesButton.IsVisible = !busy;
        CancelSubtitlesButton.IsVisible = busy;
        SubtitleProgress.IsVisible = busy;
        SubtitleLanguageBox.IsEnabled = !busy;
        SubtitleModelBox.IsEnabled = !busy;

        if (!busy)
        {
            SubtitleProgress.Value = 0;
            RefreshSubtitleSetup();
        }
    }

    private async Task GenerateSubtitlesAsync()
    {
        if (_tools is null || Sequence.IsEmpty)
        {
            SetStatus("Añade un video a la timeline antes de generar subtítulos.");
            return;
        }

        if (!HasAnyAudio())
        {
            SetStatus("El montaje no tiene sonido que transcribir.");
            return;
        }

        var model = SelectedSubtitleModel;
        var language = SelectedSubtitleLanguage;

        using var cancellation = new CancellationTokenSource();
        _subtitleCancellation = cancellation;
        SubtitleResultBox.IsVisible = false;
        SetSubtitlesBusy(true);

        void Show(string text, double fraction)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SubtitleProgress.Value = fraction * 100;
                SetStatus(text);
            });
        }

        try
        {
            // 1. Lo que falte por descargar, una sola vez. El avance se reparte según el tamaño de cada parte.
            var needRuntime = !WhisperSetup.IsRuntimeInstalled();
            var needModel = !WhisperSetup.HasModel(model);
            var downloadBytes = (needRuntime ? WhisperSetup.RuntimeDownloadBytes : 0) + (needModel ? model.Bytes : 0);
            var downloadShare = downloadBytes == 0 ? 0 : 0.5;

            if (needRuntime)
            {
                var share = (double)WhisperSetup.RuntimeDownloadBytes / downloadBytes * downloadShare;
                await WhisperSetup.InstallRuntimeAsync(
                    new Progress<double>(p => Show($"Descargando Whisper… {(p * 100).ToString("0", CultureInfo.InvariantCulture)} %", p * share)),
                    cancellationToken: cancellation.Token);
            }

            if (needModel)
            {
                var offset = needRuntime ? (double)WhisperSetup.RuntimeDownloadBytes / downloadBytes * downloadShare : 0;
                await WhisperSetup.InstallModelAsync(
                    model,
                    new Progress<double>(p => Show(
                        $"Descargando el modelo «{model.Label}»… {(p * 100).ToString("0", CultureInfo.InvariantCulture)} %",
                        offset + (p * (downloadShare - offset)))),
                    cancellationToken: cancellation.Token);
            }

            // 2. La transcripción.
            var generator = new SubtitleGenerator(_tools);
            var result = await generator.GenerateAsync(
                Edit,
                model,
                language.Code,
                new Progress<double>(p => Show(
                    $"Transcribiendo… {(p * 100).ToString("0", CultureInfo.InvariantCulture)} %",
                    downloadShare + (p * (1 - downloadShare)))),
                cancellation.Token);

            // 3. Colocarlos como textos editables, en un solo paso del historial.
            var added = Timeline.AddSubtitles(result.Segments.Select(s => new SubtitleCue(s.Start, s.End, s.Text)));

            if (added.Added == 0)
            {
                ShowSubtitleResult(
                    result.Detected == 0
                        ? "No se detectó ningún sonido que transcribir. Comprueba que el montaje tenga audio y que no esté silenciado."
                        : $"No se encontró voz que transcribir: Whisper solo detectó música o sonidos sueltos ({result.Detected} " +
                          "fragmentos, ninguno con palabras). Si sí hay voz, prueba con el modelo «Small», o elige el idioma en lugar " +
                          "de «Automático». Si el video es solo música, no habrá subtítulos.",
                    success: false);
                return;
            }

            // Llevar el cabezal al primero para que se vea dónde quedaron: con un video largo pueden empezar
            // mucho después del principio y nada a la vista lo indicaría.
            var first = added.FirstStart ?? TimeSpan.Zero;
            SeekTo(first);

            var skipped = added.Skipped > 0
                ? $" {added.Skipped} no cupieron porque ya había subtítulos en ese instante."
                : string.Empty;

            ShowSubtitleResult(
                $"✓ {added.Added} subtítulos añadidos en la capa «{AddSubtitlesCommand.LayerName}»; el primero empieza en " +
                $"{Controls.TimelineControl.FormatClock(first)}.{skipped} Cada uno es un texto que puedes corregir.",
                success: true);
        }
        catch (OperationCanceledException)
        {
            ShowSubtitleResult("Generación de subtítulos cancelada.", success: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            ShowSubtitleResult("No se pudieron generar los subtítulos: " + ex.Message, success: false);
        }
        finally
        {
            _subtitleCancellation = null;
            SetSubtitlesBusy(false);
        }
    }
}
