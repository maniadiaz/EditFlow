using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

/// <summary>
/// SVT-AV1 no acepta las opciones genéricas de bitrate que sí entienden x264 y x265.
/// </summary>
/// <remarks>
/// Comprobado contra FFmpeg n9.0: añadir <c>-maxrate</c> o <c>-minrate</c> a una
/// codificación con libsvtav1 la aborta con "Error setting encoder parameters: bad
/// parameter". El fallo lo descubrió la prueba de integración contra el binario real;
/// los tests unitarios anteriores daban por buena la cadena generada porque solo
/// comprobaban que contuviera lo que yo esperaba.
/// </remarks>
public class SvtAv1ArgumentTests
{
    private static ExportSettings Settings(RateControlMode mode, string encoder = "libsvtav1") => new()
    {
        OutputPath = "out.mp4",
        Resolution = VideoResolution.P1080,
        EncoderName = encoder,
        RateControl = mode,
        VideoBitrateKbps = 5_000,
    };

    private static string Flatten(ExportSettings settings) =>
        string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(settings));

    [Fact]
    public void Variable_bitrate_passes_the_target_and_nothing_else()
    {
        var arguments = Flatten(Settings(RateControlMode.VariableBitrate));

        Assert.Contains("-b:v 5000k", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-maxrate", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-minrate", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-bufsize", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Constant_bitrate_goes_through_the_native_parameters()
    {
        // SVT-AV1 no expone el bitrate constante por las opciones genéricas de FFmpeg:
        // hay que activarlo con rc=2, y solo lo acepta junto a pred-struct=1.
        var arguments = Flatten(Settings(RateControlMode.ConstantBitrate));

        Assert.Contains("-svtav1-params rc=2:pred-struct=1", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-maxrate", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Constant_quality_still_uses_plain_crf()
    {
        var arguments = Flatten(Settings(RateControlMode.ConstantQuality));

        Assert.Contains("-crf", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-svtav1-params", arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    public void X264_and_x265_keep_the_generic_bitrate_options(string encoder)
    {
        // El caso especial de SVT-AV1 no debe contagiarse al resto de codificadores
        // por software, que sí respetan minrate y maxrate.
        var arguments = Flatten(Settings(RateControlMode.ConstantBitrate, encoder));

        Assert.Contains("-minrate 5000k", arguments, StringComparison.Ordinal);
        Assert.Contains("-maxrate 5000k", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-svtav1-params", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_low_latency_tradeoff_is_surfaced_to_the_caller()
    {
        // pred-struct=1 desactiva las referencias hacia adelante, de las que depende
        // buena parte de la eficiencia de AV1. El usuario merece saberlo antes de
        // esperar una exportación larga para obtener un archivo peor de lo esperado.
        var support = RateControlCapabilities.Describe("libsvtav1", RateControlMode.ConstantBitrate);

        Assert.Equal(RateControlAvailability.SupportedWithTradeoff, support.Availability);
        Assert.True(support.CanBeUsed);
        Assert.False(string.IsNullOrWhiteSpace(support.Note));
    }

    [Theory]
    [InlineData("libsvtav1", RateControlMode.ConstantQuality)]
    [InlineData("libsvtav1", RateControlMode.VariableBitrate)]
    [InlineData("libx264", RateControlMode.ConstantBitrate)]
    [InlineData("h264_nvenc", RateControlMode.ConstantBitrate)]
    public void Combinations_without_caveats_report_plain_support(string encoder, RateControlMode mode)
    {
        var support = RateControlCapabilities.Describe(encoder, mode);

        Assert.Equal(RateControlAvailability.Supported, support.Availability);
        Assert.Null(support.Note);
    }
}
