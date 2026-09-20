using System.Runtime.InteropServices;

namespace EditFlow.Engine.Encoders;

/// <summary>
/// Decide qué motores de codificación tienen sentido en el sistema operativo actual.
/// </summary>
/// <remarks>
/// Las builds de FFmpeg se compilan con soporte para motores de varias plataformas a la
/// vez, así que en Windows aparecen listados <c>h264_videotoolbox</c> (exclusivo de macOS)
/// y <c>h264_vaapi</c> (exclusivo de Linux). Probarlos siempre falla, y mostrarlos
/// desactivados en la interfaz solo añade ruido sobre hardware que el usuario no podría
/// tener aunque quisiera.
/// </remarks>
public static class EncoderPlatforms
{
    /// <summary>Indica si un motor puede existir en el sistema operativo actual.</summary>
    public static bool IsApplicable(EncoderBackend backend) => backend switch
    {
        EncoderBackend.Software => true,

        // NVENC, Quick Sync y AMF tienen driver para Windows y Linux.
        EncoderBackend.Nvenc or EncoderBackend.QuickSync or EncoderBackend.Amf =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux),

        EncoderBackend.VideoToolbox => RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        EncoderBackend.Vaapi => RuntimeInformation.IsOSPlatform(OSPlatform.Linux),

        _ => false,
    };
}
