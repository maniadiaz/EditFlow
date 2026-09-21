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
/// Decodifica video real y comprueba que los píxeles llegan bien y a buen ritmo.
/// </summary>
[Trait("Category", "Integration")]
public class FrameReaderIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public FrameReaderIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Pixels_arrive_in_bgra_order_not_rgba()
    {
        // Confundir el orden de los canales es el clásico "el video se ve azul": el rojo
        // y el azul quedan intercambiados y todo lo demás parece funcionar. Un video de
        // un solo color lo delata sin ambigüedad.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-bgra-");

        try
        {
            // Rojo puro: en BGRA sus bytes son 0, 0, 255, 255.
            var path = Path.Combine(workspace.FullName, "rojo.mp4");
            var made = await ProcessRunner.RunAsync(tools.FFmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "color=c=red:s=64x64:r=10:d=1",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path,
                ],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StandardError);

            using var pool = new FramePool(capacity: 1, width: 64, height: 64);
            using var reader = new FrameReader(tools, path, TimeSpan.Zero, 64, 64, 10);

            var frame = await pool.RentAsync(CancellationToken.None);
            Assert.True(await reader.ReadIntoAsync(frame, CancellationToken.None), "no llegó ningún fotograma");

            // Muestra del centro, lejos de cualquier borde.
            var middle = ((32 * 64) + 32) * 4;
            int b = frame.Pixels[middle];
            int g = frame.Pixels[middle + 1];
            int r = frame.Pixels[middle + 2];
            int a = frame.Pixels[middle + 3];

            _output.WriteLine($"Píxel central: B={b} G={g} R={r} A={a}");

            // La compresión desplaza un poco los valores; el margen es generoso a
            // propósito, porque lo que se comprueba es el orden, no la exactitud.
            Assert.True(r > 180, $"el canal rojo debería dominar, pero R={r}");
            Assert.True(b < 80, $"el canal azul debería ser bajo, pero B={b} (¿orden RGBA?)");
            Assert.True(g < 80, $"el canal verde debería ser bajo, pero G={g}");
            Assert.Equal(255, a);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Decoding_keeps_well_ahead_of_real_time()
    {
        // El reproductor necesita 30 fotogramas por segundo. Si el decodificador no va
        // bastante por delante, cualquier hipo del sistema se ve como un tirón.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-rate-");

        try
        {
            var path = Path.Combine(workspace.FullName, "fuente.mp4");
            var made = await ProcessRunner.RunAsync(tools.FFmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc2=size=854x480:rate=30:duration=10",
                    "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", path,
                ],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StandardError);

            using var pool = new FramePool(capacity: 4, width: 854, height: 480);
            using var reader = new FrameReader(tools, path, TimeSpan.Zero, 854, 480, 30);

            var stopwatch = Stopwatch.StartNew();
            var frames = 0;

            while (frames < 150)
            {
                var frame = await pool.RentAsync(CancellationToken.None);
                var got = await reader.ReadIntoAsync(frame, CancellationToken.None);
                pool.Return(frame);

                if (!got)
                {
                    break;
                }

                frames++;
            }

            stopwatch.Stop();
            var rate = frames / stopwatch.Elapsed.TotalSeconds;

            _output.WriteLine(
                $"{frames} fotogramas en {stopwatch.ElapsedMilliseconds} ms " +
                $"= {rate.ToString("0", CultureInfo.InvariantCulture)} fps " +
                $"({(rate / 30).ToString("0.#", CultureInfo.InvariantCulture)}× el tiempo real)");
            _output.WriteLine($"Memoria de la reserva: {pool.MemoryBytes / (1024 * 1024)} MB");

            Assert.Equal(150, frames);
            Assert.True(rate > 60, $"solo se alcanzaron {rate:0} fps; se necesita el doble de 30 como mínimo");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Timestamps_follow_the_requested_start_position()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-seek-");

        try
        {
            var path = Path.Combine(workspace.FullName, "fuente.mp4");
            await ProcessRunner.RunAsync(tools.FFmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=10",
                    "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", path,
                ],
                CancellationToken.None);

            using var pool = new FramePool(capacity: 1, width: 320, height: 240);
            using var reader = new FrameReader(tools, path, TimeSpan.FromSeconds(4), 320, 240, 30);

            var frame = await pool.RentAsync(CancellationToken.None);
            await reader.ReadIntoAsync(frame, CancellationToken.None);
            var first = frame.Timestamp;

            await reader.ReadIntoAsync(frame, CancellationToken.None);
            var second = frame.Timestamp;

            _output.WriteLine($"primer fotograma en {first}, segundo en {second}");

            // El primero corresponde al punto pedido, y el siguiente avanza un fotograma.
            Assert.Equal(TimeSpan.FromSeconds(4), first);
            Assert.Equal(TimeSpan.FromSeconds(4) + TimeSpan.FromSeconds(1.0 / 30), second);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
