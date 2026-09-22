// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Exporting;

public class VisualEffectCatalogTests
{
    [Fact]
    public void No_effect_produces_no_fragment()
    {
        Assert.Null(VisualEffectCatalog.Build(VisualEffectKind.None));
    }

    [Theory]
    [InlineData(VisualEffectKind.Vhs, "eq=contrast=1.15:saturation=0.75:brightness=0.02,noise=alls=10:allf=t,gblur=sigma=0.4")]
    [InlineData(VisualEffectKind.ChromaticAberration, "rgbashift=rh=-3:bh=3")]
    [InlineData(VisualEffectKind.FilmGrain, "noise=alls=22:allf=t")]
    [InlineData(VisualEffectKind.Blur, "gblur=sigma=6")]
    [InlineData(VisualEffectKind.Vaporwave, "hue=h=280:s=1.3,eq=contrast=1.1:saturation=1.2")]
    public void Each_effect_produces_its_exact_fragment(VisualEffectKind kind, string expected)
    {
        Assert.Equal(expected, VisualEffectCatalog.Build(kind));
    }
}

/// <summary>
/// Corre cada fragmento contra el FFmpeg real de verdad: <see cref="VisualEffectCatalogTests"/>
/// solo comprueba el texto, no que el propio FFmpeg lo entienda. Un nombre de filtro mal escrito,
/// o no disponible en el build empaquetado, pasaría inadvertido sin esto.
/// </summary>
[Trait("Category", "Integration")]
public class VisualEffectExportTests
{
    private readonly ITestOutputHelper _output;

    public VisualEffectExportTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(VisualEffectKind.Vhs)]
    [InlineData(VisualEffectKind.ChromaticAberration)]
    [InlineData(VisualEffectKind.FilmGrain)]
    [InlineData(VisualEffectKind.Blur)]
    [InlineData(VisualEffectKind.Vaporwave)]
    public async Task Every_effect_exports_without_ffmpeg_rejecting_the_filter(VisualEffectKind kind)
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("editflow-effectexport-").FullName;
        try
        {
            var source = Path.Combine(directory, "a.mp4");
            var made = await ProcessRunner.RunAsync(tools.FFmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=0x8060A0:size=320x180:rate=30:duration=1",
                 "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StandardError);

            var media = await new FFprobeService(tools).ProbeAsync(source, CancellationToken.None);
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(media) { Effect = kind });

            var output = Path.Combine(directory, "salida.mp4");
            var result = await new ExportJob(tools).RunAsync(
                sequence,
                new ExportSettings
                {
                    OutputPath = output,
                    Resolution = new VideoResolution(320, 180, "prueba"),
                    EncoderName = "libx264",
                    Speed = EncodingSpeed.Fastest,
                    IncludeAudio = false,
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
