// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Exporting;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Mide cuántos decibelios cambia de verdad el audio exportado al ajustar un clip de video.
/// </summary>
[Trait("Category", "Integration")]
public class ClipGainIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ClipGainIntegrationTests(ITestOutputHelper output) => _output = output;

    private static ExportSettings Settings(string output) => new()
    {
        OutputPath = output,
        Resolution = VideoResolution.P480,
        EncoderName = "libx264",
        Speed = EncodingSpeed.Fastest,
        FrameRate = 30,
    };

    [Fact]
    public async Task Raising_and_lowering_a_video_clip_changes_the_exported_level_by_that_many_decibels()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-clipgain-");

        try
        {
            var media = await AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "v.mp4", 3, video: true, toneHz: 440);

            async Task<double> LevelAsync(string name, double gainDb, bool muted)
            {
                var timeline = new VideoTimeline();
                timeline.Append(new Clip(media) { AudioGainDb = gainDb, IsAudioMuted = muted });

                var output = Path.Combine(workspace.FullName, name);
                var result = await new ExportJob(tools).RunAsync(
                    timeline, Settings(output), progress: null, CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorMessage);

                return await AudioMixIntegrationTests.MeanVolumeAsync(tools, output);
            }

            var reference = await LevelAsync("base.mp4", 0, muted: false);
            var louder = await LevelAsync("mas.mp4", 6, muted: false);
            var quieter = await LevelAsync("menos.mp4", -6, muted: false);
            var muted = await LevelAsync("mudo.mp4", 6, muted: true);

            _output.WriteLine(
                $"base {reference:0.#} dB · +6 dB → {louder:0.#} · -6 dB → {quieter:0.#} · silenciado → {muted:0.#}");

            // La compresión AAC mueve el valor una fracción de dB, de ahí el margen.
            Assert.InRange(louder - reference, 5.0, 7.0);
            Assert.InRange(quieter - reference, -7.0, -5.0);

            // Silenciado gana al volumen: subirlo no puede hacer que suene.
            Assert.True(muted < -80, $"silenciado debía quedar mudo, pero da {muted:0.#} dB");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}
