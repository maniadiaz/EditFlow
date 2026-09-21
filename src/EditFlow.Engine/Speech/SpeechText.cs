// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace EditFlow.Engine.Speech;

/// <summary>Un fragmento de voz transcrito: qué se dice y entre qué instantes.</summary>
/// <param name="Start">Inicio, medido desde el principio del audio.</param>
/// <param name="End">Fin.</param>
/// <param name="Text">Lo que se dice, sin saltos de línea.</param>
public sealed record SpeechSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Lee la salida de Whisper en formato SRT y la deja lista para usarse como subtítulos.</summary>
public static partial class SubtitleParser
{
    /// <summary>Duración mínima de un subtítulo: por debajo no da tiempo a leerlo.</summary>
    public static TimeSpan MinimumDuration { get; } = TimeSpan.FromMilliseconds(200);

    [GeneratedRegex(@"(\d+):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{3})")]
    private static partial Regex TimeLine();

    // Etiquetas de formato que Whisper deja en el texto al cantar (<i>…</i>, <b>…</b>) y códigos de posición de
    // subtítulos ({\an8}): no son parte de lo que se dice y saldrían escritas tal cual en pantalla.
    [GeneratedRegex(@"</?\s*[a-zA-Z][^>]*>|\{\\[^}]*\}")]
    private static partial Regex MarkupTags();

    /// <summary>Quita las etiquetas de formato (<c>&lt;i&gt;</c>, <c>{n8}</c>…) de un texto.</summary>
    public static string StripMarkup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MarkupTags().Replace(text, string.Empty);
    }

    // Marcas que Whisper escribe para lo que no es voz: [MUSIC], (aplausos), *risas*…
    [GeneratedRegex(@"^\s*[\[\(\*].*[\]\)\*]\s*$")]
    private static partial Regex NonSpeech();

    /// <summary>Interpreta un archivo SRT completo.</summary>
    public static IReadOnlyList<SpeechSegment> ParseSrt(string srt) => ParseSrt(srt, out _);

    /// <summary>Interpreta un archivo SRT completo y dice cuántos fragmentos traía antes de limpiarlos.</summary>
    /// <param name="srt">Contenido del archivo.</param>
    /// <param name="detected">Fragmentos que Whisper devolvió, incluidos los de música o sonidos.</param>
    public static IReadOnlyList<SpeechSegment> ParseSrt(string srt, out int detected)
    {
        ArgumentNullException.ThrowIfNull(srt);

        var raw = new List<SpeechSegment>();
        var lines = srt.ReplaceLineEndings("\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimeLine().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var text = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                text.Add(lines[i].Trim());
            }

            raw.Add(new SpeechSegment(
                ToTime(match.Groups, 1),
                ToTime(match.Groups, 5),
                string.Join(' ', text)));
        }

        detected = raw.Count;
        return Clean(raw);
    }

    private static TimeSpan ToTime(GroupCollection groups, int first) => new(
        0,
        int.Parse(groups[first].Value, CultureInfo.InvariantCulture),
        int.Parse(groups[first + 1].Value, CultureInfo.InvariantCulture),
        int.Parse(groups[first + 2].Value, CultureInfo.InvariantCulture),
        int.Parse(groups[first + 3].Value, CultureInfo.InvariantCulture));

    // Etiquetas que Whisper escribe para lo que no es voz, a veces entre ♪ en lugar de entre corchetes.
    private static readonly HashSet<string> MusicLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "música", "musica", "musique", "musik", "instrumental", "singing", "cantando",
        "applause", "aplausos", "laughter", "risas", "silence", "silencio",
    };

    private static bool IsMusicLabel(string text) =>
        MusicLabels.Contains(new string(text.Where(char.IsLetter).ToArray()));

    /// <summary>
    /// Reduce a una las repeticiones seguidas de una misma frase, típicas de Whisper cuando no oye nada claro.
    /// </summary>
    /// <remarks>
    /// «It's fine, it's fine, it's fine, it's fine» pasa a «It's fine». Solo se toca si la frase (de 1 a 8
    /// palabras) se repite <b>tres o más veces seguidas</b>: dos veces es algo que una persona sí dice.
    /// </remarks>
    public static string CollapseRepetitions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        static string Key(string word) => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        for (var size = 1; size <= 8; size++)
        {
            for (var i = 0; i + (3 * size) <= words.Count; i++)
            {
                var runs = 1;
                while (i + ((runs + 1) * size) <= words.Count
                       && Enumerable.Range(0, size).All(k => Key(words[i + k]) == Key(words[i + (runs * size) + k])))
                {
                    runs++;
                }

                if (runs >= 3)
                {
                    words.RemoveRange(i + size, (runs - 1) * size);
                }
            }
        }

        var joined = string.Join(' ', words);
        if (joined.Length == text.Length)
        {
            return text;
        }

        // Al quitar repeticiones puede quedar una coma donde antes acababa la frase con punto.
        joined = joined.TrimEnd(',', ';', ' ');
        var last = text.TrimEnd()[^1];
        return last is '.' or '!' or '?' && !joined.EndsWith(last) ? joined + last : joined;
    }

    /// <summary>
    /// Quita lo que no sirve como subtítulo y arregla los tiempos para que se puedan colocar en una capa.
    /// </summary>
    /// <remarks>
    /// Whisper a veces repite una frase varias veces seguidas cuando hay silencio, escribe marcas como
    /// <c>[MUSIC]</c> y emite fragmentos de duración cero. Además los subtítulos de una misma capa no
    /// pueden solaparse: si uno acaba después de que empiece el siguiente, se recorta.
    /// </remarks>
    public static IReadOnlyList<SpeechSegment> Clean(IEnumerable<SpeechSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var kept = new List<SpeechSegment>();

        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            // Los ♪ que Whisper pone al cantar son decoración: se quita, y si no queda ninguna palabra se descarta.
            var text = Regex.Replace(StripMarkup(segment.Text).Replace('♪', ' ').Replace('¶', ' '), @"\s+", " ").Trim();
            text = CollapseRepetitions(text);

            if (text.Length == 0 || NonSpeech().IsMatch(text) || !text.Any(char.IsLetterOrDigit) || IsMusicLabel(text))
            {
                continue;
            }

            if (kept.Count > 0 && string.Equals(kept[^1].Text, text, StringComparison.OrdinalIgnoreCase))
            {
                // La misma frase otra vez: alarga la anterior en lugar de duplicarla.
                kept[^1] = kept[^1] with { End = segment.End > kept[^1].End ? segment.End : kept[^1].End };
                continue;
            }

            kept.Add(segment with { Text = text });
        }

        // Sin solapes, y con la duración mínima siempre que quepa antes del siguiente.
        var result = new List<SpeechSegment>();
        for (var i = 0; i < kept.Count; i++)
        {
            var segment = kept[i];
            var limit = i + 1 < kept.Count ? kept[i + 1].Start : TimeSpan.MaxValue;
            var end = segment.End < limit ? segment.End : limit;

            if (end - segment.Start < MinimumDuration)
            {
                end = segment.Start + MinimumDuration;
                if (end > limit)
                {
                    continue;   // no cabe: es un fragmento espurio pegado al siguiente
                }
            }

            result.Add(segment with { End = end });
        }

        return result;
    }
}
