using System.Globalization;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Recorre el camino completo: importar varios videos, unirlos, cortarlos y exportar.
/// </summary>
/// <remarks>
/// Los tres archivos de prueba se eligen deliberadamente incompatibles entre sí —
/// resoluciones, fotogramas por segundo y presencia de audio distintos— porque es
/// exactamente lo que rompe un grafo de filtros mal construido. Un test con tres clips
/// idénticos pasaría aunque faltara toda la normalización.
/// </remarks>
[Trait("Category", "Integration")]
public class ExportEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public ExportEndToEndTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Joins_cuts_and_exports_a_mixed_timeline()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine("FFmpeg no disponible; ejecuta: pwsh tools/fetch-ffmpeg.ps1");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-e2e-");

        try
        {
            // --- Tres fuentes deliberadamente distintas ---------------------------
            var landscape = await CreateVideoAsync(tools, workspace, "landscape.mp4",
                width: 1920, height: 1080, fps: 30, seconds: 4, withAudio: true);

            var uhd = await CreateVideoAsync(tools, workspace, "uhd.mp4",
                width: 3840, height: 2160, fps: 60, seconds: 3, withAudio: true);

            var portraitSilent = await CreateVideoAsync(tools, workspace, "portrait-silent.mp4",
                width: 1080, height: 1920, fps: 30, seconds: 3, withAudio: false);

            // --- Montaje: unir, cortar, eliminar y reordenar -----------------------
            var timeline = new VideoTimeline();
            timeline.Append(new Clip(landscape));
            timeline.Append(new Clip(uhd));
            timeline.Append(new Clip(portraitSilent));

            Assert.Equal(TimeSpan.FromSeconds(10), timeline.Duration);

            // Cortar el segundo clip por la mitad y borrar la segunda parte.
            var secondHalf = timeline.SplitAt(TimeSpan.FromSeconds(5.5));
            Assert.NotNull(secondHalf);
            Assert.Equal(4, timeline.Clips.Count);
            timeline.Remove(secondHalf);

            // Mover el vertical al principio.
            var portraitClip = timeline.Clips.Single(c => c.Source.Path == portraitSilent.Path);
            timeline.Move(portraitClip, 0);

            var expectedDuration = TimeSpan.FromSeconds(3 + 4 + 1.5);
            Assert.Equal(expectedDuration, timeline.Duration);

            _output.WriteLine($"Timeline: {timeline.Clips.Count} clips, {timeline.Duration.TotalSeconds:0.##}s");
            foreach (var clip in timeline.Clips)
            {
                _output.WriteLine($"  {clip}");
            }

            // --- Exportar con el mejor codificador disponible -----------------------
            var detector = new EncoderDetector(tools);
            var encoders = await detector.DetectAsync(CancellationToken.None);
            var encoder = encoders.FirstOrDefault(e => e.IsAvailable && e.IsHardware && e.Codec == VideoCodec.H264)
                          ?? encoders.First(e => e.IsAvailable && e.Codec == VideoCodec.H264);

            var outputPath = Path.Combine(workspace.FullName, "export.mp4");
            var settings = new ExportSettings
            {
                OutputPath = outputPath,
                Resolution = VideoResolution.P1080,
                EncoderName = encoder.Name,
                FrameRate = 30,
                RateControl = RateControlMode.VariableBitrate,
                VideoBitrateKbps = 8_000,
                Speed = EncodingSpeed.Fast,
            };

            var updates = new List<ExportProgress>();
            var job = new ExportJob(tools);

            var result = await job.RunAsync(
                timeline,
                settings,
                new Progress<ExportProgress>(updates.Add),
                CancellationToken.None);

            _output.WriteLine(string.Empty);
            _output.WriteLine($"Codificador : {encoder.DisplayName}");
            _output.WriteLine($"Resultado   : {(result.Succeeded ? "correcto" : result.ErrorMessage)}");
            _output.WriteLine($"Tiempo      : {result.Elapsed.TotalSeconds:0.##}s");

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.True(File.Exists(outputPath));

            // --- Verificar el archivo resultante con ffprobe -----------------------
            var probed = await new FFprobeService(tools).ProbeAsync(outputPath, CancellationToken.None);
            _output.WriteLine($"Salida      : {probed}");

            Assert.Equal(1920, probed.Width);
            Assert.Equal(1080, probed.Height);
            Assert.Equal("h264", probed.VideoCodec);
            Assert.True(probed.HasAudio, "La exportación debe tener audio aunque un clip no lo tuviera.");

            // La duración puede variar unas décimas por el redondeo a fotogramas enteros.
            Assert.InRange(
                probed.Duration.TotalSeconds,
                expectedDuration.TotalSeconds - 0.5,
                expectedDuration.TotalSeconds + 0.5);

            // --- Verificar que el avance se informó de verdad ----------------------
            Assert.NotEmpty(updates);
            Assert.True(updates[^1].Percentage > 99,
                $"El último avance reportado fue {updates[^1].Percentage:0.#} %.");
            Assert.True(updates.Select(u => u.Processed).SequenceEqual(updates.Select(u => u.Processed).Order()),
                "El avance debe ser monótono creciente.");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Cancelling_kills_the_process_and_leaves_no_partial_file()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine("FFmpeg no disponible; ejecuta: pwsh tools/fetch-ffmpeg.ps1");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-cancel-");

        try
        {
            // Una fuente larga y una codificación lenta, para que dé tiempo a cancelar.
            var source = await CreateVideoAsync(tools, workspace, "long.mp4",
                width: 1920, height: 1080, fps: 30, seconds: 30, withAudio: true);

            var timeline = new VideoTimeline();
            timeline.Append(new Clip(source));

            var outputPath = Path.Combine(workspace.FullName, "cancelled.mp4");
            var settings = new ExportSettings
            {
                OutputPath = outputPath,
                Resolution = VideoResolution.P2160,
                EncoderName = "libx264",
                Speed = EncodingSpeed.Slowest,
            };

            using var cancellation = new CancellationTokenSource();
            var job = new ExportJob(tools);

            var exporting = job.RunAsync(
                timeline,
                settings,
                new Progress<ExportProgress>(_ => cancellation.Cancel()),
                cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporting);

            // Un archivo a medias es peor que ninguno: quien lo encuentre después no
            // tendrá forma de saber que está incompleto.
            Assert.False(File.Exists(outputPath),
                "Una exportación cancelada no debe dejar el archivo parcial en disco.");

            _output.WriteLine("Cancelación correcta: proceso detenido y sin archivo parcial.");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    // ---------------------------------------------------------------------------

    private static async Task<MediaInfo> CreateVideoAsync(
        FFmpegTools tools,
        DirectoryInfo directory,
        string name,
        int width,
        int height,
        int fps,
        double seconds,
        bool withAudio)
    {
        var path = Path.Combine(directory.FullName, name);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi",
            "-i", $"testsrc2=size={width}x{height}:rate={fps}:duration={duration}",
        };

        if (withAudio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}"]);
        }

        arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);

        if (withAudio)
        {
            arguments.AddRange(["-c:a", "aac", "-shortest"]);
        }

        arguments.Add(path);

        var result = await ProcessRunner.RunAsync(tools.FFmpegPath, arguments, CancellationToken.None);
        Assert.True(result.Succeeded, $"No se pudo crear {name}: {result.StandardError}");

        return new MediaInfo(path, TimeSpan.FromSeconds(seconds), width, height, fps, "h264", withAudio);
    }

}
