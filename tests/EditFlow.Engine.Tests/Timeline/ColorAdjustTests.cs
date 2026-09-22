// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Playback;
using EditFlow.Engine.PreviewCache;
using EditFlow.Engine.Probing;
using EditFlow.Engine.Tests.PreviewCache;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Timeline;

public class ColorAdjustTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-color-");

    public void Dispose()
    {
        try { _workspace.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private MediaInfo Media(string name, double seconds = 20)
    {
        var path = Path.Combine(_workspace.FullName, name);
        File.WriteAllText(path, "no es un video");
        return new MediaInfo(path, S(seconds), 1920, 1080, 30, "h264", true);
    }

    private static ExportSettings Settings() => new()
    {
        OutputPath = "salida.mp4",
        Resolution = VideoResolution.P720,
        EncoderName = "libx264",
    };

    // ---------------------------------------------------------------- modelo

    [Fact]
    public void Nothing_adjusted_means_no_filters()
    {
        Assert.True(ColorAdjust.None.IsNone);
        Assert.Null(ColorFilter.Build(ColorAdjust.None));
        Assert.Null(ColorFilter.Build(null));
        Assert.Null(ColorFilter.Build(new ColorAdjust(0.01, 0, 0, 0)));
    }

    [Fact]
    public void Values_are_clamped_and_garbage_becomes_zero()
    {
        var color = new ColorAdjust(500, -500, double.NaN, double.PositiveInfinity).Clamped();

        Assert.Equal(100, color.Exposure);
        Assert.Equal(-100, color.Contrast);
        Assert.Equal(0, color.Saturation);
        Assert.Equal(0, color.Temperature);
    }

    [Fact]
    public void Each_control_produces_its_own_filter()
    {
        Assert.Equal("eq=contrast=1.5", ColorFilter.Build(new ColorAdjust(Contrast: 50)));
        Assert.Equal("eq=saturation=0", ColorFilter.Build(new ColorAdjust(Saturation: -100)));
        Assert.Equal("eq=contrast=0.5:saturation=2", ColorFilter.Build(new ColorAdjust(Contrast: -50, Saturation: 100)));

        var exposure = ColorFilter.Build(new ColorAdjust(Exposure: 100))!;
        Assert.StartsWith("lutyuv=y='clip(val*2.8284,minval,maxval)'", exposure, StringComparison.Ordinal);

        var warm = ColorFilter.Build(new ColorAdjust(Temperature: 100))!;
        Assert.Contains("u='clip(val-(maxval-minval)/224*14,minval,maxval)'", warm, StringComparison.Ordinal);
        Assert.Contains("v='clip(val+(maxval-minval)/224*14,minval,maxval)'", warm, StringComparison.Ordinal);

        var cool = ColorFilter.Build(new ColorAdjust(Temperature: -100))!;
        Assert.Contains("u='clip(val+", cool, StringComparison.Ordinal);
        Assert.Contains("v='clip(val-", cool, StringComparison.Ordinal);
    }

    [Fact]
    public void Luminance_and_chroma_come_before_contrast_and_saturation()
    {
        var filter = ColorFilter.Build(new ColorAdjust(20, 30, 40, 50))!;

        Assert.True(filter.IndexOf("lutyuv", StringComparison.Ordinal) < filter.IndexOf("eq=", StringComparison.Ordinal));
    }

    [Fact]
    public void Color_survives_splitting_cloning_and_lifting()
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media("a.mp4")) { Color = new ColorAdjust(10, 20, 30, 40) };
        sequence.Video.Append(clip);

        var second = sequence.Video.SplitAt(S(5))!;
        Assert.Equal(clip.Color, second.Color);
        Assert.Equal(clip.Color, clip.Clone().Color);

        new LiftClipToLayerCommand(sequence, second).Execute();
        Assert.Equal(clip.Color, sequence.OverlayTracks[0].Items[0].Color);

        var slice = SequenceSlicer.Slice(sequence, S(0), S(3));
        Assert.Equal(clip.Color, slice.Video.Clips[0].Color);
    }

    [Fact]
    public void Setting_the_color_is_undoable_and_can_start_from_a_value_already_shown()
    {
        var clip = new Clip(Media("a.mp4"));
        var history = new UndoHistory();

        // Mientras se arrastraba el deslizador el modelo ya llevaba el valor provisional.
        clip.Color = new ColorAdjust(Exposure: 40);
        history.Do(new SetColorCommand(clip, new ColorAdjust(Exposure: 40), previous: ColorAdjust.None));
        Assert.Equal(40, clip.Color.Exposure);

        history.Undo();
        Assert.True(clip.Color.IsNone);

        history.Redo();
        Assert.Equal(40, clip.Color.Exposure);
    }

    [Fact]
    public void A_video_overlay_can_be_adjusted_too()
    {
        var item = OverlayItem.CreateVideo(Media("b.mp4"), TimeSpan.Zero, S(1), S(3));

        new SetColorCommand(item, new ColorAdjust(Saturation: -100)).Execute();

        Assert.Equal(-100, item.Color.Saturation);
    }

    // ------------------------------------------------------------- guardado

    [Fact]
    public async Task Color_is_saved_and_an_old_project_opens_without_it()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media("a.mp4"));
        var clip = new Clip(media) { Color = new ColorAdjust(10, -20, 30, -40) };
        project.Timeline.Append(clip);
        project.Timeline.Append(new Clip(media));
        project.Sequence.AddOverlayTrack().TryAdd(
            OverlayItem.CreateVideo(media, TimeSpan.Zero, S(1), S(3)));
        project.Sequence.OverlayTracks[0].Items[0].Color = new ColorAdjust(Temperature: 25);

        var path = Path.Combine(_workspace.FullName, "p.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

        Assert.Equal(new ColorAdjust(10, -20, 30, -40), loaded.Timeline.Clips[0].Color);
        Assert.True(loaded.Timeline.Clips[1].Color.IsNone);
        Assert.Equal(25, loaded.Sequence.OverlayTracks[0].Items[0].Color.Temperature);

        // Un proyecto de la versión anterior no tiene el campo.
        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\"color\": null", text, StringComparison.Ordinal);
        var document = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        document["version"] = 4;
        foreach (var node in document["clips"]!.AsArray())
        {
            node!.AsObject().Remove("color");
        }

        foreach (var layer in document["overlayTracks"]!.AsArray())
        {
            foreach (var item in layer!["items"]!.AsArray())
            {
                item!.AsObject().Remove("color");
            }
        }

        var old = document.ToJsonString();
        var oldPath = Path.Combine(_workspace.FullName, "old.editflow");
        await File.WriteAllTextAsync(oldPath, old);
        var reopened = (await ProjectSerializer.LoadAsync(oldPath, CancellationToken.None)).Project;

        Assert.True(reopened.Timeline.Clips[0].Color.IsNone);
    }

    // ---------------------------------------------------------------- grafo

    [Fact]
    public void An_adjusted_clip_gets_the_filters_right_after_the_pixel_format_and_an_unadjusted_one_does_not()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 5)) { Color = new ColorAdjust(Saturation: -100) });
        sequence.Video.Append(new Clip(Media("b.mp4", 5)));

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        Assert.Contains("setsar=1,format=yuv420p,eq=saturation=0[v0]", graph, StringComparison.Ordinal);
        Assert.Contains("setsar=1,format=yuv420p[v1]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_video_overlay_is_adjusted_in_yuv_before_becoming_rgba()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 10)));
        var track = sequence.AddOverlayTrack();
        var item = OverlayItem.CreateVideo(Media("b.mp4", 10), TimeSpan.Zero, S(1), S(3));
        item.Color = new ColorAdjust(Contrast: 20);
        track.TryAdd(item);

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        Assert.Contains("scale=1280:-2,format=yuv420p,eq=contrast=1.2,format=rgba", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_reader_and_the_frame_extractor_carry_the_same_filters()
    {
        var filter = ColorFilter.Build(new ColorAdjust(Exposure: 30));

        var reader = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 1280, 720, 30, false, filter));
        Assert.Contains("pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=black,format=yuv420p,lutyuv=", reader, StringComparison.Ordinal);

        var plain = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 1280, 720, 30));
        Assert.DoesNotContain("lutyuv", plain, StringComparison.Ordinal);

        var frame = string.Join(' ', EditFlow.Engine.Thumbnails.FrameExtractor.BuildArguments("v.mp4", TimeSpan.Zero, 480, "o.jpg", filter));
        Assert.Contains("scale=480:-2,format=yuv420p,lutyuv=", frame, StringComparison.Ordinal);
    }

    // ------------------------------------------- copia de preview

    [Fact]
    public void Adjusting_one_clip_invalidates_only_its_preview_sections()
    {
        using var directory = new Workspace();
        using var manager = new PreviewCacheManager(new FFmpegTools("ffmpeg", "ffprobe", "prueba"), directory.Path);
        var settings = PreviewCacheSettings.For(540, 30);

        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 10)));
        var second = new Clip(Media("b.mp4", 10));
        sequence.Video.Append(second);
        manager.Update(sequence, settings);
        var before = manager.Sections.Select(s => s.Hash).ToArray();

        second.Color = new ColorAdjust(Exposure: 30);
        manager.Update(sequence, settings);
        var after = manager.Sections.Select(s => s.Hash).ToArray();

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
        Assert.NotEqual(before[2], after[2]);
        Assert.NotEqual(before[3], after[3]);
    }

    [Fact]
    public void Splitting_an_adjusted_clip_still_changes_nothing_but_adjusting_half_of_it_does()
    {
        using var directory = new Workspace();
        using var manager = new PreviewCacheManager(new FFmpegTools("ffmpeg", "ffprobe", "prueba"), directory.Path);
        var settings = PreviewCacheSettings.For(540, 30);

        var sequence = new EditSequence();
        var clip = new Clip(Media("a.mp4", 20)) { Color = new ColorAdjust(Saturation: -50) };
        sequence.Video.Append(clip);
        manager.Update(sequence, settings);
        var before = manager.Sections.Select(s => s.Hash).ToArray();

        var second = sequence.Video.SplitAt(S(7.5))!;
        manager.Update(sequence, settings);
        Assert.Equal(before, manager.Sections.Select(s => s.Hash).ToArray());

        second.Color = ColorAdjust.None;
        manager.Update(sequence, settings);
        var changed = manager.Sections.Select(s => s.Hash).ToArray();
        Assert.Equal(before[0], changed[0]);          // 0-5 s: antes del corte
        Assert.NotEqual(before[1], changed[1]);       // 5-10 s: contiene el corte
        Assert.NotEqual(before[3], changed[3]);       // después del corte
    }
}

