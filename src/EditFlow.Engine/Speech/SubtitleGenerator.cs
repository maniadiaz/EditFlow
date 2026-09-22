// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;

namespace EditFlow.Engine.Speech;

/// <summary>Un idioma de audio o de subtítulos.</summary>
/// <param name="Code">Código ISO (<c>es</c>, <c>en</c>…) o <c>auto</c> para que lo detecte.</param>
/// <param name="Label">Nombre que se muestra.</param>
/// <param name="EnglishName">Nombre en inglés, que es como se le nombra al traductor.</param>
public sealed record SpeechLanguage(string Code, string Label, string EnglishName)
{
    /// <summary>Idiomas ofrecidos para el audio; «Automático» deja que Whisper lo detecte por los primeros segundos.</summary>
    public static IReadOnlyList<SpeechLanguage> All { get; } =
    [
        new("auto", "Automático", "the original language"),
        new("es", "Español", "Spanish"),
        new("en", "English", "English"),
        new("pt", "Português", "Portuguese"),
        new("fr", "Français", "French"),
        new("de", "Deutsch", "German"),
        new("it", "Italiano", "Italian"),
        new("ja", "日本語", "Japanese"),
    ];

    /// <summary>Idiomas a los que se puede traducir: los mismos, sin «Automático».</summary>
    public static IReadOnlyList<SpeechLanguage> Targets { get; } = All.Where(l => l.Code != "auto").ToList();

    /// <summary>Busca un idioma por su código, o <see langword="null"/> si no está entre los ofrecidos.</summary>
    public static SpeechLanguage? FindByCode(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase) && l.Code != "auto");
}

/// <summary>Lo que devolvió una transcripción.</summary>
/// <param name="Segments">Fragmentos de voz ya limpios, listos para usarse como subtítulos.</param>
/// <param name="Detected">Fragmentos que Whisper devolvió en total, incluidos los de música o sonidos sueltos.</param>
/// <param name="Language">Código del idioma en que se habla (el indicado, o el que Whisper detectó).</param>
public sealed record SubtitleResult(IReadOnlyList<SpeechSegment> Segments, int Detected, string? Language = null);

/// <summary>
/// Genera subtítulos a partir del sonido del montaje, transcribiéndolo en el propio equipo con Whisper.
/// </summary>
/// <remarks>
/// El audio que se transcribe es la mezcla del preview (video, música y demás pistas, con sus
/// volúmenes): lo que se oye es lo que se transcribe. Nada sale del equipo.
/// </remarks>
public sealed partial class SubtitleGenerator
{
    private readonly FFmpegTools _tools;
    private readonly string _root;

    /// <summary>Crea el generador.</summary>
    /// <param name="tools">Ejecutables de FFmpeg.</param>
    /// <param name="root">Carpeta de instalación de Whisper; por defecto la de datos del usuario.</param>
    public SubtitleGenerator(FFmpegTools tools, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
        _root = root ?? WhisperSetup.DefaultRoot;
    }

    [GeneratedRegex(@"progress\s*=\s*(\d+)\s*%")]
    private static partial Regex ProgressLine();

    /// <summary>Argumentos de whisper-cli. Expuestos para poder comprobarlos.</summary>
    public static IReadOnlyList<string> BuildArguments(string modelPath, string audioPath, string outputPrefix, string language, int threads) =>
    [
        "-m", modelPath,
        "-f", audioPath,
        "-l", language,
        "-t", threads.ToString(CultureInfo.InvariantCulture),
        "-osrt",

        // Además del SRT, un JSON con el idioma detectado: hace falta para saber si hay que traducir.
        "-oj",
        "-of", outputPrefix,

        // -np: nada de ruido en la salida; -pp: avance por porcentaje.
        "-np", "-pp",

        // Sin arrastrar el contexto de la frase anterior: es lo que más reduce las repeticiones que
        // Whisper genera en los silencios.
        "-mc", "0",

        // Líneas de hasta 42 caracteres cortadas entre palabras, que es lo que cabe cómodo en pantalla.
        "-ml", "42", "-sow",
    ];

