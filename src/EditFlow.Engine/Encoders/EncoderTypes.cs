namespace EditFlow.Engine.Encoders;

/// <summary>Códecs de video que EditFlow ofrece al exportar.</summary>
public enum VideoCodec
{
    /// <summary>H.264 / AVC. Máxima compatibilidad; la opción segura.</summary>
    H264,

    /// <summary>H.265 / HEVC. Aproximadamente un 40 % menos de tamaño que H.264 a igual calidad.</summary>
    Hevc,

    /// <summary>AV1. El más eficiente, pero requiere hardware reciente para codificar rápido.</summary>
    Av1,
}

/// <summary>Motor que realiza la codificación.</summary>
public enum EncoderBackend
{
    /// <summary>Codificación por CPU (libx264, libx265, SVT-AV1). Lenta pero de máxima calidad.</summary>
    Software,

    /// <summary>NVIDIA NVENC.</summary>
    Nvenc,

    /// <summary>Intel Quick Sync Video.</summary>
    QuickSync,

    /// <summary>AMD Advanced Media Framework.</summary>
    Amf,

    /// <summary>Apple VideoToolbox.</summary>
    VideoToolbox,

    /// <summary>VA-API (Linux).</summary>
    Vaapi,
}

/// <summary>
/// Un codificador concreto de FFmpeg y si está realmente disponible en esta máquina.
/// </summary>
/// <param name="Name">Nombre del codificador en FFmpeg, por ejemplo <c>h264_nvenc</c>.</param>
/// <param name="Codec">Códec que produce.</param>
/// <param name="Backend">Motor que lo ejecuta.</param>
/// <param name="DisplayName">Nombre legible para mostrar en la interfaz.</param>
/// <param name="IsAvailable">
/// <see langword="true"/> solo si el codificador superó una prueba de codificación real.
/// Que FFmpeg lo liste no basta: QSV y AMF aparecen listados en cualquier build aunque
/// la máquina no tenga el hardware correspondiente.
/// </param>
/// <param name="UnavailableReason">Explicación legible de por qué no se puede usar.</param>
/// <param name="Diagnostics">Salida de error original de FFmpeg, para depuración.</param>
public sealed record EncoderInfo(
    string Name,
    VideoCodec Codec,
    EncoderBackend Backend,
    string DisplayName,
    bool IsAvailable,
    string? UnavailableReason = null,
    string? Diagnostics = null)
{
    /// <summary>Indica si la codificación ocurre en la GPU.</summary>
    public bool IsHardware => Backend is not EncoderBackend.Software;
}
