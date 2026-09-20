using EditFlow.Engine.Encoders;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Traduce la escala de calidad de EditFlow (1 a 100) a la escala nativa de cada
/// familia de codificadores, y sugiere bitrates por resolución.
/// </summary>
/// <remarks>
/// Los codificadores no comparten escala: x264 y x265 usan CRF de 0 a 51, SVT-AV1 de
/// 0 a 63, NVENC un CQ de 0 a 51, y en todos ellos <b>un número más bajo significa
/// más calidad</b>. Además, valores iguales no producen calidades iguales entre
/// códecs. Exponer el número nativo haría que cambiar de H.264 a AV1 alterase la
/// calidad sin que el usuario tocara nada.
/// </remarks>
public static class QualityScale
{
    /// <summary>Valor mínimo de la escala de EditFlow.</summary>
    public const int Minimum = 1;

    /// <summary>Valor máximo de la escala de EditFlow.</summary>
    public const int Maximum = 100;

    /// <summary>
    /// Convierte la calidad normalizada al valor nativo del codificador indicado.
    /// </summary>
    /// <param name="quality">Calidad de 1 a 100; valores fuera de rango se acotan.</param>
    /// <param name="encoderName">Nombre del codificador de FFmpeg.</param>
    public static int ToNative(int quality, string encoderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderName);

        var normalized = Math.Clamp(quality, Minimum, Maximum);

        // Rangos útiles por familia. Se evitan deliberadamente los extremos: un CRF 0
        // es sin pérdidas y produce archivos enormes sin ganancia visible, y un CRF 51
        // es inservible. Estos límites cubren el rango que alguien usaría de verdad.
        var (best, worst) = NativeRange(encoderName);

        // La escala nativa va al revés que la nuestra: más calidad, número más bajo.
        var position = (normalized - Minimum) / (double)(Maximum - Minimum);
        return (int)Math.Round(worst - (position * (worst - best)));
    }

    /// <summary>
    /// Rango nativo utilizable de un codificador, como (mejor calidad, peor calidad).
    /// </summary>
    internal static (int Best, int Worst) NativeRange(string encoderName) => encoderName switch
    {
        // SVT-AV1: CRF de 0 a 63. Por debajo de 15 el archivo crece sin mejora apreciable.
        "libsvtav1" => (15, 55),

        // NVENC: CQ de 0 a 51. Necesita valores algo más bajos que x264 para una
        // calidad comparable, porque su análisis es menos exhaustivo.
        var n when n.EndsWith("_nvenc", StringComparison.Ordinal) => (16, 40),

        // Quick Sync: global_quality equivalente a un CRF.
        var n when n.EndsWith("_qsv", StringComparison.Ordinal) => (16, 40),

        // AMF: QP de 0 a 51.
        var n when n.EndsWith("_amf", StringComparison.Ordinal) => (16, 40),

        // VideoToolbox usa una escala propia de 0 a 100 donde MÁS es mejor; se invierte
        // aquí para que ToNative la trate igual que al resto.
        var n when n.EndsWith("_videotoolbox", StringComparison.Ordinal) => (80, 30),

        // x264 y x265: CRF de 0 a 51. 18 es prácticamente indistinguible del original;
        // por encima de 32 los artefactos ya se notan.
        _ => (18, 32),
    };

    /// <summary>
    /// Bitrate sugerido en kbps para una resolución y un códec.
    /// </summary>
    /// <remarks>
    /// Son los valores de partida del diálogo de exportación, no un límite. HEVC y AV1
    /// alcanzan una calidad equivalente con bastante menos bitrate que H.264, así que
    /// ofrecerles el mismo número desperdiciaría su principal ventaja.
    /// </remarks>
    public static int SuggestedBitrateKbps(VideoResolution resolution, VideoCodec codec)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var baseline = resolution.Height switch
        {
            <= 480 => 2_500,
            <= 720 => 5_000,
            <= 1080 => 10_000,
            <= 1440 => 20_000,
            _ => 40_000,
        };

        var factor = codec switch
        {
            VideoCodec.H264 => 1.0,
            VideoCodec.Hevc => 0.6,
            VideoCodec.Av1 => 0.55,
            _ => 1.0,
        };

        return (int)Math.Round(baseline * factor / 100.0) * 100;
    }
}
