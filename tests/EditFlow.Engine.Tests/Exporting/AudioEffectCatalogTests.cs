// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Exporting;

public class AudioEffectCatalogTests
{
    [Fact]
    public void No_effect_produces_no_fragment()
    {
        Assert.Null(AudioEffectCatalog.Build(AudioEffectKind.None));
    }

    [Theory]
    [InlineData(AudioEffectKind.Voice, "highpass=f=90,equalizer=f=3000:width_type=o:width=2:g=4")]
    [InlineData(AudioEffectKind.Denoise, "afftdn=nr=12:nf=-25")]
    [InlineData(AudioEffectKind.Compressor, "acompressor=threshold=0.1:ratio=4:attack=5:release=50")]
    [InlineData(AudioEffectKind.Limiter, "alimiter=limit=0.9")]
    [InlineData(AudioEffectKind.Reverb, "aecho=0.8:0.9:40|60:0.3|0.25")]
    [InlineData(AudioEffectKind.Chorus, "chorus=0.7:0.9:55:0.4:0.25:2")]
    [InlineData(AudioEffectKind.Normalize, "loudnorm=I=-16:TP=-1.5:LRA=11")]
    public void Each_effect_produces_its_exact_fragment(AudioEffectKind kind, string expected)
    {
        Assert.Equal(expected, AudioEffectCatalog.Build(kind));
    }

    [Fact]
    public void A_centred_pan_produces_no_fragment()
    {
        Assert.Null(AudioEffectCatalog.BuildPan(0));
        Assert.Null(AudioEffectCatalog.BuildPan(0.0005));
    }

    [Theory]
    [InlineData(-1, "stereotools=balance_in=-1")]
    [InlineData(1, "stereotools=balance_in=1")]
    [InlineData(0.5, "stereotools=balance_in=0.5")]
    public void A_pan_away_from_centre_produces_the_balance_filter(double pan, string expected)
    {
        Assert.Equal(expected, AudioEffectCatalog.BuildPan(pan));
    }
}

/// <summary>
/// Corre cada fragmento contra el FFmpeg real de verdad, igual que
/// <see cref="VisualEffectExportTests"/> para los efectos de imagen.
/// </summary>
[Trait("Category", "Integration")]
public class AudioEffectExportTests
{
    private readonly ITestOutputHelper _output;

    public AudioEffectExportTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(AudioEffectKind.Voice)]
    [InlineData(AudioEffectKind.Denoise)]
    [InlineData(AudioEffectKind.Compressor)]
    [InlineData(AudioEffectKind.Limiter)]
    [InlineData(AudioEffectKind.Reverb)]
    [InlineData(AudioEffectKind.Chorus)]
    [InlineData(AudioEffectKind.Normalize)]
    public async Task Every_audio_effect_exports_without_ffmpeg_rejecting_the_filter(AudioEffectKind kind)
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("editflow-audioeffectexport-").FullName;
        try
        {
            var source = Path.Combine(directory, "a.mp4");
            var made = await ProcessRunner.RunAsync(tools.FFmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y",
                 "-f", "lavfi", "-i", "color=c=gray:size=320x180:rate=30:duration=1",
                 "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                 "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                 "-c:a", "aac", source],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StandardError);

            var media = await new FFprobeService(tools).ProbeAsync(source, CancellationToken.None);
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(media) { AudioEffect = kind, Pan = 0.4 });

            var output = Path.Combine(directory, "salida.mp4");
            var result = await new ExportJob(tools).RunAsync(
                sequence,
                new ExportSettings
                {
                    OutputPath = output,
                    Resolution = new VideoResolution(320, 180, "prueba"),
                    EncoderName = "libx264",
                    Speed = EncodingSpeed.Fastest,
                    IncludeAudio = true,
                },
                null,
                CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
