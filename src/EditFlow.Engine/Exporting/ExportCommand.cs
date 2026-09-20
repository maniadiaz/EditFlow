// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Comando de FFmpeg listo para ejecutar, con los recursos temporales que necesita.
/// </summary>
/// <remarks>
/// Implementa <see cref="IDisposable"/> porque un grafo largo se pasa a través de un
/// archivo temporal que hay que borrar cuando la exportación termina o se cancela.
/// </remarks>
public sealed class ExportCommand : IDisposable
{
    private readonly string? _scriptPath;
    private bool _disposed;

    internal ExportCommand(IReadOnlyList<string> arguments, string filterGraph, string? scriptPath)
    {
        Arguments = arguments;
        FilterGraph = filterGraph;
        _scriptPath = scriptPath;
    }

    /// <summary>Argumentos completos para FFmpeg, en orden.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Grafo de filtros generado, para mostrarlo al usuario o depurar.</summary>
    public string FilterGraph { get; }

    /// <summary>Indica si el grafo se pasa por archivo en lugar de en la línea de comandos.</summary>
    public bool UsesScriptFile => _scriptPath is not null;

    /// <summary>
    /// Reconstruye la línea de comandos tal como se escribiría en una terminal.
    /// </summary>
    /// <remarks>
    /// Solo para mostrar y depurar. La ejecución real usa la lista de argumentos, que no
    /// pasa por ningún entrecomillado y por tanto no puede malinterpretarse.
    /// </remarks>
    public string ToDisplayString(string ffmpegPath = "ffmpeg")
    {
        var builder = new StringBuilder(Quote(ffmpegPath));

        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_scriptPath is not null && File.Exists(_scriptPath))
        {
            try
            {
                File.Delete(_scriptPath);
            }
            catch (IOException)
            {
                // El archivo temporal quedará para la limpieza del sistema. No merece
                // tumbar una exportación que ya terminó correctamente.
            }
            catch (UnauthorizedAccessException)
            {
                // Idem.
            }
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}

/// <summary>Ensambla el comando completo de exportación.</summary>
public static class ExportCommandBuilder
{
    /// <summary>
    /// Longitud a partir de la cual el grafo se pasa por archivo en lugar de inline.
    /// </summary>
    /// <remarks>
    /// Windows limita la línea de comandos a unos 32 000 caracteres. Una timeline con
    /// muchos clips supera ese límite con facilidad, y el fallo resultante no menciona
    /// la longitud: FFmpeg simplemente recibe argumentos truncados. Se deja un margen
    /// amplio porque las rutas de los archivos también cuentan.
    /// </remarks>
    public const int InlineGraphLimit = 8_000;

    /// <summary>Construye el comando para exportar una timeline con unos ajustes dados.</summary>
    /// <exception cref="ArgumentException">Si la timeline está vacía o los ajustes son inválidos.</exception>
    public static ExportCommand Build(VideoTimeline timeline, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(settings);

        var plan = FilterGraphBuilder.Build(timeline, settings);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-y",
            "-nostats",
            // Emite el avance como pares clave=valor en stdout, que es lo que permite
            // mostrar porcentaje y tiempo restante sin parsear el log humano de stderr.
            "-progress", "pipe:1",
        };

        arguments.AddRange(plan.InputArguments);

        string? scriptPath = null;
        if (plan.FilterGraph.Length > InlineGraphLimit)
        {
            scriptPath = Path.Combine(Path.GetTempPath(), $"editflow-{Guid.NewGuid():N}.filter");
            File.WriteAllText(scriptPath, plan.FilterGraph, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            arguments.AddRange(["-filter_complex_script", scriptPath]);
        }
        else
        {
            arguments.AddRange(["-filter_complex", plan.FilterGraph]);
        }

        arguments.AddRange(["-map", plan.VideoLabel, "-map", plan.AudioLabel]);
        arguments.AddRange(FFmpegArgumentBuilder.BuildOutputArguments(settings));

        return new ExportCommand(arguments, plan.FilterGraph, scriptPath);
    }
}
