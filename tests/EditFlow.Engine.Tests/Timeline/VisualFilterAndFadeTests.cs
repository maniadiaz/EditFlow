// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.PreviewCache;
using EditFlow.Engine.Probing;
using EditFlow.Engine.Tests.PreviewCache;

namespace EditFlow.Engine.Tests.Timeline;

public class VisualFilterAndFadeTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-filterfade-");

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
    public void A_fade_cannot_be_negative()
    {
        var clip = new Clip(Media("a.mp4", 5)) { FadeIn = TimeSpan.FromSeconds(-1) };

        Assert.Equal(TimeSpan.Zero, clip.FadeIn);
    }

    [Fact]
    public void The_two_fades_together_cannot_exceed_the_clips_duration()
    {
        var clip = new Clip(Media("a.mp4", 5)) { FadeIn = S(4) };

        // Solo caben 1 s más: el resto se recorta.
        clip.FadeOut = S(4);

        Assert.Equal(S(4), clip.FadeIn);
        Assert.Equal(S(1), clip.FadeOut);
    }

    [Fact]
    public void Setting_a_fade_alone_beyond_the_duration_clamps_to_the_whole_clip()
    {
        var clip = new Clip(Media("a.mp4", 5)) { FadeIn = S(50) };

        Assert.Equal(S(5), clip.FadeIn);
    }

    [Fact]
    public void Filter_and_fades_survive_cloning()
    {
        var clip = new Clip(Media("a.mp4", 10))
        {
            Filter = VisualFilterKind.Sepia,
            FadeIn = S(1),
            FadeOut = S(1),
        };

        var clone = clip.Clone();

        Assert.Equal(VisualFilterKind.Sepia, clone.Filter);
        Assert.Equal(S(1), clone.FadeIn);
        Assert.Equal(S(1), clone.FadeOut);
    }

    [Fact]
    public void Splitting_keeps_the_filter_on_both_halves_but_each_fade_only_on_its_own_edge()
    {
        var clip = new Clip(Media("a.mp4", 10))
        {
            Filter = VisualFilterKind.Vintage,
            FadeIn = S(1),
            FadeOut = S(1),
        };

        var second = clip.SplitAt(S(5))!;

        Assert.Equal(VisualFilterKind.Vintage, clip.Filter);
        Assert.Equal(VisualFilterKind.Vintage, second.Filter);

        // La primera mitad conserva su fundido de entrada, pero el de salida ya no tiene
        // sentido en un borde que ahora es un corte, no el final real del clip.
        Assert.Equal(S(1), clip.FadeIn);
        Assert.Equal(TimeSpan.Zero, clip.FadeOut);

        // La segunda mitad conserva el fundido de salida original; el de entrada tampoco
        // tiene sentido en un borde que ya no es el principio real del clip.
        Assert.Equal(TimeSpan.Zero, second.FadeIn);
        Assert.Equal(S(1), second.FadeOut);
    }

    [Fact]
    public void Filter_and_fades_survive_slicing()
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media("a.mp4", 10))
        {
            Filter = VisualFilterKind.Warm,
            FadeIn = S(1),
            FadeOut = S(1),
        };
        sequence.Video.Append(clip);

        // Un trozo que conserva ambos bordes del clip original conserva sus dos fundidos.
        var whole = SequenceSlicer.Slice(sequence, S(0), S(10));
        Assert.Equal(VisualFilterKind.Warm, whole.Video.Clips[0].Filter);
        Assert.Equal(S(1), whole.Video.Clips[0].FadeIn);
        Assert.Equal(S(1), whole.Video.Clips[0].FadeOut);

        // Un trozo intermedio, que no llega a ninguno de los dos bordes reales, pierde ambos
        // fundidos: fundir un corte interno sería incorrecto. El filtro, en cambio, se
        // conserva siempre: no depende del borde que se recorte.
        var middle = SequenceSlicer.Slice(sequence, S(3), S(7));
        Assert.Equal(VisualFilterKind.Warm, middle.Video.Clips[0].Filter);
        Assert.Equal(TimeSpan.Zero, middle.Video.Clips[0].FadeIn);
        Assert.Equal(TimeSpan.Zero, middle.Video.Clips[0].FadeOut);
    }

    [Fact]
    public void Applying_a_filter_is_undoable()
    {
        var clip = new Clip(Media("a.mp4"));
        var history = new UndoHistory();

        history.Do(new SetFilterCommand(clip, VisualFilterKind.Cool));
        Assert.Equal(VisualFilterKind.Cool, clip.Filter);

        history.Undo();
        Assert.Equal(VisualFilterKind.None, clip.Filter);

        history.Redo();
        Assert.Equal(VisualFilterKind.Cool, clip.Filter);
    }

    [Fact]
    public void Setting_fades_is_undoable()
    {
        var clip = new Clip(Media("a.mp4", 10)) { FadeIn = S(2) };
        var history = new UndoHistory();

        history.Do(new SetClipFadeCommand(clip, S(1), S(3)));
        Assert.Equal(S(1), clip.FadeIn);
        Assert.Equal(S(3), clip.FadeOut);

        history.Undo();
        Assert.Equal(S(2), clip.FadeIn);
        Assert.Equal(TimeSpan.Zero, clip.FadeOut);

        history.Redo();
        Assert.Equal(S(1), clip.FadeIn);
        Assert.Equal(S(3), clip.FadeOut);
    }

    // ------------------------------------------------------------- guardado

    [Fact]
    public async Task Filter_and_fades_are_saved_and_an_old_project_opens_without_them()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media("a.mp4", 10));
        var clip = new Clip(media) { Filter = VisualFilterKind.Vignette, FadeIn = S(1), FadeOut = S(2) };
        project.Timeline.Append(clip);
        project.Timeline.Append(new Clip(media));

        var path = Path.Combine(_workspace.FullName, "p.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

        Assert.Equal(VisualFilterKind.Vignette, loaded.Timeline.Clips[0].Filter);
        Assert.Equal(S(1), loaded.Timeline.Clips[0].FadeIn);
        Assert.Equal(S(2), loaded.Timeline.Clips[0].FadeOut);
        Assert.Equal(VisualFilterKind.None, loaded.Timeline.Clips[1].Filter);
        Assert.Equal(TimeSpan.Zero, loaded.Timeline.Clips[1].FadeIn);

        // Un proyecto de una versión anterior no tiene estos campos.
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["version"] = 8;
        foreach (var node in document["clips"]!.AsArray())
        {
            var obj = node!.AsObject();
            obj.Remove("filter");
            obj.Remove("fadeIn");
            obj.Remove("fadeOut");
        }

        var oldPath = Path.Combine(_workspace.FullName, "old.editflow");
        await File.WriteAllTextAsync(oldPath, document.ToJsonString());
        var reopened = (await ProjectSerializer.LoadAsync(oldPath, CancellationToken.None)).Project;

        Assert.Equal(VisualFilterKind.None, reopened.Timeline.Clips[0].Filter);
        Assert.Equal(TimeSpan.Zero, reopened.Timeline.Clips[0].FadeIn);
        Assert.Equal(TimeSpan.Zero, reopened.Timeline.Clips[0].FadeOut);
    }

    // ---------------------------------------------------------------- grafo

    [Fact]
    public void The_filter_and_the_fade_land_right_after_the_pixel_format_in_order()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 5))
        {
            Filter = VisualFilterKind.BlackAndWhite,
            FadeIn = S(1),
        });
        sequence.Video.Append(new Clip(Media("b.mp4", 5)));

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        Assert.Contains("setsar=1,format=yuv420p,hue=s=0,fade=t=in:st=0:d=1[v0]", graph, StringComparison.Ordinal);
        Assert.Contains("setsar=1,format=yuv420p[v1]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fade_also_silences_the_clips_own_audio()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 5)) { FadeIn = S(1), FadeOut = S(1) });

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        Assert.Contains("afade=t=in:st=0:d=1,afade=t=out:st=4:d=1", graph, StringComparison.Ordinal);
    }

    // ------------------------------------------- copia de preview

    [Fact]
    public void Filtering_one_clip_invalidates_only_its_preview_sections()
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

        second.Filter = VisualFilterKind.Sepia;
        manager.Update(sequence, settings);
        var after = manager.Sections.Select(s => s.Hash).ToArray();

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
        Assert.NotEqual(before[2], after[2]);
        Assert.NotEqual(before[3], after[3]);
    }

    [Fact]
    public void Fading_one_clip_invalidates_only_the_sections_that_touch_its_real_edges()
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

        // Como con la transición o el encuadre, un fundido solo se conserva en el trozo que
        // llega hasta el borde real del clip (SequenceSlicer lo vacía en cualquier otro): un
        // fundido de entrada invalida el primer trozo del segundo clip, uno de salida el último,
        // y ninguno de los dos toca el primer clip.
        second.FadeIn = S(1);
        second.FadeOut = S(1);
        manager.Update(sequence, settings);
        var after = manager.Sections.Select(s => s.Hash).ToArray();

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
        Assert.NotEqual(before[2], after[2]);
        Assert.NotEqual(before[3], after[3]);
    }
}

