using System.Globalization;

namespace EditFlow.Engine.Exporting;

/// <summary>Estado de avance de una exportación en curso.</summary>
/// <param name="Processed">Duración de video ya codificada.</param>
/// <param name="Total">Duración total de la timeline.</param>
/// <param name="Frames">Fotogramas codificados hasta ahora.</param>
/// <param name="FramesPerSecond">Ritmo instantáneo de codificación.</param>
/// <param name="Speed">Múltiplo del tiempo real; 2.4 significa 2,4 veces más rápido.</param>
public sealed record ExportProgress(
    TimeSpan Processed,
    TimeSpan Total,
    long Frames,
    double FramesPerSecond,
    double Speed)
{
    /// <summary>Porcentaje completado, de 0 a 100.</summary>
    public double Percentage => Total > TimeSpan.Zero
        ? Math.Clamp(Processed.TotalSeconds / Total.TotalSeconds * 100, 0, 100)
        : 0;

    /// <summary>
    /// Tiempo restante estimado, o <see langword="null"/> si aún no puede calcularse.
    /// </summary>
    /// <remarks>
    /// Se calcula a partir de la velocidad reportada por FFmpeg y no de una media desde
    /// el inicio, porque el ritmo cambia mucho entre escenas simples y complejas.
    /// </remarks>
    public TimeSpan? Remaining
    {
        get
        {
            if (Total <= TimeSpan.Zero)
            {
                return null;
            }

            // Si no queda nada pendiente, el tiempo restante es cero aunque se
            // desconozca el ritmo. Devolver null aquí dejaría la estimación en blanco
            // justo en el instante en que la exportación acaba de terminar.
            var pending = Total - Processed;
            if (pending <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return Speed > 0
                ? TimeSpan.FromSeconds(pending.TotalSeconds / Speed)
                : null;
        }
    }
}

/// <summary>
/// Interpreta las líneas <c>clave=valor</c> que FFmpeg emite con <c>-progress pipe:1</c>.
/// </summary>
/// <remarks>
/// Es una salida pensada para máquinas, a diferencia del log de stderr, que está pensado
/// para personas y cambia de formato entre versiones. Leer el avance de stderr es la
/// forma habitual de que un integrador acabe con un parser que se rompe al actualizar.
/// </remarks>
public sealed class ProgressParser
{
    private readonly TimeSpan _total;
    private TimeSpan _processed;
    private long _frames;
    private double _fps;
    private double _speed;

    /// <summary>Crea un parser para una timeline de la duración indicada.</summary>
    public ProgressParser(TimeSpan total) => _total = total;

    /// <summary>Indica si FFmpeg ya notificó el final del trabajo.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Procesa una línea de salida.
    /// </summary>
    /// <returns>
    /// Un avance actualizado cuando la línea cierra un bloque de estado, o
    /// <see langword="null"/> si la línea solo aporta un dato parcial.
    /// </returns>
    /// <remarks>
    /// FFmpeg emite varias claves seguidas y cierra cada bloque con <c>progress=</c>.
    /// Notificar en cada clave produciría avances incoherentes, con el porcentaje de un
    /// instante y la velocidad de otro.
    /// </remarks>
    public ExportProgress? Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
            case "out_time_ms" when _processed == TimeSpan.Zero:
                // FFmpeg escribe microsegundos en ambas claves: 'out_time_ms' está mal
                // nombrada desde hace años y se mantiene por compatibilidad. Se prefiere
                // 'out_time_us', y la otra solo sirve de reserva.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
                    && microseconds >= 0)
                {
                    _processed = TimeSpan.FromMilliseconds(microseconds / 1000.0);
                }

                break;

            case "out_time":
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                {
                    _processed = parsed;
                }

                break;

            case "frame":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
                {
                    _frames = frames;
                }

                break;

            case "fps":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                {
                    _fps = fps;
                }

                break;

            case "speed":
                // Llega como "2.41x", y como "N/A" en los primeros instantes.
                var trimmed = value.TrimEnd('x', 'X').Trim();
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                {
                    _speed = speed;
                }

                break;

            case "progress":
                if (value == "end")
                {
                    IsComplete = true;
                    _processed = _total;
                }

                return new ExportProgress(_processed, _total, _frames, _fps, _speed);
        }

        return null;
    }
}
