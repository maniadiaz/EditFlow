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

// Subtítulos automáticos: transcribir el sonido del montaje con Whisper, en el propio equipo, traducirlos si se
// pide, y dejar el resultado como textos editables en la capa «Sub».
public partial class MainWindow
{
    private CancellationTokenSource? _subtitleCancellation;

    private WhisperModel SelectedSubtitleModel =>
        WhisperModel.All[Math.Max(SubtitleModelBox.SelectedIndex, 0)];

    private SpeechLanguage SelectedSubtitleLanguage =>
        SpeechLanguage.All[Math.Max(SubtitleLanguageBox.SelectedIndex, 0)];

    /// <summary>Idioma al que traducir, o <see langword="null"/> para dejarlos en el idioma del audio.</summary>
    private SpeechLanguage? SelectedTargetLanguage =>
        SubtitleTargetBox.SelectedIndex <= 0 ? null : SpeechLanguage.Targets[SubtitleTargetBox.SelectedIndex - 1];

    private void WireSubtitles()
    {
        foreach (var language in SpeechLanguage.All)
        {
            SubtitleLanguageBox.Items.Add(language.Label);
        }

        SubtitleTargetBox.Items.Add("Igual que el audio");
        foreach (var language in SpeechLanguage.Targets)
        {
            SubtitleTargetBox.Items.Add(language.Label);
        }

        foreach (var model in WhisperModel.All)
        {
            SubtitleModelBox.Items.Add(model.Label);
        }

        SubtitleLanguageBox.SelectedIndex = 0;
        SubtitleTargetBox.SelectedIndex = 0;

        // Si el modelo preciso ya está descargado, es el que se propone: transcribe bastante mejor.
        SubtitleModelBox.SelectedIndex = WhisperSetup.HasModel(WhisperModel.Small) ? 1 : 0;

        SubtitleModelBox.SelectionChanged += (_, _) => RefreshSubtitleSetup();
        SubtitleLanguageBox.SelectionChanged += (_, _) => RefreshSubtitleSetup();
        SubtitleTargetBox.SelectionChanged += (_, _) => RefreshSubtitleSetup();
        GenerateSubtitlesButton.Click += async (_, _) => await GenerateSubtitlesAsync();
        CancelSubtitlesButton.Click += (_, _) => _subtitleCancellation?.Cancel();

        RefreshSubtitleSetup();
    }

    private static string Megabytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Si hay que traducir seguro (el audio está en un idioma elegido y es distinto del de los subtítulos).
    /// Con el audio en «Automático» no se sabe hasta transcribir: entonces se decide después.
    /// </summary>
    private bool TranslationCertain =>
        SelectedTargetLanguage is { } target
        && SelectedSubtitleLanguage.Code != "auto"
        && !string.Equals(SelectedSubtitleLanguage.Code, target.Code, StringComparison.OrdinalIgnoreCase);

    private static bool TranslatorInstalled =>
        TranslationSetup.LocateServer() is not null && TranslationSetup.HasModel(TranslationModel.Default);

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

        var translation = string.Empty;
        if (SelectedTargetLanguage is { } target)
        {
            var translatorBytes = (TranslationSetup.LocateServer() is null ? TranslationSetup.RuntimeDownloadBytes : 0)
                + (TranslationSetup.HasModel(TranslationModel.Default) ? 0 : TranslationModel.Default.Bytes);

            if (translatorBytes == 0)
            {
                translation = $" El traductor ya está instalado: si el audio no está en {target.Label}, se traducirá.";
            }
            else if (TranslationCertain)
            {
                bytes += translatorBytes;
                translation = $" Traducir a {target.Label} usa el modelo {TranslationModel.Default.Label} ({Megabytes(translatorBytes)}, una sola vez).";
            }
            else
            {
                translation = $" Si el audio no está en {target.Label}, se descargará además el traductor ({Megabytes(translatorBytes)}, una sola vez).";
            }
        }