public class OverlayFadeTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-overlayfade-");

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

    [Fact]
    public async Task An_items_fades_are_saved_and_an_old_project_opens_without_them()
    {
        var project = new EditProject();
        var item = OverlayItem.CreateText(new TextStyle("Hola"), S(1), S(5));
        item.FadeIn = S(1);
        item.FadeOut = S(2);
        project.Sequence.AddOverlayTrack().TryAdd(item);

        var path = Path.Combine(_workspace.FullName, "p.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

        var loadedItem = Assert.Single(Assert.Single(loaded.Sequence.OverlayTracks).Items);
        Assert.Equal(S(1), loadedItem.FadeIn);
        Assert.Equal(S(2), loadedItem.FadeOut);

        // Un proyecto de una versión anterior no tiene estos campos.
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["version"] = 9;
        foreach (var layer in document["overlayTracks"]!.AsArray())
        {
            foreach (var savedItem in layer!["items"]!.AsArray())
            {
                var obj = savedItem!.AsObject();
                obj.Remove("fadeIn");
                obj.Remove("fadeOut");
            }
        }

        var oldPath = Path.Combine(_workspace.FullName, "old.editflow");
        await File.WriteAllTextAsync(oldPath, document.ToJsonString());
        var reopened = (await ProjectSerializer.LoadAsync(oldPath, CancellationToken.None)).Project;

        var reopenedItem = Assert.Single(Assert.Single(reopened.Sequence.OverlayTracks).Items);
        Assert.Equal(TimeSpan.Zero, reopenedItem.FadeIn);
        Assert.Equal(TimeSpan.Zero, reopenedItem.FadeOut);
    }

    [Fact]
    public void Fading_a_layer_item_invalidates_only_the_preview_sections_it_touches()
    {
        using var directory = new Workspace();
        using var manager = new PreviewCacheManager(new FFmpegTools("ffmpeg", "ffprobe", "prueba"), directory.Path);
        var settings = PreviewCacheSettings.For(540, 30);

        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 10)));
        var item = OverlayItem.CreateText(new TextStyle("Hola"), S(1), S(2));
        sequence.AddOverlayTrack().TryAdd(item);
        manager.Update(sequence, settings);
        var before = manager.Sections.Select(s => s.Hash).ToArray();

        item.FadeIn = S(0.5);
        manager.Update(sequence, settings);
        var after = manager.Sections.Select(s => s.Hash).ToArray();

        // El texto solo cae en el primer trozo (0-5 s): fundirlo no debe tocar la huella de
        // ningún otro.
        Assert.NotEqual(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
    }
}
