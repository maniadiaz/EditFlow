namespace EditFlow.Engine.Execution;

/// <summary>Resultado de ejecutar un proceso externo hasta su finalización.</summary>
/// <param name="ExitCode">Código de salida del proceso.</param>
/// <param name="StandardOutput">Todo lo escrito en stdout.</param>
/// <param name="StandardError">Todo lo escrito en stderr.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// Indica si el proceso terminó correctamente.
    /// </summary>
    /// <remarks>
    /// Se mira <b>exclusivamente</b> el código de salida, nunca si stderr quedó vacío.
    /// FFmpeg escribe en stderr de forma rutinaria aunque todo vaya bien: SVT-AV1, por
    /// ejemplo, vuelca un banner informativo completo en cada codificación exitosa.
    /// Tratar "stderr no vacío" como fallo marcaría libsvtav1 como no disponible.
    /// </remarks>
    public bool Succeeded => ExitCode == 0;
}