        if (bytes == 0)
        {
            SubtitleSetupNote.Text = $"Whisper y el modelo «{model.Label}» ya están instalados.{translation}";
            GenerateSubtitlesButton.Content = "Generar subtítulos";
        }
        else
        {
            SubtitleSetupNote.Text = "La primera vez se descarga lo necesario (código abierto, guardado en tu carpeta de datos, " +
                                     $"una sola vez): unos {Megabytes(bytes)} en total.{translation}";
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
        SubtitleTargetBox.IsEnabled = !busy;
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

        // Transcribir compite por CPU con la decodificación del preview —Whisper corre en el
        // mismo equipo, sin límite de prioridad—, y al terminar el cabezal salta al primer
        // subtítulo. Seguir reproduciendo mientras tanto solo daba un video a tirones que
        // además cambiaba de sitio sin avisar: se para antes de empezar.
        StopPlayback();

        var model = SelectedSubtitleModel;
        var language = SelectedSubtitleLanguage;
        var target = SelectedTargetLanguage;

        using var cancellation = new CancellationTokenSource();
        _subtitleCancellation = cancellation;
        SubtitleResultBox.IsVisible = false;
        SetSubtitlesBusy(true);

        // Cada etapa ocupa un tramo de la barra de progreso.
        IProgress<double> Stage(string text, double from, double to) => new Progress<double>(p =>
            Dispatcher.UIThread.Post(() =>
            {
                SubtitleProgress.Value = (from + ((to - from) * p)) * 100;
                SetStatus($"{text} {(p * 100).ToString("0", CultureInfo.InvariantCulture)} %");
            }));

        var warning = string.Empty;

        try
        {
            // 1. Whisper y su modelo, si faltan.
            var needRuntime = !WhisperSetup.IsRuntimeInstalled();
            var needModel = !WhisperSetup.HasModel(model);
            var willTranslate = TranslationCertain;

            var transcribeEnd = willTranslate ? 0.6 : 1.0;
            var downloadEnd = needRuntime || needModel ? 0.15 : 0.0;

            if (needRuntime)
            {
                await WhisperSetup.InstallRuntimeAsync(
                    Stage("Descargando Whisper…", 0, needModel ? 0.03 : downloadEnd), cancellationToken: cancellation.Token);
            }

            if (needModel)
            {
                await WhisperSetup.InstallModelAsync(
                    model, Stage($"Descargando el modelo «{model.Label}»…", needRuntime ? 0.03 : 0, downloadEnd),
                    cancellationToken: cancellation.Token);
            }

            // 2. La transcripción.
            var generator = new SubtitleGenerator(_tools);
            var result = await generator.GenerateAsync(
                Edit, model, language.Code, Stage("Transcribiendo…", downloadEnd, transcribeEnd), cancellation.Token);

            var segments = result.Segments;
            SpeechLanguage? translatedTo = null;

            // 3. Traducirlos, si se pidió y el audio no está ya en ese idioma.
            var spoken = SpeechLanguage.FindByCode(result.Language);
            if (segments.Count > 0
                && target is not null
                && !string.Equals(result.Language, target.Code, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var translateStart = transcribeEnd;

                    if (!TranslatorInstalled)
                    {
                        await InstallTranslatorAsync(Stage, transcribeEnd, transcribeEnd + ((1 - transcribeEnd) * 0.6), cancellation.Token);
                        translateStart = transcribeEnd + ((1 - transcribeEnd) * 0.6);
                    }

                    segments = await new SubtitleTranslator().TranslateAsync(
                        segments,
                        spoken?.EnglishName,
                        target.EnglishName,
                        Stage($"Traduciendo a {target.Label}…", translateStart, 1.0),
                        cancellation.Token);
                    translatedTo = target;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.Net.Http.HttpRequestException)
                {
                    // Los subtítulos ya transcritos no se pierden por un fallo al traducirlos.
                    warning = $" No se pudieron traducir a {target.Label} ({ex.Message}); se dejaron en el idioma del audio.";
                }
            }

            // 4. Colocarlos como textos editables, en un solo paso del historial.
            var added = Timeline.AddSubtitles(segments.Select(s => new SubtitleCue(s.Start, s.End, s.Text)));

            if (added.Added == 0)
            {
                // Tres motivos distintos para no añadir nada, y cada uno pide una explicación distinta: sin
                // esto, regenerar sobre un montaje que ya tenía subtítulos (todos chocan con los que ya
                // había) mostraba el mismo mensaje que cuando Whisper de verdad no encontró voz, que es
                // mucho más confuso: parece que la transcripción falló cuando en realidad funcionó.
                var message = segments.Count == 0
                    ? result.Detected == 0
                        ? "No se detectó ningún sonido que transcribir. Comprueba que el montaje tenga audio y que no esté silenciado."
                        : $"No se encontró voz que transcribir: Whisper solo detectó música o sonidos sueltos ({result.Detected} " +
                          "fragmentos, ninguno con palabras). Si sí hay voz, prueba con el modelo «Small», o elige el idioma en lugar " +
                          "de «Automático». Si el video es solo música, no habrá subtítulos."
                    : $"Se transcribieron {segments.Count} líneas, pero ninguna cupo: ya había subtítulos en esos instantes " +
                      $"en la capa «{AddSubtitlesCommand.LayerName}». Borra o mueve los que ya tienes y vuelve a generarlos.";

                ShowSubtitleResult(message, success: false);
                return;
            }

            // Llevar el cabezal al primero para que se vea dónde quedaron: con un video largo pueden empezar
            // mucho después del principio y nada a la vista lo indicaría.
            var first = added.FirstStart ?? TimeSpan.Zero;
            SeekTo(first);

            var skipped = added.Skipped > 0
                ? $" {added.Skipped} no cupieron porque ya había subtítulos en ese instante."
                : string.Empty;

            var how = translatedTo is not null
                ? $" Traducidos{(spoken is not null ? $" del {NameInSpanish(spoken)}" : string.Empty)} al {NameInSpanish(translatedTo)}."
                : spoken is not null ? $" Idioma del audio: {NameInSpanish(spoken)}." : string.Empty;

            ShowSubtitleResult(
                $"✓ {added.Added} subtítulos añadidos en la capa «{AddSubtitlesCommand.LayerName}»; el primero empieza en " +
                $"{Controls.TimelineControl.FormatClock(first)}.{how}{skipped}{warning} Cada uno es un texto que puedes corregir.",
                success: warning.Length == 0);
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

    /// <summary>Nombre del idioma para una frase en español («inglés», no «English»).</summary>
    private static string NameInSpanish(SpeechLanguage language) => language.Code switch
    {
        "es" => "español",
        "en" => "inglés",
        "pt" => "portugués",
        "fr" => "francés",
        "de" => "alemán",
        "it" => "italiano",
        "ja" => "japonés",
        _ => language.Label,
    };

    /// <summary>Descarga el servidor de llama.cpp y el modelo de traducción, lo que falte.</summary>
    private static async Task InstallTranslatorAsync(
        Func<string, double, double, IProgress<double>> stage, double from, double to, CancellationToken cancellationToken)
    {
        var needRuntime = TranslationSetup.LocateServer() is null;
        var needModel = !TranslationSetup.HasModel(TranslationModel.Default);
        var total = (needRuntime ? TranslationSetup.RuntimeDownloadBytes : 0) + (needModel ? TranslationModel.Default.Bytes : 0);
        if (total == 0)
        {
            return;
        }

        var runtimeEnd = from + ((to - from) * (needRuntime ? (double)TranslationSetup.RuntimeDownloadBytes / total : 0));

        if (needRuntime)
        {
            await TranslationSetup.InstallRuntimeAsync(
                stage("Descargando el traductor…", from, runtimeEnd), cancellationToken: cancellationToken);
        }

        if (needModel)
        {
            await TranslationSetup.InstallModelAsync(
                TranslationModel.Default,
                stage($"Descargando el modelo de traducción ({Megabytes(TranslationModel.Default.Bytes)})…", runtimeEnd, to),
                cancellationToken: cancellationToken);
        }
    }
}
