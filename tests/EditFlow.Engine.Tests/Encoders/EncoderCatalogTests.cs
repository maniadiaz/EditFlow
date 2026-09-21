// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Encoders;

namespace EditFlow.Engine.Tests.Encoders;

public class EncoderCatalogTests
{
    /// <summary>Salida real de <c>ffmpeg -encoders</c> (FFmpeg n9.0, build GPL de BtbN).</summary>
    private static string RealOutput => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffmpeg-encoders.txt"));

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("hevc_nvenc")]
    [InlineData("av1_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    [InlineData("libx264")]
    [InlineData("libx265")]
    [InlineData("libsvtav1")]
    public void Parses_known_encoders_from_real_output(string encoder)
    {
        var parsed = EncoderCatalog.ParseListedEncoders(RealOutput);

        Assert.Contains(encoder, parsed);
    }

    [Fact]
    public void Does_not_mistake_the_legend_for_encoder_entries()
    {
        // La salida de ffmpeg empieza con una leyenda cuyas líneas también comienzan
        // por " V....." — por ejemplo " V..... = Video". Un parser que no respete el
        // separador ------ recogería "=" como si fuera un codificador.
        var parsed = EncoderCatalog.ParseListedEncoders(RealOutput);

        Assert.DoesNotContain("=", parsed);
        Assert.DoesNotContain("Video", parsed);
        Assert.DoesNotContain("------", parsed);
    }

    [Fact]
    public void Ignores_audio_and_subtitle_encoders()
    {
        const string output =
            """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
             S..... srt                  SubRip subtitle
            """;

        var parsed = EncoderCatalog.ParseListedEncoders(output);

        Assert.Equal(["h264_nvenc"], parsed);
    }

    [Fact]
    public void Handles_windows_line_endings()
    {
        var output = "Encoders:\r\n ------\r\n V....D libx264              libx264 H.264\r\n";

        var parsed = EncoderCatalog.ParseListedEncoders(output);

        Assert.Equal(["libx264"], parsed);
    }

    [Fact]
    public void Every_catalog_entry_has_a_distinct_name()
    {
        var duplicates = EncoderCatalog.Known
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_codec_offers_a_software_fallback()
    {
        // Sin al menos un codificador por CPU por códec, una máquina sin GPU compatible
        // se quedaría sin poder exportar en ese formato.
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            var hasSoftware = EncoderCatalog.Known
                .Any(e => e.Codec == codec && e.Backend == EncoderBackend.Software);

            Assert.True(hasSoftware, $"{codec} no tiene codificador por CPU de respaldo.");
        }
    }
}
