// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Encoders;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class QualityScaleTests
{
    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    [InlineData("libsvtav1")]
    [InlineData("h264_nvenc")]
    [InlineData("av1_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    public void Higher_quality_means_a_lower_native_value(string encoder)
    {
        // En CRF, CQ, QP y global_quality el número baja cuando la calidad sube.
        // Invertirlo daría exactamente lo contrario de lo que pide el usuario.
        var low = QualityScale.ToNative(20, encoder);
        var high = QualityScale.ToNative(90, encoder);

        Assert.True(high < low,
            $"{encoder}: calidad 90 dio {high} y calidad 20 dio {low}; debería ser menor.");
    }

    [Fact]
    public void VideoToolbox_scale_runs_the_other_way()
    {
        // VideoToolbox usa 0 a 100 donde MÁS es mejor, al contrario que CRF.
        var low = QualityScale.ToNative(20, "h264_videotoolbox");
        var high = QualityScale.ToNative(90, "h264_videotoolbox");

        Assert.True(high > low,
            $"VideoToolbox: calidad 90 dio {high} y calidad 20 dio {low}; debería ser mayor.");
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libsvtav1")]
    [InlineData("h264_nvenc")]
    public void Native_values_stay_inside_the_usable_range(string encoder)
    {
        var (best, worst) = QualityScale.NativeRange(encoder);
        var lower = Math.Min(best, worst);
        var upper = Math.Max(best, worst);

        for (var quality = QualityScale.Minimum; quality <= QualityScale.Maximum; quality++)
        {
            var native = QualityScale.ToNative(quality, encoder);
            Assert.InRange(native, lower, upper);
        }
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(9999)]
    public void Out_of_range_quality_is_clamped_instead_of_throwing(int quality)
    {
        var native = QualityScale.ToNative(quality, "libx264");
        var (best, worst) = QualityScale.NativeRange("libx264");

        Assert.InRange(native, best, worst);
    }

    [Fact]
    public void Av1_reaches_equivalent_quality_with_less_bitrate_than_h264()
    {
        // Ofrecer a AV1 el mismo bitrate que a H.264 desperdiciaría su principal ventaja.
        var h264 = QualityScale.SuggestedBitrateKbps(VideoResolution.P1080, VideoCodec.H264);
        var hevc = QualityScale.SuggestedBitrateKbps(VideoResolution.P1080, VideoCodec.Hevc);
        var av1 = QualityScale.SuggestedBitrateKbps(VideoResolution.P1080, VideoCodec.Av1);

        Assert.True(hevc < h264);
        Assert.True(av1 < hevc);
    }

    [Fact]
    public void Suggested_bitrate_grows_with_resolution()
    {
        var previous = 0;

        foreach (var resolution in VideoResolution.Presets)
        {
            var suggested = QualityScale.SuggestedBitrateKbps(resolution, VideoCodec.H264);
            Assert.True(suggested > previous,
                $"{resolution.Label} sugirió {suggested}, que no supera al anterior ({previous}).");
            previous = suggested;
        }
    }

    [Fact]
    public void The_five_requested_resolutions_are_offered()
    {
        var labels = VideoResolution.Presets.Select(r => r.Label).ToArray();

        Assert.Equal(["480p", "720p", "1080p", "1440p", "4K"], labels);
    }

    [Fact]
    public void Portrait_variants_swap_the_dimensions()
    {
        var portrait = VideoResolution.P1080.AsPortrait();

        Assert.Equal(1080, portrait.Width);
        Assert.Equal(1920, portrait.Height);
    }
}
