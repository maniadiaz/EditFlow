using System.Diagnostics;
using System.Text;

namespace EditFlow.Engine.Execution;

/// <summary>
/// Ejecuta procesos externos (FFmpeg y ffprobe) capturando su salida.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Ejecuta <paramref name="executable"/> y espera a que termine, devolviendo
    /// su código de salida junto con stdout y stderr completos.
    /// </summary>
    /// <remarks>
    /// Los argumentos se pasan por <see cref="ProcessStartInfo.ArgumentList"/> en vez de
    /// como una cadena única: así el runtime se encarga del entrecomillado. Es lo que evita
    /// que una ruta con espacios —o un grafo de filtros lleno de comillas y comas— se parta
    /// en argumentos distintos sin que nos enteremos.
    /// </remarks>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"No se pudo iniciar el proceso '{executable}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Matar el árbol completo: FFmpeg puede haber lanzado procesos hijos, y
            // dejarlos vivos mantendría el archivo de salida bloqueado.
            TryKill(process);
            throw;
        }

        // WaitForExitAsync puede volver antes de que se vacíen los buffers de salida.
        // Esta espera sin token garantiza que stdout y stderr están completos.
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
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
            // El proceso ya había terminado entre la comprobación y el Kill.
        }
        catch (NotSupportedException)
        {
            // Plataforma sin soporte para matar el árbol; el proceso principal ya murió.
        }
    }
}
