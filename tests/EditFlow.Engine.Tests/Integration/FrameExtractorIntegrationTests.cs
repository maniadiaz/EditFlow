// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine;
using EditFlow.Engine.Thumbnails;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

[Trait("Category", "Integration")]
public class FrameExtractorIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public FrameExtractorIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Extracts_a_jpeg_of_the_requested_width()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-frame-");

        try
        {
            var video = await AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "v.mp4", 3, video: true, toneHz: null);
            var output = Path.Combine(workspace.FullName, "covers", "c.jpg");

            var ok = await new FrameExtractor(tools).ExtractAsync(
                video.Path, TimeSpan.FromSeconds(1), output, width: 160);

            Assert.True(ok);
            var bytes = await File.ReadAllBytesAsync(output);
            Assert.True(bytes.Length > 500);

            // Firma de un JPEG: FF D8 FF.
            Assert.Equal([0xFF, 0xD8, 0xFF], bytes[..3]);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_video_returns_false_instead_of_throwing()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-frame-bad-");

        try
        {
            var bad = Path.Combine(workspace.FullName, "no.mp4");
            await File.WriteAllBytesAsync(bad, new byte[1024]);

            Assert.False(await new FrameExtractor(tools).ExtractAsync(
                bad, TimeSpan.Zero, Path.Combine(workspace.FullName, "c.jpg")));
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public void The_seek_goes_before_the_input_so_it_is_instant()
    {
        var args = FrameExtractor.BuildArguments("in.mp4", TimeSpan.FromSeconds(2.5), 320, "o.jpg");

        Assert.True(args.ToList().IndexOf("-ss") < args.ToList().IndexOf("-i"));
        Assert.Contains("2.5", args);
        Assert.Contains("scale=320:-2", args);
    }
}
