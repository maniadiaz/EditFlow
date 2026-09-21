// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>Resolución, velocidad de fotogramas y decodificación por hardware del preview.</summary>
public class PreviewQualityUnitTests
{
    [Fact]
    public void Hardware_decoding_is_only_requested_when_asked_for()
    {
        var software = FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 1280, 720, 60);
        var hardware = FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 1280, 720, 60, hardwareDecoding: true);

        Assert.DoesNotContain("-hwaccel", software);

        var line = string.Join(' ', hardware);
        Assert.Contains("-hwaccel auto", line, StringComparison.Ordinal);

        // La opción de hardware va antes de la entrada, que es donde FFmpeg la lee.
        Assert.True(hardware.ToList().IndexOf("-hwaccel") < hardware.ToList().IndexOf("-i"));
    }

    [Fact]
    public void The_requested_frame_rate_and_size_reach_the_filter()
    {
        var line = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 1920, 1080, 59.94));

        Assert.Contains("fps=59.94", line, StringComparison.Ordinal);
        Assert.Contains("scale=1920:1080", line, StringComparison.Ordinal);
    }
}

[Trait("Category", "Integration")]
public class PreviewQualityIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public PreviewQualityIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<string> MakeVideoAsync(FFmpegTools tools, DirectoryInfo directory, int rate, int seconds)
    {
        var path = Path.Combine(directory.FullName, $"v{rate}.mp4");
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate={rate}:duration={seconds}",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", path,
            ],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return path;
    }

    [Fact]
    public async Task A_60_fps_video_is_shown_at_60_fps_and_not_halved_to_30()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-fps60-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, rate: 60, seconds: 6);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            player.Configure(1280, 720, 60, hardwareDecoding: false);

            var count = 0;
            var width = 0;
            player.FrameReady = frame => { Interlocked.Increment(ref count); width = frame.Width; };

            await player.OpenAsync(path, TimeSpan.Zero);
            player.Play();
            await Task.Delay(500);   // arranque
            var before = Volatile.Read(ref count);
            await Task.Delay(2000);
            var shown = Volatile.Read(ref count) - before;
            player.Pause();

            _output.WriteLine($"{shown} fotogramas en 2 s a {width} px de ancho");

            Assert.Equal(1280, width);                 // la resolución pedida llega a los fotogramas
            Assert.InRange(shown, 100, 130);           // ~120 = 60 por segundo; a 30 serían ~60
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Asking_for_hardware_decoding_always_ends_up_showing_frames()
    {
        // Con o sin tarjeta compatible, pedir hardware no debe dejar el preview en negro: si falla,
        // el reproductor reintenta por software.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-hw-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, rate: 30, seconds: 4);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            player.Configure(854, 480, 30, hardwareDecoding: true);

            var count = 0;
            player.FrameReady = _ => Interlocked.Increment(ref count);

            await player.OpenAsync(path, TimeSpan.Zero);
            player.Play();
            await Task.Delay(1500);

            Assert.True(count > 20, $"solo llegaron {count} fotogramas");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task The_decode_size_can_change_between_openings()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-resize-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, rate: 30, seconds: 6);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var sizes = new List<(int, int)>();
            player.FrameReady = f => { lock (sizes) { sizes.Add((f.Width, f.Height)); } };

            await player.OpenAsync(path, TimeSpan.Zero);
            await Task.Delay(400);

            player.Configure(1920, 1080, 30, hardwareDecoding: false);
            player.Scrub(path, TimeSpan.FromSeconds(2));
            await Task.Delay(1200);

            lock (sizes)
            {
                Assert.Contains((854, 480), sizes);
                Assert.Contains((1920, 1080), sizes);

                // Tras el cambio no vuelve a aparecer el tamaño viejo.
                var firstBig = sizes.IndexOf((1920, 1080));
                Assert.DoesNotContain((854, 480), sizes.Skip(firstBig));
            }
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}
