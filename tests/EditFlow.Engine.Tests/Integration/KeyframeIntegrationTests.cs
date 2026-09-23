// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Comprueba con FFmpeg de verdad que las animaciones y la corrección de color avanzada producen
/// lo que dicen: que un objeto se mueve, que un LUT se aplica y que el volumen sube.
/// </summary>
/// <remarks>
/// Una expresión mal construida no falla al montar el grafo: FFmpeg la acepta y devuelve un valor
/// constante, o aborta a mitad de la exportación cuando el valor sale de rango. Un test que solo
/// mirara el texto del grafo no vería ni una cosa ni la otra, así que aquí se miden los píxeles
/// del archivo exportado.
/// </remarks>
[Trait("Category", "Integration")]
public class KeyframeIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public KeyframeIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_animated_layer_really_travels_across_the_frame()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-kf-");

        try
        {
            var below = await SolidAsync(tools, workspace, "negro.mp4", "black", seconds: 4);
            var layer = await SolidAsync(tools, workspace, "blanco.mp4", "white", seconds: 4, size: "64x64");

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(below));

            var track = sequence.AddOverlayTrack("V2");
            var item = OverlayItem.CreateVideo(
                layer, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(4),
                new OverlayTransform(0.1, 0.5, 0.1), playsAudio: false);

            // De un extremo al otro del cuadro en cuatro segundos.
            item.Animation = Animation.None.With(
                AnimatedProperty.OffsetX,
                KeyframeTrack.Empty.With(TimeSpan.Zero, 0.15).With(TimeSpan.FromSeconds(4), 0.85));

            Assert.True(track.TryAdd(item));

            var output = await ExportAsync(tools, workspace, "movido.mp4", sequence);

            var early = await BrightColumnAsync(tools, output, at: 0.3);
            var late = await BrightColumnAsync(tools, output, at: 3.6);

            _output.WriteLine($"Columna del cuadro blanco: t=0,3 → {early};  t=3,6 → {late}");

            Assert.True(early > 0, "No se encontró el cuadro al principio.");
            Assert.True(late > 0, "No se encontró el cuadro al final.");

            // 854 de ancho: de un 15 % a un 85 % son unos 600 píxeles de recorrido.
            Assert.True(late - early > 400,
                $"El elemento apenas se movió: de la columna {early} a la {late}. La expresión "
                + "probablemente se evaluó una sola vez en vez de en cada fotograma.");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task An_animated_zoom_survives_a_whole_export_without_aborting()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-kfzoom-");

        try
        {
            var media = await SolidAsync(tools, workspace, "patron.mp4", "gray", seconds: 3, pattern: true);

            var clip = new Clip(media);

            // Incluye un punto por debajo del zoom mínimo a propósito: sin acotarlo dentro de la
            // expresión, FFmpeg pediría recortar más de lo que hay y abortaría a mitad.
            clip.Animation = Animation.None.With(
                AnimatedProperty.Scale,
                KeyframeTrack.Empty.With(TimeSpan.Zero, 0.5).With(TimeSpan.FromSeconds(3), 2.5));

            var sequence = new EditSequence();
            sequence.Video.Append(clip);

            var output = await ExportAsync(tools, workspace, "zoom.mp4", sequence);

            Assert.True(File.Exists(output));
            Assert.True(new FileInfo(output).Length > 1000);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Automated_volume_really_rises_across_the_clip()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-kfvol-");

        try
        {
            var video = await SolidAsync(tools, workspace, "negro.mp4", "black", seconds: 4);
            var music = await AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "tono.wav", 4, video: false, toneHz: 440);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(video));

            var audioTrack = sequence.AddAudioTrack("A1");
            var audio = new AudioClip(music, TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.Zero);
            audio.Animation = Animation.None.With(
                AnimatedProperty.Volume,
                KeyframeTrack.Empty.With(TimeSpan.Zero, -30).With(TimeSpan.FromSeconds(4), 0));

            Assert.True(audioTrack.TryAdd(audio));

            var output = await ExportAsync(tools, workspace, "volumen.mp4", sequence);

            var first = await MeanVolumeAsync(tools, output, from: 0, to: 1);
            var last = await MeanVolumeAsync(tools, output, from: 3, to: 4);

            _output.WriteLine($"Volumen medio: primer segundo {first:0.0} dB, último {last:0.0} dB");

            Assert.True(last - first > 10,
                $"El volumen no subió: {first:0.0} dB al principio y {last:0.0} dB al final.");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task A_LUT_with_an_awkward_path_is_applied_instead_of_breaking_the_graph()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-lut-");

        try
        {
            // Una carpeta y un nombre con todo lo que rompe el analizador de filtros: espacios,
            // coma, corchetes y apóstrofo. Es lo que tuvo aparcada la importación de LUT.
            var folder = Directory.CreateDirectory(Path.Combine(workspace.FullName, "mis luts, raros [v2]"));
            var lut = Path.Combine(folder.FullName, "el look d'Ana.cube");

            // Un LUT que intercambia el rojo y el azul: el efecto se ve de un vistazo.
            await File.WriteAllTextAsync(lut, string.Join(
                '\n',
                "TITLE \"intercambio\"",
                "LUT_3D_SIZE 2",
                "0 0 0", "0 0 1", "0 1 0", "0 1 1",
                "1 0 0", "1 0 1", "1 1 0", "1 1 1"));

            var media = await SolidAsync(tools, workspace, "rojo.mp4", "red", seconds: 2);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(media) { Grade = new ColorGrade(LutPath: lut) });

            var output = await ExportAsync(tools, workspace, "lut.mp4", sequence);
            var pixel = await PixelAsync(tools, output, 400, 240);

            _output.WriteLine($"Rojo pasado por el LUT: R={pixel.R} G={pixel.G} B={pixel.B}");

            Assert.True(pixel.B > 120 && pixel.R < 90,
                $"El LUT no se aplicó: el rojo debía salir azul y salió {pixel.R}/{pixel.G}/{pixel.B}.");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task The_colour_wheels_and_curves_reach_the_exported_file()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-grade-");

        try
        {
            var media = await SolidAsync(tools, workspace, "gris.mp4", "gray", seconds: 2);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(media)
            {
                Grade = new ColorGrade(
                    Midtones: new ColorWheel(Blue: 0.5),
                    Master: ToneCurve.FromPoints([new CurvePoint(0, 0), new CurvePoint(0.5, 0.75), new CurvePoint(1, 1)])),
            });

            var output = await ExportAsync(tools, workspace, "grade.mp4", sequence);
            var pixel = await PixelAsync(tools, output, 400, 240);

            _output.WriteLine($"Gris corregido: R={pixel.R} G={pixel.G} B={pixel.B}");

            // Es la comprobación que descartó 'colorbalance': con él, la rueda de medios no movía
            // ni un punto un gris medio, que es el tono más común de cualquier plano.
            Assert.True(pixel.B > pixel.R + 15,
                $"La rueda de medios no tiñó de azul un gris medio: {pixel.R}/{pixel.G}/{pixel.B}.");
            Assert.True(pixel.G > 140, $"La curva no aclaró el gris medio: G={pixel.G}.");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    // ------------------------------------------------------------------ ayudas

    private static async Task<MediaInfo> SolidAsync(
        FFmpegTools tools,
        DirectoryInfo workspace,
        string name,
        string color,
        double seconds,
        string size = "640x360",
        bool pattern = false)
    {
        var path = Path.Combine(workspace.FullName, name);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);

        var source = pattern
            ? $"testsrc2=size={size}:rate=30:duration={duration}"
            : $"color=c={color}:s={size}:r=30:d={duration}";

        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", source,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                path,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);

        var parts = size.Split('x');
        return new MediaInfo(
            path,
            TimeSpan.FromSeconds(seconds),
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            30,
            "h264",
            HasAudio: false);
    }

    private static async Task<string> ExportAsync(
        FFmpegTools tools, DirectoryInfo workspace, string name, EditSequence sequence)
    {
        var output = Path.Combine(workspace.FullName, name);

        var result = await new ExportJob(tools).RunAsync(
            sequence,
            new ExportSettings
            {
                OutputPath = output,
                Resolution = VideoResolution.P480,
                EncoderName = "libx264",
                FrameRate = 30,
                Speed = EncodingSpeed.Fast,
            },
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        return output;
    }

    /// <summary>Columna donde está lo más claro de una fila del fotograma, o -1 si todo es oscuro.</summary>
    private static async Task<int> BrightColumnAsync(FFmpegTools tools, string path, double at)
    {
        var raw = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".gray");

        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", at.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-frames:v", "1",
                "-vf", "format=gray,crop=854:1:0:240",
                "-f", "rawvideo", "-pix_fmt", "gray",
                raw,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);

        var row = await File.ReadAllBytesAsync(raw);
        var best = -1;
        var brightest = (byte)128;

        for (var x = 0; x < row.Length; x++)
        {
            if (row[x] > brightest)
            {
                brightest = row[x];
                best = x;
            }
        }

        return best;
    }

    private static async Task<(byte R, byte G, byte B)> PixelAsync(FFmpegTools tools, string path, int x, int y)
    {
        var raw = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".rgb");

        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", path,
                "-vf", string.Create(CultureInfo.InvariantCulture, $"format=rgb24,crop=1:1:{x}:{y}"),
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24",
                raw,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);

        var bytes = await File.ReadAllBytesAsync(raw);
        Assert.True(bytes.Length >= 3);
        return (bytes[0], bytes[1], bytes[2]);
    }

    /// <summary>Volumen medio de un tramo del archivo, en dB.</summary>
    private static async Task<double> MeanVolumeAsync(FFmpegTools tools, string path, double from, double to)
    {
        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner",
                "-ss", from.ToString(CultureInfo.InvariantCulture),
                "-to", to.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-vn", "-af", "volumedetect", "-f", "null", "-",
            ],
            CancellationToken.None);

        foreach (var line in result.StandardError.Split('\n'))
        {
            var marker = line.IndexOf("mean_volume:", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var text = line[(marker + "mean_volume:".Length)..].Replace("dB", string.Empty, StringComparison.Ordinal);
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        Assert.Fail("FFmpeg no informó del volumen medio.");
        return 0;
    }
}
