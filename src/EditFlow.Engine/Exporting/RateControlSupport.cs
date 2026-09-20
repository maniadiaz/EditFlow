namespace EditFlow.Engine.Exporting;

/// <summary>Hasta qué punto un codificador admite un modo de control de tasa.</summary>
public enum RateControlAvailability
{
    /// <summary>Admitido sin reservas.</summary>
    Supported,

    /// <summary>Admitido, pero a costa de algo que conviene advertir.</summary>
    SupportedWithTradeoff,

    /// <summary>No admitido por este codificador.</summary>
    Unsupported,
}

/// <summary>Resultado de consultar el soporte de un modo de control de tasa.</summary>
/// <param name="Availability">Nivel de soporte.</param>
/// <param name="Note">Explicación del compromiso o del motivo, si lo hay.</param>
public sealed record RateControlSupport(RateControlAvailability Availability, string? Note = null)
{
    /// <summary>Indica si el modo puede usarse, con o sin advertencia.</summary>
    public bool CanBeUsed => Availability is not RateControlAvailability.Unsupported;
}

/// <summary>
/// Describe qué modos de control de tasa admite cada codificador, y con qué coste.
/// </summary>
/// <remarks>
/// No todos los codificadores implementan los tres modos de la misma forma. Ofrecerlos
/// todos por igual llevaría al usuario a elegir una combinación que falla al empezar a
/// exportar, o —peor— que funciona pero con una pérdida de calidad que nadie le avisó.
/// </remarks>
public static class RateControlCapabilities
{
    /// <summary>Consulta el soporte de un modo para un codificador concreto.</summary>
    public static RateControlSupport Describe(string encoderName, RateControlMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderName);

        if (encoderName == "libsvtav1" && mode == RateControlMode.ConstantBitrate)
        {
            // Comprobado contra FFmpeg n9.0: SVT-AV1 solo acepta 'rc=2' (bitrate
            // constante) si se combina con 'pred-struct=1', su modo de baja latencia.
            // Ese modo desactiva las referencias hacia adelante, de las que depende
            // buena parte de la eficiencia de AV1.
            return new RateControlSupport(
                RateControlAvailability.SupportedWithTradeoff,
                "SVT-AV1 solo admite bitrate constante en modo de baja latencia, que reduce " +
                "notablemente la compresión. Para un tamaño de archivo predecible con mejor " +
                "calidad, usa bitrate variable.");
        }

        if (encoderName.EndsWith("_videotoolbox", StringComparison.Ordinal)
            && mode == RateControlMode.ConstantBitrate)
        {
            return new RateControlSupport(
                RateControlAvailability.SupportedWithTradeoff,
                "VideoToolbox no garantiza un bitrate estrictamente constante; lo trata " +
                "como un objetivo aproximado.");
        }

        return new RateControlSupport(RateControlAvailability.Supported);
    }
}
