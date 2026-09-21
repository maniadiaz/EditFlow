// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Comprueba que el video sigue a un reloj externo en lugar de marcar su propio ritmo.
/// </summary>
/// <remarks>
/// El reloj lo controla el test, así que puede ir a mitad de velocidad, al doble o
/// quedarse quieto. Con el reloj real del audio esas situaciones son difíciles de
/// provocar a propósito, y son justo las que rompen la sincronía.
/// </remarks>
[Trait("Category", "Integration")]
public class ClockSyncIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ClockSyncIntegrationTests(ITestOutputHelper output) => _output = output;

    /// <summary>Reloj de mentira que avanza a la velocidad que se le indique.</summary>
    private sealed class FakeClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public double Speed { get; set; } = 1.0;

        public TimeSpan Now => TimeSpan.FromSeconds(_stopwatch.Elapsed.TotalSeconds * Speed);
    }

    private static async Task<string> MakeVideoAsync(FFmpegTools tools, DirectoryInfo directory, int seconds)
    {
        var path = Path.Combine(directory.FullName, "fuente.mp4");
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate=30:duration={seconds}",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", path,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        return path;
    }

    [Fact]
    public async Task Video_follows_a_clock_running_at_half_speed()
    {
        // Si el video marcara su propio ritmo, entregaría 30 fotogramas por segundo pase
        // lo que pase y se adelantaría al audio hasta perder la sincronía por completo.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-sync-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 6);
            var clock = new FakeClock { Speed = 0.5 };

            using var player = new VideoPlayer(tools, 640, 360, frameRate: 30)
            {
                MasterClock = () => clock.Now,
            };

            var frames = 0;
            player.FrameReady = _ => Interlocked.Increment(ref frames);

            await player.OpenAsync(path, TimeSpan.Zero, CancellationToken.None);
            player.Play();
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            player.Pause();

            var delivered = Volatile.Read(ref frames);
            var rate = delivered / 2.0;

            _output.WriteLine(
                $"reloj a media velocidad: {delivered} fotogramas en 2 s = " +
                $"{rate.ToString("0.#", CultureInfo.InvariantCulture)} fps");

            // A mitad de velocidad corresponden unos 15 fps, no 30.
            Assert.InRange(rate, 8, 22);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_stopped_clock_stops_the_video()
    {
        // Es lo que pasa cuando el audio se queda esperando el búfer: el video debe
        // esperarlo en vez de seguir corriendo y desincronizarse.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-frozen-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 6);
            var clock = new FakeClock { Speed = 0 };

            using var player = new VideoPlayer(tools, 640, 360, frameRate: 30)
            {
                MasterClock = () => clock.Now,
            };

            var frames = 0;
            player.FrameReady = _ => Interlocked.Increment(ref frames);

            await player.OpenAsync(path, TimeSpan.Zero, CancellationToken.None);
            player.Play();
            await Task.Delay(TimeSpan.FromMilliseconds(800), CancellationToken.None);
            player.Pause();

            var delivered = Volatile.Read(ref frames);
            _output.WriteLine($"reloj parado: {delivered} fotogramas entregados");

            // El primero se muestra siempre para no dejar la pantalla en negro; a partir
            // de ahí, con el reloj quieto, no debería avanzar apenas.
            Assert.InRange(delivered, 1, 4);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Frames_are_dropped_when_the_clock_runs_ahead()
    {
        // Un reloj al triple deja al video atrás. Mostrar los fotogramas atrasados no
        // recupera la sincronía: la empeora. Hay que descartarlos para alcanzarlo.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-drop-");

        try
        {
            var path = await MakeVideoAsync(tools, workspace, seconds: 10);
            var clock = new FakeClock { Speed = 3.0 };

            using var player = new VideoPlayer(tools, 640, 360, frameRate: 30)
            {
                MasterClock = () => clock.Now,
            };

            var positions = new List<TimeSpan>();
            player.FrameReady = frame => { lock (positions) { positions.Add(frame.Timestamp); } };

            await player.OpenAsync(path, TimeSpan.Zero, CancellationToken.None);
            player.Play();
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            player.Pause();

            TimeSpan last;
            int shown;
            lock (positions) { last = positions[^1]; shown = positions.Count; }

            _output.WriteLine(
                $"reloj al triple: {shown} mostrados, {player.DroppedFrames} descartados, " +
                $"última posición {last}");

            Assert.True(player.DroppedFrames > 0, "debería haber descartado fotogramas para alcanzar al reloj");

            // Lo que importa: la posición del video sigue al reloj en vez de arrastrarse.
            Assert.True(last > TimeSpan.FromSeconds(3),
                $"el video se quedó en {last} mientras el reloj iba por ~6 s");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
