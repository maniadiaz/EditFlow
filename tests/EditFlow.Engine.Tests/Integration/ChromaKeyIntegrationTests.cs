// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Playback;
using EditFlow.Engine.Thumbnails;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Comprueba con FFmpeg de verdad que el recorte por color hace lo que dice: que el fondo verde
/// desaparece, que lo que había debajo se ve a través, y que el sujeto sobrevive.
/// </summary>
/// <remarks>
/// Un test que solo mirara el texto del grafo pasaría aunque el filtro estuviera mal puesto —
/// después del <c>overlay</c>, sobre un formato sin canal alfa, con el color en un formato que
/// FFmpeg no entiende—. Aquí se leen los píxeles del archivo exportado.
/// </remarks>
[Trait("Category", "Integration")]
public class ChromaKeyIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ChromaKeyIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_green_background_of_a_layer_lets_the_main_video_show_through()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-chroma-");

        try
        {
            // Debajo, un video rojo entero. Encima, una capa verde con un cuadro azul en el centro.
            var below = await SolidAsync(tools, workspace, "rojo.mp4", "0xFF0000", box: null);
            var layer = await SolidAsync(tools, workspace, "croma.mp4", "0x00B140", box: "0x0000FF");

            var keyed = await ExportAsync(tools, workspace, "recortado.mp4", below, layer, ChromaKey.Green);
            var plain = await ExportAsync(tools, workspace, "entero.mp4", below, layer, ChromaKey.None);

            // Una esquina, lejos del cuadro del centro: ahí la capa era puro fondo.
            var keyedCorner = await PixelAsync(tools, keyed, x: 40, y: 40);
            var plainCorner = await PixelAsync(tools, plain, x: 40, y: 40);

            // El centro exacto de la salida de 854×480, que es donde cae el cuadro azul: el recorte
            // no debe tocarlo.
            var keyedCentre = await PixelAsync(tools, keyed, x: 427, y: 240);

            _output.WriteLine($"Esquina con recorte : {Describe(keyedCorner)}");
            _output.WriteLine($"Esquina sin recorte : {Describe(plainCorner)}");
            _output.WriteLine($"Centro con recorte  : {Describe(keyedCentre)}");

            Assert.True(IsMostly(plainCorner, red: false, green: true, blue: false),
                $"Sin recortar, la esquina debía seguir siendo el fondo verde de la capa; fue {Describe(plainCorner)}.");

            Assert.True(IsMostly(keyedCorner, red: true, green: false, blue: false),
                $"Con el fondo recortado, la esquina debía dejar ver el video rojo de debajo; fue {Describe(keyedCorner)}.");

            // Aquí el umbral se aprieta: es el que caza un sujeto recortado a medias. Con el rojo
            // de debajo asomando a través de un azul semitransparente, el canal rojo sube, y con un
            // umbral flojo eso pasaba por bueno.
            Assert.True(IsMostly(keyedCentre, red: false, green: false, blue: true, weak: 55),
                $"El cuadro azul no es fondo y debía salir entero y opaco; fue {Describe(keyedCentre)}.");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task A_still_frame_for_the_preview_comes_back_as_a_png_with_the_background_gone()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-chroma-still-");

        try
        {
            var layer = await SolidAsync(tools, workspace, "croma.mp4", "0x00B140", box: "0x0000FF");
            var file = Path.Combine(workspace.FullName, "fotograma.png");

            var ok = await new FrameExtractor(tools).ExtractAsync(
                layer.Path, TimeSpan.FromSeconds(1), file, width: 320,
                keyFilter: ChromaKeyFilter.Build(ChromaKey.Green));

            Assert.True(ok);

            // Firma de un PNG. Es la parte que importa: un JPEG no tiene canal alfa, así que el
            // fondo recortado volvería a verse opaco sobre el preview.
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);

            var pixelFormat = await ProbeAsync(tools, file, "stream=pix_fmt");
            _output.WriteLine($"Formato del fotograma: {pixelFormat}");
            Assert.Contains("a", pixelFormat, StringComparison.Ordinal);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public void The_live_decoder_premultiplies_so_the_cut_out_pixels_do_not_tint_what_is_under_them()
    {
        var arguments = FrameReader.BuildArguments(
            "capa.mp4", TimeSpan.Zero, 320, 180, 30,
            keyFilter: ChromaKeyFilter.Build(ChromaKey.Green));

        var chain = arguments[arguments.ToList().IndexOf("-vf") + 1];

        Assert.Contains("format=yuva444p,chromakey=", chain, StringComparison.Ordinal);
        Assert.EndsWith("premultiply=inplace=1", chain, StringComparison.Ordinal);

        // Los fotogramas salen como BGRA premultiplicado: sin este paso, un píxel recortado
        // conservaría su verde con alfa 0 y dejaría un velo sobre el video de abajo.
        Assert.Contains("bgra", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void Without_a_cut_the_live_decoder_chain_is_exactly_the_one_that_was_there_before()
    {
        var before = FrameReader.BuildArguments("capa.mp4", TimeSpan.Zero, 320, 180, 30);
        var after = FrameReader.BuildArguments("capa.mp4", TimeSpan.Zero, 320, 180, 30, keyFilter: null);

        Assert.Equal(before, after);
        Assert.DoesNotContain("premultiply", string.Join(' ', after), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ ayudas

    /// <summary>Crea un video de un color plano, opcionalmente con un cuadro de otro en el centro.</summary>
    private static async Task<MediaInfo> SolidAsync(
        FFmpegTools tools, DirectoryInfo workspace, string name, string color, string? box)
    {
        var path = Path.Combine(workspace.FullName, name);
        var source = $"color=c={color}:s=640x360:r=30:d=2";

        var filters = box is null
            ? "null"
            : $"drawbox=x=240:y=100:w=160:h=160:color={box}:t=fill";

        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", source,
                "-vf", filters,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                path,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        return new MediaInfo(path, TimeSpan.FromSeconds(2), 640, 360, 30, "h264", HasAudio: false);
    }

    /// <summary>Exporta el video de abajo con la capa encima, recortada o no.</summary>
    private static async Task<string> ExportAsync(
        FFmpegTools tools, DirectoryInfo workspace, string name, MediaInfo below, MediaInfo layer, ChromaKey key)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(below));

        var track = sequence.AddOverlayTrack("V2");
        var item = OverlayItem.CreateVideo(
            layer, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(2), playsAudio: false);
        item.ChromaKey = key;
        Assert.True(track.TryAdd(item));

        var output = Path.Combine(workspace.FullName, name);
        var settings = new ExportSettings
        {
            OutputPath = output,
            Resolution = VideoResolution.P480,
            EncoderName = "libx264",
            FrameRate = 30,
            Speed = EncodingSpeed.Fast,
        };

        var result = await new ExportJob(tools).RunAsync(sequence, settings, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        return output;
    }

    /// <summary>Lee el color de un píxel del primer fotograma, en RGB.</summary>
    private static async Task<(byte R, byte G, byte B)> PixelAsync(FFmpegTools tools, string path, int x, int y)
    {
        // Se recorta un único píxel y se vuelca en crudo: así no hay compresión de por medio que
        // pudiera explicar una diferencia de color. El paso a RGB va antes del recorte porque en
        // YUV 4:2:0 el croma va a mitad de resolución y un recorte de 1×1 no es representable.
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
        Assert.True(bytes.Length >= 3, "No se leyó el píxel.");
        return (bytes[0], bytes[1], bytes[2]);
    }

    private static async Task<string> ProbeAsync(FFmpegTools tools, string path, string entries)
    {
        var result = await ProcessRunner.RunAsync(
            tools.FFprobePath,
            ["-hide_banner", "-loglevel", "error", "-show_entries", entries, "-of", "csv=p=0", path],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Indica si un píxel es claramente de los canales pedidos y no de los otros.
    /// </summary>
    /// <param name="weak">Por debajo de cuánto se da un canal por ausente.</param>
    /// <remarks>
    /// El umbral por defecto es generoso: entre el paso por YUV 4:2:0, la compresión de H.264 y el
    /// reescalado a 480p, un rojo puro llega como algo parecido a un rojo, no como 255-0-0 exacto.
    /// Y el propio verde de croma (<c>#00B140</c>) lleva un azul de 64 dentro. Lo que se comprueba
    /// es de qué color es, no que sea el mismo número; quien necesite apretar, pasa su umbral.
    /// </remarks>
    private static bool IsMostly(
        (byte R, byte G, byte B) pixel, bool red, bool green, bool blue, int weak = 80)
    {
        const int Strong = 90;

        return Check(pixel.R, red) && Check(pixel.G, green) && Check(pixel.B, blue);

        bool Check(byte value, bool expected) => expected ? value > Strong : value < weak;
    }

    private static string Describe((byte R, byte G, byte B) pixel) =>
        string.Create(CultureInfo.InvariantCulture, $"R={pixel.R} G={pixel.G} B={pixel.B}");
}
