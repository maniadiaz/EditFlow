// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EditFlow.Engine.Speech;

/// <summary>
/// Traduce los subtítulos con un modelo de lenguaje que corre en el propio equipo (llama.cpp).
/// </summary>
/// <remarks>
/// <para>
/// Se arranca un servidor local de llama.cpp, que solo escucha en 127.0.0.1, se le pasan los subtítulos por
/// tandas y se apaga al terminar. Nada sale del equipo.
/// </para>
/// <para>
/// Cada tanda lleva las líneas anteriores como contexto (sin traducirlas), para que una frase partida en dos
/// subtítulos se traduzca coherente, y el modelo devuelve una línea numerada por cada una. Si una tanda no
/// devuelve todas sus líneas se reintenta más pequeña; lo que aun así falle se deja en el idioma original en
/// lugar de perder el subtítulo.
/// </para>
/// </remarks>
public sealed partial class SubtitleTranslator
{
    private const int BatchSize = 12;
    private const int ContextLines = 3;

    private readonly string _root;
    private readonly TranslationModel _model;

    /// <summary>Crea el traductor.</summary>
    /// <param name="root">Carpeta de instalación; por defecto la de datos del usuario.</param>
    /// <param name="model">Modelo a usar; por defecto el recomendado.</param>
    public SubtitleTranslator(string? root = null, TranslationModel? model = null)
    {
        _root = root ?? WhisperSetup.DefaultRoot;
        _model = model ?? TranslationModel.Default;
    }

    [GeneratedRegex(@"^\s*(\d+)\s*[\.\):\-]\s*(.*?)\s*$")]
    private static partial Regex NumberedLine();

    /// <summary>Instrucciones al modelo. Expuestas para poder comprobarlas.</summary>
    public static string BuildSystemPrompt(string? sourceLanguage, string targetLanguage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var from = string.IsNullOrWhiteSpace(sourceLanguage) ? "the original language" : sourceLanguage;
        var spanishNote = targetLanguage.StartsWith("Spanish", StringComparison.OrdinalIgnoreCase)
            ? " Use neutral Latin American Spanish: address people with \"tú\" or \"usted\", never \"vosotros\"."
            : string.Empty;

        return $"You are a professional subtitle translator. Translate the numbered subtitle lines from {from} into {targetLanguage}. " +
               "They are spoken dialogue from a video, and some sentences are split across consecutive lines. " +
               "Rules: keep the meaning, tone and register natural for spoken dialogue; keep lines short like subtitles; " +
               "keep proper names and titles unchanged; do not add explanations, do not merge or split lines, do not translate " +
               "the lines marked as context." + spanishNote + " " +
               "Answer with exactly one line per numbered line, formatted \"N. translation\", and nothing else.";
    }

