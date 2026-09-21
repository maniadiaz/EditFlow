// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

[Trait("Category", "Integration")]
public class VideoPlayerIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public VideoPlayerIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<string> MakeVideoAsync(FFmpegTools tools, DirectoryInfo directory, int seconds)
    {
        var path = Path.Combine(directory.FullName, "fuente.mp4");
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=854x480:rate=30:duration={seconds}",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", path,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        return path;
    }

    [Fact]
    public async Task Playback_is_paced_to_real_time_not_to_decoder_speed()
    {
        // El decodificador alcanza 742 fps. Sin frenarlo, dos segundos de video pasarían
        // en menos de una décima y el video se vería acelerado.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-pace-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 5);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var frames = 0;
            player.FrameReady = _ => Interlocked.Increment(ref frames);

            await player.OpenAsync(path, TimeSpan.Zero, CancellationToken.None);
            var clock = Stopwatch.StartNew();
            player.Play();

            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            player.Pause();
            clock.Stop();

            var rate = frames / clock.Elapsed.TotalSeconds;
            _output.WriteLine(
                $"{frames} fotogramas en {clock.ElapsedMilliseconds} ms = " +
                $"{rate.ToString("0.#", CultureInfo.InvariantCulture)} fps");
            _output.WriteLine($"Búfer: {player.BufferBytes / (1024 * 1024)} MB");

            // Margen amplio: en una máquina cargada el ritmo baja un poco, pero lo que se
            // comprueba es que no va a la velocidad del decodificador.
            Assert.InRange(rate, 20, 40);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Opening_shows_a_frame_even_while_paused()
    {
        // Sin esto el preview queda en negro al abrir o al saltar, que es exactamente el
        // defecto que tuvo la versión con LibVLC.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-first-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 3);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var received = new TaskCompletionSource<TimeSpan>();
            player.FrameReady = frame => received.TrySetResult(frame.Timestamp);

            await player.OpenAsync(path, TimeSpan.FromSeconds(1), CancellationToken.None);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(received.Task, completed);

            _output.WriteLine($"Primer fotograma en pausa: {received.Task.Result}");
            Assert.False(player.IsPlaying);
            Assert.Equal(TimeSpan.FromSeconds(1), received.Task.Result);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Pausing_stops_delivering_frames()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-pause-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 5);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var frames = 0;
            player.FrameReady = _ => Interlocked.Increment(ref frames);

            await player.OpenAsync(path, TimeSpan.Zero, CancellationToken.None);
            player.Play();
            await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);

            player.Pause();
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            var afterPause = Volatile.Read(ref frames);

            await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
            var later = Volatile.Read(ref frames);

            _output.WriteLine($"tras pausar: {afterPause}, medio segundo después: {later}");

            // Puede colarse el fotograma que ya estaba esperando su turno, no más.
            Assert.InRange(later - afterPause, 0, 1);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Seeking_lands_on_the_requested_position()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-seek2-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 10);

            using var player = new VideoPlayer(tools, 854, 480, frameRate: 30);
            var positions = new List<TimeSpan>();
            player.FrameReady = frame => { lock (positions) { positions.Add(frame.Timestamp); } };

            await player.OpenAsync(path, TimeSpan.Zero, CancellationToken.None);
            await Task.Delay(400, CancellationToken.None);

            var clock = Stopwatch.StartNew();
            await player.SeekAsync(TimeSpan.FromSeconds(7), CancellationToken.None);
            await Task.Delay(400, CancellationToken.None);
            clock.Stop();

            TimeSpan last;
            lock (positions) { last = positions[^1]; }

            _output.WriteLine($"salto a 7 s resuelto en {clock.ElapsedMilliseconds} ms, posición {last}");

            Assert.InRange(last.TotalSeconds, 6.9, 7.2);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
