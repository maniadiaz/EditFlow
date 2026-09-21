// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using EditFlow.Engine.Encoders;

namespace EditFlow.Engine.Tests.Encoders;

public class EncoderPlatformsTests
{
    [Fact]
    public void Software_encoding_is_always_applicable()
    {
        // Es el respaldo universal: si esto fuera falso en alguna plataforma,
        // esa plataforma se quedaría sin poder exportar.
        Assert.True(EncoderPlatforms.IsApplicable(EncoderBackend.Software));
    }

    [Fact]
    public void VideoToolbox_is_offered_only_on_macOS()
    {
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        Assert.Equal(expected, EncoderPlatforms.IsApplicable(EncoderBackend.VideoToolbox));
    }

    [Fact]
    public void Vaapi_is_offered_only_on_Linux()
    {
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        Assert.Equal(expected, EncoderPlatforms.IsApplicable(EncoderBackend.Vaapi));
    }

    [Theory]
    [InlineData(EncoderBackend.Nvenc)]
    [InlineData(EncoderBackend.QuickSync)]
    [InlineData(EncoderBackend.Amf)]
    public void Pc_gpu_backends_are_offered_on_Windows_and_Linux(EncoderBackend backend)
    {
        var expected =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        Assert.Equal(expected, EncoderPlatforms.IsApplicable(backend));
    }

    [Fact]
    public void Every_codec_keeps_a_usable_encoder_on_this_platform()
    {
        // El filtro de plataforma no debe dejar ningún códec sin ninguna opción:
        // eso haría desaparecer un formato entero del diálogo de exportación.
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            var applicable = EncoderCatalog.Known
                .Where(e => e.Codec == codec && EncoderPlatforms.IsApplicable(e.Backend))
                .ToArray();

            Assert.NotEmpty(applicable);
        }
    }
}