[Trait("Category", "Integration")]
public class ColorAdjustExportTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private readonly ITestOutputHelper _output;

    public ColorAdjustExportTests(ITestOutputHelper output) => _output = output;

    private static async Task<MediaInfo> MakeAsync(FFmpegTools tools, string directory, string color)
    {
        var path = Path.Combine(directory, $"c-{color.Replace("0x", string.Empty, StringComparison.Ordinal)}.mp4");
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c={color}:size=320x180:rate=30:duration=3",
             "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return await new FFprobeService(tools).ProbeAsync(path, CancellationToken.None);
    }

    /// <summary>Color medio (B, G, R) del centro de un fotograma exportado.</summary>
    private static async Task<(double B, double G, double R)> CenterColorAsync(
        FFmpegTools tools, MediaInfo media, ColorAdjust color)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(media) { Color = color });

        var output = Path.Combine(Path.GetDirectoryName(media.Path)!, "salida-" + Guid.NewGuid().ToString("N") + ".mp4");
        var result = await new ExportJob(tools).RunAsync(
            sequence,
            new ExportSettings
            {
                OutputPath = output,
                Resolution = new VideoResolution(320, 180, "prueba"),
                EncoderName = "libx264",
                Speed = EncodingSpeed.Fastest,
                IncludeAudio = false,
            },
            null,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        using var player = new VideoPlayer(tools, 320, 180, 30);
        player.Configure(320, 180, 30, hardwareDecoding: false);
        byte[]? pixels = null;
        player.FrameReady = f => { pixels ??= f.Pixels.ToArray(); };
        await player.OpenAsync(output, S(1));
        for (var i = 0; i < 40 && pixels is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(pixels);

        double b = 0, g = 0, r = 0;
        var count = 0;
        for (var y = 70; y < 110; y++)
        {
            for (var x = 140; x < 180; x++)
            {
                var i = ((y * 320) + x) * 4;
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
                count++;
            }
        }

        return (b / count, g / count, r / count);
    }

    private async Task<(FFmpegTools Tools, string Directory)?> SetUpAsync()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return null;
        }

        await Task.CompletedTask;
        return (tools, Directory.CreateTempSubdirectory("editflow-colorexport-").FullName);
    }

    [Fact]
    public async Task The_controls_change_the_exported_picture_in_the_expected_direction()
    {
        if (await SetUpAsync() is not var (tools, directory))
        {
            return;
        }

        try
        {
            var gray = await MakeAsync(tools, directory, "0x808080");
            var red = await MakeAsync(tools, directory, "0xC03020");

            var plain = await CenterColorAsync(tools, gray, ColorAdjust.None);
            var bright = await CenterColorAsync(tools, gray, new ColorAdjust(Exposure: 100));
            var dark = await CenterColorAsync(tools, gray, new ColorAdjust(Exposure: -100));
            var warm = await CenterColorAsync(tools, gray, new ColorAdjust(Temperature: 100));
            var cool = await CenterColorAsync(tools, gray, new ColorAdjust(Temperature: -100));
            var redPlain = await CenterColorAsync(tools, red, ColorAdjust.None);
            var redGrey = await CenterColorAsync(tools, red, new ColorAdjust(Saturation: -100));
            var redVivid = await CenterColorAsync(tools, red, new ColorAdjust(Saturation: 100));

            _output.WriteLine($"gris {plain}; +exposición {bright}; -exposición {dark}; cálido {warm}; frío {cool}");
            _output.WriteLine($"rojo {redPlain}; sin color {redGrey}; vivo {redVivid}");

            Assert.True(bright.G > plain.G + 40, "más exposición aclara");
            Assert.True(dark.G < plain.G - 40, "menos exposición oscurece");

            Assert.True(warm.R > warm.B + 10, "cálido: más rojo que azul");
            Assert.True(cool.B > cool.R + 10, "frío: más azul que rojo");
            Assert.InRange(Math.Abs(plain.R - plain.B), 0, 6);                  // el gris sin ajuste sigue siendo gris

            Assert.InRange(Math.Abs(redGrey.R - redGrey.G), 0, 12);             // saturación -100: en gris
            Assert.InRange(Math.Abs(redGrey.R - redGrey.B), 0, 12);
            Assert.True(redVivid.R - redVivid.G > redPlain.R - redPlain.G, "más saturación separa más los colores");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
