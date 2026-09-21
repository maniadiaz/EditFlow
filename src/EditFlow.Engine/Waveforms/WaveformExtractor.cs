// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;

namespace EditFlow.Engine.Waveforms;

/// <summary>Lee el audio de un archivo y lo resume en picos de amplitud.</summary>
public sealed class WaveformExtractor
{
    /// <summary>Picos por segundo de audio. 100 da un pico cada 10 ms.</summary>
    public const int PeaksPerSecond = 100;

    // A 8 kHz cada pico resume 80 muestras. La forma de onda no necesita más: es un dibujo,
    // no un análisis, y decodificar a una frecuencia baja es mucho más rápido.
    private const int SampleRate = 8000;

    private readonly FFmpegTools _tools;

    /// <summary>Crea un extractor que usará los ejecutables indicados.</summary>
    public WaveformExtractor(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>Argumentos de FFmpeg que producen PCM mono de 16 bits por la salida estándar.</summary>
    public static IReadOnlyList<string> BuildArguments(string path) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-i", path,
        "-vn", "-map", "0:a:0",
        "-ac", "1", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "s16le",
        "pipe:1",
    ];

    /// <summary>Extrae los picos de un archivo.</summary>
    /// <returns>Un byte por pico, de 0 (silencio) a 255 (máxima amplitud).</returns>
    /// <exception cref="InvalidOperationException">Si FFmpeg no puede leer el audio.</exception>
    public async Task<byte[]> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var startInfo = new ProcessStartInfo
        {
            FileName = _tools.FFmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in BuildArguments(path))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("No se pudo iniciar FFmpeg.");
        }

        // stderr se lee a la vez que stdout: si nadie lo vaciara y FFmpeg escribiera mucho,
        // su búfer se llenaría y el proceso se bloquearía esperando, con nosotros esperando
        // a su vez la salida estándar.
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var accumulator = new PeakAccumulator(SampleRate / PeaksPerSecond);

        try
        {
            var buffer = new byte[64 * 1024];
            var stream = process.StandardOutput.BaseStream;

            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                accumulator.Add(buffer.AsSpan(0, read));
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var message = (await errors.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException(
                "No se pudo leer el audio: " + (message.Length > 0 ? message : "FFmpeg terminó con error."));
        }

        return accumulator.Finish();
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
