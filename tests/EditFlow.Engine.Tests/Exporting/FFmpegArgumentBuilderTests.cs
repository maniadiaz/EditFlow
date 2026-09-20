// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class FFmpegArgumentBuilderTests
{
    private static ExportSettings Settings(
        string encoder,
        RateControlMode mode = RateControlMode.ConstantQuality,
        int bitrate = 0,
        int quality = 65,
        EncodingSpeed speed = EncodingSpeed.Balanced) => new()
        {
            OutputPath = "out.mp4",
            Resolution = VideoResolution.P1080,
            EncoderName = encoder,
            RateControl = mode,
            VideoBitrateKbps = bitrate,
            Quality = quality,
            Speed = speed,
        };

    private static string Flatten(ExportSettings settings) =>
        string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(settings));

    // -----------------------------------------------------------------------
    //  NVENC
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("hevc_nvenc")]
    [InlineData("av1_nvenc")]
    public void Nvenc_constant_quality_always_pins_the_bitrate_to_zero(string encoder)
    {
        // Sin '-b:v 0', NVENC aplica su bitrate por defecto y el '-cq' queda ignorado
        // en silencio: la exportación sale con una calidad que nadie pidió. Es el error
        // más repetido al integrar NVENC, y por eso tiene un test propio por códec.
        var arguments = Flatten(Settings(encoder));

        Assert.Contains("-cq", arguments, StringComparison.Ordinal);
        Assert.Contains("-b:v 0", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Nvenc_variable_bitrate_sets_target_peak_and_buffer()
    {
        var arguments = Flatten(Settings("h264_nvenc", RateControlMode.VariableBitrate, bitrate: 10_000));

        Assert.Contains("-rc vbr", arguments, StringComparison.Ordinal);
        Assert.Contains("-b:v 10000k", arguments, StringComparison.Ordinal);
        Assert.Contains("-maxrate 15000k", arguments, StringComparison.Ordinal);
        Assert.Contains("-bufsize 20000k", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Nvenc_constant_bitrate_uses_the_cbr_mode()
    {
        var arguments = Flatten(Settings("h264_nvenc", RateControlMode.ConstantBitrate, bitrate: 8_000));

        Assert.Contains("-rc cbr", arguments, StringComparison.Ordinal);
        Assert.Contains("-b:v 8000k", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-cq", arguments, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    //  x264 / x265
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    public void Software_constant_quality_uses_crf(string encoder)
    {
        var arguments = Flatten(Settings(encoder));

        Assert.Contains("-crf", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-cq", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Software_constant_bitrate_pins_min_max_and_buffer()
    {
        var arguments = Flatten(Settings("libx264", RateControlMode.ConstantBitrate, bitrate: 6_000));

        Assert.Contains("-minrate 6000k", arguments, StringComparison.Ordinal);
        Assert.Contains("-maxrate 6000k", arguments, StringComparison.Ordinal);
        Assert.Contains("-b:v 6000k", arguments, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    //  Quick Sync / AMF / VideoToolbox
    // -----------------------------------------------------------------------

    [Fact]
    public void QuickSync_constant_quality_uses_global_quality()
    {
        var arguments = Flatten(Settings("h264_qsv"));

        Assert.Contains("-global_quality", arguments, StringComparison.Ordinal);
        Assert.Contains("-look_ahead 1", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Amf_constant_quality_sets_both_frame_quantizers()
    {
        var arguments = Flatten(Settings("h264_amf"));

        Assert.Contains("-rc cqp", arguments, StringComparison.Ordinal);
        Assert.Contains("-qp_i", arguments, StringComparison.Ordinal);
        Assert.Contains("-qp_p", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Amf_variable_bitrate_uses_the_peak_constrained_mode()
    {
        var arguments = Flatten(Settings("h264_amf", RateControlMode.VariableBitrate, bitrate: 9_000));

        Assert.Contains("-rc vbr_peak", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoToolbox_constant_quality_uses_its_own_scale()
    {
        var arguments = Flatten(Settings("h264_videotoolbox"));

        Assert.Contains("-q:v", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-crf", arguments, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    //  Presets de velocidad
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(EncodingSpeed.Fastest, "p1")]
    [InlineData(EncodingSpeed.Balanced, "p5")]
    [InlineData(EncodingSpeed.Slowest, "p7")]
    public void Nvenc_speed_maps_to_its_p_presets(EncodingSpeed speed, string expected)
    {
        var arguments = Flatten(Settings("h264_nvenc", speed: speed));

        Assert.Contains($"-preset {expected}", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Svt_av1_speed_scale_runs_the_opposite_way()
    {
        // En SVT-AV1, 0 es lo más lento y de mejor calidad, y 13 lo más rápido —
        // al revés que en x264. Invertir la escala haría que pedir velocidad
        // produjera la exportación más lenta posible.
        var fastest = Flatten(Settings("libsvtav1", speed: EncodingSpeed.Fastest));
        var slowest = Flatten(Settings("libsvtav1", speed: EncodingSpeed.Slowest));

        var fastestPreset = ExtractPreset(fastest);
        var slowestPreset = ExtractPreset(slowest);

        Assert.True(
            int.Parse(fastestPreset, CultureInfo.InvariantCulture) >
            int.Parse(slowestPreset, CultureInfo.InvariantCulture),
            $"El preset de 'más rápido' ({fastestPreset}) debería ser numéricamente mayor " +
            $"que el de 'más lento' ({slowestPreset}) en SVT-AV1.");
    }

    [Theory]
    [InlineData(EncodingSpeed.Fastest, "ultrafast")]
    [InlineData(EncodingSpeed.Balanced, "medium")]
    [InlineData(EncodingSpeed.Slowest, "veryslow")]
    public void X264_speed_maps_to_its_named_presets(EncodingSpeed speed, string expected)
    {
        var arguments = Flatten(Settings("libx264", speed: speed));

        Assert.Contains($"-preset {expected}", arguments, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    //  Estructura y validación
    // -----------------------------------------------------------------------

    [Fact]
    public void Audio_is_always_encoded_to_aac()
    {
        var arguments = Flatten(Settings("libx264"));

        Assert.Contains("-c:a aac", arguments, StringComparison.Ordinal);
        Assert.Contains("-b:a 192k", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Streaming_optimisation_moves_the_index_to_the_front()
    {
        var arguments = Flatten(Settings("libx264"));

        Assert.Contains("-movflags +faststart", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_path_comes_last()
    {
        var arguments = FFmpegArgumentBuilder.BuildOutputArguments(Settings("libx264"));

        Assert.Equal("out.mp4", arguments[^1]);
    }

    [Theory]
    [InlineData(RateControlMode.VariableBitrate)]
    [InlineData(RateControlMode.ConstantBitrate)]
    public void A_bitrate_mode_without_a_bitrate_is_refused(RateControlMode mode)
    {
        // Dejarlo pasar produciría un '-b:v 0k' que FFmpeg interpreta de forma
        // impredecible según el codificador.
        var settings = Settings("libx264", mode, bitrate: 0);

        Assert.Throws<ArgumentException>(() => FFmpegArgumentBuilder.BuildOutputArguments(settings));
    }

    [Fact]
    public void An_empty_output_path_is_refused()
    {
        var settings = Settings("libx264") with { OutputPath = "  " };

        Assert.Throws<ArgumentException>(() => FFmpegArgumentBuilder.BuildOutputArguments(settings));
    }

    [Fact]
    public void Bitrates_are_formatted_independently_of_the_system_locale()
    {
        // En una máquina con locale español, un formateo descuidado escribiría "10.000k"
        // y FFmpeg lo interpretaría como 10 kbps.
        var arguments = Flatten(Settings("libx264", RateControlMode.VariableBitrate, bitrate: 10_000));

        Assert.Contains("-b:v 10000k", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("10.000", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("10,000", arguments, StringComparison.Ordinal);
    }

    private static string ExtractPreset(string arguments)
    {
        var parts = arguments.Split(' ');
        var index = Array.IndexOf(parts, "-preset");
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : string.Empty;
    }
}
