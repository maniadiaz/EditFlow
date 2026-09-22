// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>Arrastrar el cabezal: muchas peticiones seguidas, de las que solo cuenta la última.</summary>
[Trait("Category", "Integration")]
public class ScrubIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ScrubIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<string> MakeVideoAsync(FFmpegTools tools, DirectoryInfo directory, string name, int seconds)
    {
        var path = Path.Combine(directory.FullName, name);
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=854x480:rate=30:duration={seconds}",
                "-c:v", "libx264", "-preset", "veryfast", "-g", "12", "-pix_fmt", "yuv420p", path,
            ],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && started.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_burst_of_requests_ends_on_the_last_one_without_serving_every_intermediate_one()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-scrub-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, "fuente.mp4", seconds: 20);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var seen = new List<TimeSpan>();
            player.FrameReady = frame => { lock (seen) { seen.Add(frame.Timestamp); } };

            await player.OpenAsync(path, TimeSpan.Zero);
            await Task.Delay(300);
            lock (seen) { seen.Clear(); }

            // Como arrastrar el cabezal: 60 posiciones nuevas en un segundo.
            for (var i = 1; i <= 60; i++)
            {
                player.Scrub(path, TimeSpan.FromSeconds(i * 0.25));
                await Task.Delay(16);
            }

            var target = TimeSpan.FromSeconds(15);
            await WaitUntilAsync(() => { lock (seen) { return seen.Count > 0 && Math.Abs((seen[^1] - target).TotalSeconds) < 0.5; } }, 4000);

            TimeSpan last;
            int count;
            lock (seen) { last = seen[^1]; count = seen.Count; }
            _output.WriteLine($"60 peticiones -> {count} fotogramas mostrados, el último en {last.TotalSeconds:0.00} s");

            Assert.InRange(last.TotalSeconds, 14.5, 15.5);

            // Cada fotograma mostrado corresponde a una petición atendida, y son menos que las
            // 60 hechas: las intermedias se descartaron en vez de encolarse.
            Assert.True(count < 60, $"se atendieron todas las peticiones ({count}); debían fusionarse");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Scrubbing_to_another_file_switches_to_it_and_forgets_the_pending_position_of_the_old_one()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-scrub2-");

        try
        {
            var first = await MakeVideoAsync(tools, workspace, "a.mp4", seconds: 20);
            var second = await MakeVideoAsync(tools, workspace, "b.mp4", seconds: 6);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var seen = new List<TimeSpan>();
            player.FrameReady = frame => { lock (seen) { seen.Add(frame.Timestamp); } };

            await player.OpenAsync(first, TimeSpan.Zero);

            player.Scrub(first, TimeSpan.FromSeconds(18));
            await player.OpenAsync(second, TimeSpan.FromSeconds(2));   // otro clip: la posición pendiente ya no vale
            lock (seen) { seen.Clear(); }

            await Task.Delay(1200);

            lock (seen)
            {
                Assert.NotEmpty(seen);

                // El segundo archivo solo dura 6 s: un fotograma cerca de los 18 s sería del primero.
                Assert.All(seen, t => Assert.True(t.TotalSeconds < 6.1, $"llegó un fotograma de {t.TotalSeconds:0.0} s"));
            }
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Scrubbing_while_playing_keeps_playing_from_the_new_position()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-scrub3-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, "fuente.mp4", seconds: 20);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var seen = new List<TimeSpan>();
            player.FrameReady = frame => { lock (seen) { seen.Add(frame.Timestamp); } };

            await player.OpenAsync(path, TimeSpan.Zero);
            player.Play();
            await Task.Delay(300);

            player.Scrub(path, TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => { lock (seen) { return seen.Count > 0 && seen[^1].TotalSeconds >= 10; } }, 3000);
            var atArrival = player.Position;

            await Task.Delay(800);
            var later = player.Position;

            _output.WriteLine($"al llegar {atArrival.TotalSeconds:0.00} s; 0,8 s después {later.TotalSeconds:0.00} s");

            // Sigue avanzando: la reproducción no se quedó parada tras el salto.
            Assert.True(later - atArrival > TimeSpan.FromSeconds(0.4), "debía seguir reproduciendo tras el salto");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}