    /// <summary>Lee una respuesta del modelo con líneas numeradas.</summary>
    /// <returns>Traducción de cada número pedido; los que faltan no aparecen.</returns>
    public static IReadOnlyDictionary<int, string> ParseReply(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var result = new Dictionary<int, string>();
        foreach (var line in reply.ReplaceLineEndings("\n").Split('\n'))
        {
            var match = NumberedLine().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var number))
            {
                var text = SubtitleParser.StripMarkup(match.Groups[2].Value).Trim().Trim('"', '“', '”');
                if (text.Length > 0 && !result.ContainsKey(number))
                {
                    result[number] = text;
                }
            }
        }

        return result;
    }

    /// <summary>Traduce los subtítulos conservando sus tiempos.</summary>
    /// <param name="segments">Subtítulos en el idioma original.</param>
    /// <param name="sourceLanguage">Nombre en inglés del idioma original (<c>English</c>…), o <see langword="null"/> si se desconoce.</param>
    /// <param name="targetLanguage">Nombre en inglés del idioma al que traducir.</param>
    /// <param name="progress">Avance de 0 a 1.</param>
    /// <exception cref="InvalidOperationException">Si el traductor no está instalado o el servidor no arranca.</exception>
    public async Task<IReadOnlyList<SpeechSegment>> TranslateAsync(
        IReadOnlyList<SpeechSegment> segments,
        string? sourceLanguage,
        string targetLanguage,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        if (segments.Count == 0)
        {
            return segments;
        }

        var server = TranslationSetup.LocateServer(_root)
            ?? throw new InvalidOperationException("El traductor no está instalado.");
        var modelPath = TranslationSetup.ModelPath(_model, _root);
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException("El modelo de traducción no está descargado.");
        }

        var port = FreePort();
        using var process = StartServer(server, modelPath, port);
        using var registration = cancellationToken.Register(() => Kill(process));

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromMinutes(10),
            };

            await WaitUntilReadyAsync(client, process, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.05);

            return await TranslateOverHttpAsync(client, segments, sourceLanguage, targetLanguage, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>Traduce por tandas usando un servidor ya en marcha. Aparte para poder probarlo sin llama.cpp.</summary>
    internal static async Task<IReadOnlyList<SpeechSegment>> TranslateOverHttpAsync(
        HttpClient client,
        IReadOnlyList<SpeechSegment> segments,
        string? sourceLanguage,
        string targetLanguage,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var system = BuildSystemPrompt(sourceLanguage, targetLanguage);
        var translated = new string[segments.Count];

        for (var start = 0; start < segments.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(BatchSize, segments.Count - start);
            await TranslateBatchAsync(client, system, segments, translated, start, count, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(0.05 + (0.95 * (start + count) / segments.Count));
        }

        return segments
            .Select((s, i) => s with { Text = string.IsNullOrWhiteSpace(translated[i]) ? s.Text : translated[i] })
            .ToList();
    }

    private static async Task TranslateBatchAsync(
        HttpClient client,
        string system,
        IReadOnlyList<SpeechSegment> segments,
        string[] translated,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        var reply = await AskAsync(client, system, segments, translated, start, count, cancellationToken).ConfigureAwait(false);

        // Lo que no volvió se reintenta línea a línea, con el mismo contexto; si aun así no, queda el original.
        for (var i = 0; i < count; i++)
        {
            if (reply.TryGetValue(i + 1, out var text))
            {
                translated[start + i] = text;
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (!string.IsNullOrWhiteSpace(translated[start + i]))
            {
                continue;
            }

            var single = await AskAsync(client, system, segments, translated, start + i, 1, cancellationToken).ConfigureAwait(false);
            if (single.TryGetValue(1, out var text))
            {
                translated[start + i] = text;
            }
        }
    }

    private static async Task<IReadOnlyDictionary<int, string>> AskAsync(
        HttpClient client,
        string system,
        IReadOnlyList<SpeechSegment> segments,
        string[] translated,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        var user = new System.Text.StringBuilder();

        // Las líneas de antes van como contexto, ya traducidas si se pudo: ayudan a mantener nombres y género.
        var contextStart = Math.Max(0, start - ContextLines);
        if (contextStart < start)
        {
            user.AppendLine("Context (already handled, do not translate):");
            for (var i = contextStart; i < start; i++)
            {
                user.AppendLine(segments[i].Text);
            }

            user.AppendLine().AppendLine("Translate these lines:");
        }

        for (var i = 0; i < count; i++)
        {
            user.Append(CultureInfo.InvariantCulture, $"{i + 1}. {segments[start + i].Text}").AppendLine();
        }

        var request = new ChatRequest(
            [new ChatMessage("system", system), new ChatMessage("user", user.ToString())],
            Temperature: 0.2,
            MaxTokens: 160 * count + 200,
            TemplateKwargs: new ChatTemplateOptions(EnableThinking: false));

        // Con el contexto generado, y no por reflexión: al publicar con recorte, la serialización
        // por reflexión está desactivada y esto lanzaría en cuanto alguien tradujera un subtítulo.
        using var response = await client
            .PostAsJsonAsync("v1/chat/completions", request, TranslationJson.Default.ChatRequest, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync(TranslationJson.Default.ChatResponse, cancellationToken)
            .ConfigureAwait(false);
        return ParseReply(body?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty);
    }

    internal sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

    internal sealed record ChatRequest(
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("chat_template_kwargs")] ChatTemplateOptions TemplateKwargs);

    /// <summary>
    /// Opciones que el servidor pasa a la plantilla de chat del modelo.
    /// </summary>
    /// <remarks>
    /// Era un <c>Dictionary&lt;string, object&gt;</c>, que es lo cómodo de escribir pero lo que el
    /// generador de serialización no puede resolver: un <c>object</c> no tiene metadatos, y al
    /// publicar fallaba al enviar la petición. Con un tipo propio se sabe en compilación qué va
    /// dentro, y de paso queda dicho.
    ///
    /// <c>enable_thinking</c> apaga el razonamiento en voz alta de Qwen3: aquí solo se quiere la
    /// traducción, y el modelo devolvería además su cadena de pensamiento.
    /// </remarks>
    internal sealed record ChatTemplateOptions(
        [property: JsonPropertyName("enable_thinking")] bool EnableThinking);

    internal sealed record ChatChoice([property: JsonPropertyName("message")] ChatMessage? Message);

    internal sealed record ChatResponse([property: JsonPropertyName("choices")] ChatChoice[]? Choices);

    // ---------------------------------------------------------------- servidor

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartServer(string server, string modelPath, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = server,
            WorkingDirectory = Path.GetDirectoryName(server)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Un solo hueco y un contexto corto: cada tanda es pequeña, y así el modelo ocupa lo mínimo de memoria.
        foreach (var argument in new[]
                 {
                     "-m", modelPath,
                     "--host", "127.0.0.1",
                     "--port", port.ToString(CultureInfo.InvariantCulture),
                     "-c", "3072",
                     "-np", "1",
                     "-t", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(CultureInfo.InvariantCulture),
                     "--no-webui",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        // El servidor escribe mucho en la salida; sin leerla se llenaría el búfer y se quedaría parado.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitUntilReadyAsync(HttpClient client, Process process, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Cargar un modelo de casi 3 GB tarda: de unos segundos a más de un minuto según el disco.
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(4))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException("El traductor se cerró al arrancar. ¿Hay memoria suficiente libre?");
            }

            try
            {
                using var health = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
                if (health.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Aún arrancando.
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("El traductor tardó demasiado en arrancar.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
    }
}

/// <summary>Contexto de serialización generado en compilación para la API de traducción.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SubtitleTranslator.ChatRequest))]
[JsonSerializable(typeof(SubtitleTranslator.ChatResponse))]
internal sealed partial class TranslationJson : JsonSerializerContext;
