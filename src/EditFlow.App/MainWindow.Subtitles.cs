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
        SubtitleModelBox.SelectedIndex = 0;

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

        var needRuntime = WhisperSetup.LocateCli() is null;
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
            var needRuntime = WhisperSetup.LocateCli() is null;
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
            var segments = await generator.GenerateAsync(
                Edit,
                model,
                language.Code,
                new Progress<double>(p => Show(
                    $"Transcribiendo… {(p * 100).ToString("0", CultureInfo.InvariantCulture)} %",
                    downloadShare + (p * (1 - downloadShare)))),
                cancellation.Token);

            // 3. Colocarlos como textos editables, en un solo paso del historial.
            var added = Timeline.AddSubtitles(segments.Select(s => new SubtitleCue(s.Start, s.End, s.Text)));

            SetStatus(added == 0
                ? "No se detectó voz en el montaje."
                : $"{added} subtítulos añadidos en la capa «{AddSubtitlesCommand.LayerName}». Revísalos: cada uno es un texto que puedes corregir.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Generación de subtítulos cancelada.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            SetStatus("No se pudieron generar los subtítulos: " + ex.Message);
        }
        finally
        {
            _subtitleCancellation = null;
            SetSubtitlesBusy(false);
        }
    }
}
