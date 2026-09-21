// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Playback;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>Renderiza la mezcla del preview con FFmpeg de verdad y mide el archivo resultante.</summary>
[Trait("Category", "Integration")]
public class PreviewMixIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public PreviewMixIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_mix_lasts_as_long_as_the_sequence_and_contains_the_music()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-premix-");

        try
        {
            var video = await AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "video.mp4", 3, video: true, toneHz: null);
            var music = await AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "musica.wav", 5, video: false, toneHz: 800);

            // Video mudo de 3 s con música de 5 s: en el preview solo se oye la música,
            // que era justo lo que faltaba al reproducir clip por clip.
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(video));
            sequence.AddAudioTrack("Música").TryAdd(
                new AudioClip(music, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.Zero));

            var output = Path.Combine(workspace.FullName, "cache", "mix.flac");
            var mix = await new PreviewMixRenderer(tools).RenderAsync(sequence, output);

            Assert.Equal(output, mix.Path);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"), "no debe quedar el temporal");

            var probed = await new FFprobeService(tools).ProbeMediaAsync(output, CancellationToken.None);
            _output.WriteLine($"duración de la mezcla: {probed.Duration.TotalSeconds:0.##} s (plan {mix.Duration.TotalSeconds:0.##} s)");

            Assert.InRange(probed.Duration.TotalSeconds, 4.8, 5.3);
            Assert.True(probed.HasAudio);

            var mean = await AudioMixIntegrationTests.MeanVolumeAsync(tools, output);
            _output.WriteLine($"volumen medio: {mean:0.#} dB");
            Assert.True(mean > -50, $"la mezcla suena casi muda ({mean:0.#} dB)");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_cancelled_render_leaves_no_file_behind()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-premix-cancel-");

        try
        {
            var video = await AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "video.mp4", 2, video: true, toneHz: 300);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(video));

            var output = Path.Combine(workspace.FullName, "mix.flac");
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new PreviewMixRenderer(tools).RenderAsync(sequence, output, cts.Token));

            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
