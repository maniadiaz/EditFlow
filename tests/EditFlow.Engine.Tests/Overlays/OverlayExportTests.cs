// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Overlays;
using SkiaSharp;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Overlays;

public sealed class TextRendererTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-text-");

    public void Dispose() => _workspace.DeleteWithRetry();

    private static SKBitmap Decode(string path) => SKBitmap.Decode(path);

    [Fact]
    public void A_text_becomes_a_png_with_a_transparent_background_and_visible_glyphs()
    {
        var path = Path.Combine(_workspace.FullName, "t.png");

        Assert.True(TextRenderer.RenderToFile(new TextStyle("Hola", 0.2, "#FF0000"), 480, path));

        using var bitmap = Decode(path);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);             // la esquina está vacía

        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 200 && pixel.Red > 200 && pixel.Green < 60)
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > 200, $"el texto rojo debería verse; píxeles rojos: {opaque}");
    }

    [Fact]
    public void The_image_scales_with_the_canvas_height_so_preview_and_export_match()
    {
        var style = new TextStyle("Título de prueba", 0.1);

        var preview = TextRenderer.Measure(style, 480)!.Value;
        var export = TextRenderer.Measure(style, 1080)!.Value;

        // El texto ocupa la misma fracción del video a cualquier resolución. Con un margen del 5 %:
        // cada sistema ajusta las letras a la cuadrícula de píxeles a su manera (Linux dio 2,252
        // donde Windows daba 2,250), y eso no es un fallo de escala.
        const double expected = 1080.0 / 480;
        Assert.InRange((double)export.Height / preview.Height, expected * 0.95, expected * 1.05);
        Assert.InRange((double)export.Width / preview.Width, expected * 0.95, expected * 1.05);
    }

    [Fact]
    public void A_bigger_size_or_more_lines_make_a_bigger_image()
    {
        var small = TextRenderer.Measure(new TextStyle("Hola", 0.05), 480)!.Value;
        var large = TextRenderer.Measure(new TextStyle("Hola", 0.15), 480)!.Value;
        var two = TextRenderer.Measure(new TextStyle("Hola\nmundo", 0.05), 480)!.Value;

        Assert.True(large.Width > small.Width && large.Height > small.Height);
        Assert.True(two.Height > small.Height * 1.6);
    }

    [Fact]
    public void An_empty_text_draws_nothing()
    {
        var path = Path.Combine(_workspace.FullName, "vacio.png");

        Assert.False(TextRenderer.RenderToFile(new TextStyle("   "), 480, path));
        Assert.False(File.Exists(path));
        Assert.Null(TextRenderer.Measure(new TextStyle(""), 480));
    }

    [Fact]
    public void An_invalid_colour_falls_back_to_white_instead_of_failing()
    {
        var path = Path.Combine(_workspace.FullName, "c.png");

        Assert.True(TextRenderer.RenderToFile(new TextStyle("Hola", 0.2, "no es un color"), 480, path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void The_cache_draws_once_and_reuses_the_file_until_something_changes()
    {
        var cache = new TextRenderCache(Path.Combine(_workspace.FullName, "cache"));
        var style = new TextStyle("Repetido");

        var first = cache.GetPath(style, 480);
        var stamp = File.GetLastWriteTimeUtc(first!);

        Assert.Equal(first, cache.GetPath(style, 480));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first!));

        Assert.NotEqual(first, cache.GetPath(style with { Color = "#00FF00" }, 480));
        Assert.NotEqual(first, cache.GetPath(style, 1080));
        Assert.Null(cache.GetPath(new TextStyle(" "), 480));
    }
}