    /// <summary>Transcribe el montaje y devuelve los fragmentos de voz con sus tiempos.</summary>
    /// <param name="sequence">Montaje del que se toma el sonido.</param>
    /// <param name="model">Modelo a usar; debe estar descargado.</param>
    /// <param name="language">Código de idioma o <c>auto</c>.</param>
    /// <param name="progress">Avance de 0 a 1.</param>
    /// <exception cref="InvalidOperationException">Si Whisper o el modelo no están instalados, o algo falla.</exception>
    public async Task<SubtitleResult> GenerateAsync(
        EditSequence sequence,
        WhisperModel model,
        string language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        var cli = WhisperSetup.LocateCli(_root)
            ?? throw new InvalidOperationException("Whisper no está instalado.");
        var modelPath = WhisperSetup.ModelPath(model, _root);
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException($"El modelo «{model.Label}» no está descargado.");
        }

        var work = Path.Combine(Path.GetTempPath(), "editflow-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            // 1. La mezcla de audio del montaje (la misma que se oye en el preview).
            var mix = await new PreviewMixRenderer(_tools)
                .RenderAsync(sequence, Path.Combine(work, "mix.flac"), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(0.05);

            // 2. Whisper trabaja con 16 kHz mono.
            var wav = Path.Combine(work, "audio.wav");
            var convert = await ProcessRunner.RunAsync(
                _tools.FFmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", mix.Path, "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav],
                cancellationToken).ConfigureAwait(false);

            if (!convert.Succeeded || !File.Exists(wav))
            {
                throw new InvalidOperationException("No se pudo preparar el audio para transcribirlo: " + convert.StandardError.Trim());
            }

            progress?.Report(0.08);

            // 3. La transcripción.
            var prefix = Path.Combine(work, "subtitles");
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            await RunWhisperAsync(
                cli,
                BuildArguments(modelPath, wav, prefix, language, threads),
                fraction => progress?.Report(0.08 + (fraction * 0.92)),
                cancellationToken).ConfigureAwait(false);

            var srt = prefix + ".srt";
            var spoken = language != "auto" ? language : await ReadDetectedLanguageAsync(prefix + ".json", cancellationToken).ConfigureAwait(false);
            if (!File.Exists(srt))
            {
                return new SubtitleResult([], 0, spoken);
            }

            var segments = SubtitleParser.ParseSrt(
                await File.ReadAllTextAsync(srt, cancellationToken).ConfigureAwait(false), out var detected);

            // 4. Colocarlos justo donde se habla, con el detector de voz.
            segments = await AlignWithSpeechAsync(segments, wav, cancellationToken).ConfigureAwait(false);

            progress?.Report(1);
            return new SubtitleResult(segments, detected, spoken);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Un archivo aún en uso: es temporal, lo limpiará el sistema.
            }
        }
    }

    private static async Task<string?> ReadDetectedLanguageAsync(string jsonPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(jsonPath))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false));

            return document.RootElement.TryGetProperty("result", out var result)
                   && result.TryGetProperty("language", out var code)
                ? code.GetString()
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<SpeechSegment>> AlignWithSpeechAsync(
        IReadOnlyList<SpeechSegment> segments, string wav, CancellationToken cancellationToken)
    {
        var vadCli = WhisperSetup.LocateVadCli(_root);
        var vadModel = WhisperSetup.VadModelPath(_root);
        if (segments.Count == 0 || vadCli is null || !File.Exists(vadModel))
        {
            return segments;
        }

        try
        {
            // Un poco más sensible que el 0,5 de fábrica: perder una palabra suave es peor que dejar pasar un ruido.
            var result = await ProcessRunner.RunAsync(
                vadCli,
                ["-vm", vadModel, "-f", wav, "-vt", "0.4", "-np"],
                cancellationToken).ConfigureAwait(false);

            return result.Succeeded
                ? SubtitleParser.AlignToSpeech(segments, SubtitleParser.ParseSpeechRegions(result.StandardOutput))
                : segments;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Sin el detector los subtítulos salen igual, con los tiempos de Whisper.
            return segments;
        }
    }

    private static async Task RunWhisperAsync(
        string cli, IReadOnlyList<string> arguments, Action<double> onProgress, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cli,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var errors = new System.Text.StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (ProgressLine().Match(e.Data) is { Success: true } match)
            {
                onProgress(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0);
            }
            else if (errors.Length < 4000)
            {
                errors.AppendLine(e.Data);
            }
        };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Ya había terminado.
            }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Whisper terminó con error: " + errors.ToString().Trim());
        }
    }
}