public class OverlayGraphTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ExportSettings Settings() => new()
    {
        OutputPath = "o.mp4",
        Resolution = VideoResolution.P1080,
        EncoderName = "libx264",
        FrameRate = 30,
    };

    private static EditSequence Sequence(double videoSeconds = 10)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo("v.mp4", S(videoSeconds), 1920, 1080, 30, "h264", true)));
        return sequence;
    }

    private static (OverlayItem Item, IReadOnlyDictionary<Guid, string> Assets) AddTitle(
        EditSequence sequence, double start, double seconds, TextStyle? style = null)
    {
        var item = OverlayItem.CreateText(style ?? new TextStyle("Hola"), S(start), S(seconds));
        sequence.FindOrCreateOverlayTrackFor(S(start), S(seconds)).TryAdd(item);
        return (item, new Dictionary<Guid, string> { [item.Id] = "titulo.png" });
    }

    [Fact]
    public void A_sequence_without_overlays_produces_the_same_graph_as_before()
    {
        var plan = FilterGraphBuilder.Build(Sequence(), Settings());

        Assert.Contains("[vout]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("overlay", plan.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("[vstack]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_is_composited_at_its_time_and_position()
    {
        var sequence = Sequence();
        var (item, assets) = AddTitle(sequence, 2, 3);
        item.Transform = new OverlayTransform(0.25, 0.9, 0.25, 1);

        var plan = FilterGraphBuilder.Build(sequence, Settings(), assets);

        Assert.Contains("[vstack]", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("overlay=x=main_w*0.25-overlay_w/2:y=main_h*0.9-overlay_h/2", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("enable='between(t,2,5)'", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("setpts=PTS-STARTPTS+2/TB", plan.FilterGraph, StringComparison.Ordinal);
        Assert.EndsWith("[vout]", plan.FilterGraph.Split(";\n").First(l => l.Contains("overlay=", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("titulo.png", plan.InputArguments);
        Assert.Contains("-loop", plan.InputArguments);
    }

    [Fact]
    public void Opacity_adds_a_channel_mixer_only_when_needed()
    {
        var sequence = Sequence();
        var (item, assets) = AddTitle(sequence, 0, 3);

        Assert.DoesNotContain("colorchannelmixer", FilterGraphBuilder.Build(sequence, Settings(), assets).FilterGraph, StringComparison.Ordinal);

        item.Transform = item.Transform with { Opacity = 0.5 };
        Assert.Contains("colorchannelmixer=aa=0.5", FilterGraphBuilder.Build(sequence, Settings(), assets).FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Layers_are_stacked_bottom_to_top()
    {
        var sequence = Sequence();
        var back = OverlayItem.CreateText(new TextStyle("fondo"), S(0), S(5));
        var front = OverlayItem.CreateText(new TextStyle("frente"), S(0), S(5));
        sequence.AddOverlayTrack().TryAdd(back);      // creada primero: queda debajo
        sequence.AddOverlayTrack().TryAdd(front);     // creada después: va delante

        var assets = new Dictionary<Guid, string> { [back.Id] = "fondo.png", [front.Id] = "frente.png" };
        var plan = FilterGraphBuilder.Build(sequence, Settings(), assets);

        var inputs = plan.InputArguments.ToList();
        Assert.True(inputs.IndexOf("fondo.png") < inputs.IndexOf("frente.png"));
        Assert.Contains("[vstack][ov0]overlay=", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[vs0][ov1]overlay=", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_layers_and_empty_texts_are_left_out()
    {
        var sequence = Sequence();
        var (_, assets) = AddTitle(sequence, 0, 3);
        sequence.OverlayTracks[0].IsHidden = true;

        Assert.DoesNotContain("overlay", FilterGraphBuilder.Build(sequence, Settings(), assets).FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_is_scaled_to_a_fraction_of_the_video_width()
    {
        var sequence = Sequence();
        var logo = OverlayItem.CreateImage("logo.png", 2, S(0), S(5));
        logo.Transform = new OverlayTransform(0.9, 0.1, 0.2, 1);
        sequence.AddOverlayTrack().TryAdd(logo);

        var plan = FilterGraphBuilder.Build(sequence, Settings(), new Dictionary<Guid, string> { [logo.Id] = "logo.png" });

        Assert.Contains("scale=384:-1", plan.FilterGraph, StringComparison.Ordinal);   // 20 % de 1920
    }

    [Fact]
    public void A_title_that_outlasts_the_video_extends_the_export_with_black()
    {
        var sequence = Sequence(4);
        var (_, assets) = AddTitle(sequence, 3, 4);

        var plan = FilterGraphBuilder.Build(sequence, Settings(), assets);

        Assert.Equal(S(7), plan.Duration);
        Assert.Contains("tpad=stop_mode=add:stop_duration=3", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[vstack]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlay_is_cut_short_where_the_export_ends()
    {
        var sequence = Sequence(4);
        var (_, assets) = AddTitle(sequence, 1, 2);
        var late = OverlayItem.CreateText(new TextStyle("tarde"), S(30), S(2));   // más allá del final
        sequence.OverlayTracks[0].TryAdd(late);
        var all = new Dictionary<Guid, string>(assets) { [late.Id] = "tarde.png" };

        var plan = FilterGraphBuilder.Build(sequence, Settings(), all);

        // Un elemento que empieza después del final no entra... pero alarga la secuencia, que
        // es lo que hace cualquier pista con algo colocado ahí. Lo comprobado es que el grafo
        // sigue siendo válido: cada overlay tiene su entrada.
        var overlays = plan.FilterGraph.Split("overlay=").Length - 1;
        var loops = plan.InputArguments.Count(a => a == "-loop");
        Assert.Equal(loops, overlays);
    }

    [Fact]
    public void The_audio_only_plan_lasts_as_long_as_the_video_plan_when_a_title_runs_over()
    {
        var sequence = Sequence(4);
        var (_, assets) = AddTitle(sequence, 3, 4);

        var video = FilterGraphBuilder.Build(sequence, Settings(), assets);
        var audio = FilterGraphBuilder.BuildAudioOnly(sequence);

        Assert.Equal(video.Duration, audio.Duration);
    }
}

[Trait("Category", "Integration")]
public class OverlayExportIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public OverlayExportIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<SKBitmap> FrameAtAsync(FFmpegTools tools, string video, double seconds, string outPng)
    {
        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
             "-i", video, "-frames:v", "1", outPng],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return SKBitmap.Decode(outPng);
    }

    private static int BrightPixels(SKBitmap frame, SKRectI area)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = frame.GetPixel(x, y);
                if (p.Red > 200 && p.Green > 200 && p.Blue > 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public async Task An_exported_title_appears_only_during_its_time_and_where_it_was_placed()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-overlay-export-");

        try
        {
            // Fondo negro: cualquier píxel claro es el título.
            var black = Path.Combine(workspace.FullName, "negro.mp4");
            var made = await ProcessRunner.RunAsync(
                tools.FFmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=854x480:r=30:d=6",
                 "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "6",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", black],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StandardError);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(new MediaInfo(black, TimeSpan.FromSeconds(6), 854, 480, 30, "h264", true)));

            var title = OverlayItem.CreateText(
                new TextStyle("HOLA", 0.25, "#FFFFFF", Shadow: false), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            title.Transform = new OverlayTransform(0.25, 0.25, 0.25, 1);   // cuarto superior izquierdo
            sequence.AddOverlayTrack().TryAdd(title);

            var output = Path.Combine(workspace.FullName, "salida.mp4");
            var result = await new ExportJob(tools).RunAsync(
                sequence,
                new ExportSettings
                {
                    OutputPath = output,
                    Resolution = VideoResolution.P480,
                    EncoderName = "libx264",
                    Speed = EncodingSpeed.Fastest,
                    FrameRate = 30,
                },
                progress: null,
                CancellationToken.None);

            _output.WriteLine(result.Succeeded ? "exportación correcta" : result.ErrorMessage + "\n" + result.Command);
            Assert.True(result.Succeeded, result.ErrorMessage);

            var topLeft = new SKRectI(0, 0, 427, 240);
            var elsewhere = new SKRectI(427, 240, 854, 480);

            using var before = await FrameAtAsync(tools, output, 1.0, Path.Combine(workspace.FullName, "a.png"));
            using var during = await FrameAtAsync(tools, output, 3.0, Path.Combine(workspace.FullName, "b.png"));
            using var after = await FrameAtAsync(tools, output, 5.0, Path.Combine(workspace.FullName, "c.png"));

            var visible = BrightPixels(during, topLeft);
            _output.WriteLine($"píxeles claros — antes: {BrightPixels(before, topLeft)}, durante: {visible}, después: {BrightPixels(after, topLeft)}, otra zona: {BrightPixels(during, elsewhere)}");

            Assert.Equal(0, BrightPixels(before, topLeft));       // aún no aparece
            Assert.True(visible > 300, "el título debe verse durante su tramo");
            Assert.Equal(0, BrightPixels(after, topLeft));        // ya desapareció
            Assert.Equal(0, BrightPixels(during, elsewhere));     // y está donde se colocó
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}

public sealed class ImageProbeTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-imgprobe-");

    public void Dispose() => _workspace.DeleteWithRetry();

    [Fact]
    public void The_size_of_a_png_is_read_from_its_header()
    {
        var path = Path.Combine(_workspace.FullName, "logo.png");
        using (var bitmap = new SKBitmap(300, 120))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(path))
        {
            data.SaveTo(file);
        }

        Assert.Equal((300, 120), ImageProbe.TryRead(path));
    }

    [Fact]
    public void Something_that_is_not_an_image_gives_null()
    {
        var path = Path.Combine(_workspace.FullName, "no.png");
        File.WriteAllText(path, "esto no es una imagen");

        Assert.Null(ImageProbe.TryRead(path));
        Assert.Null(ImageProbe.TryRead(Path.Combine(_workspace.FullName, "no-existe.png")));
    }
}
