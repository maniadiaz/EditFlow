// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;
using EditFlow.Engine.Probing;
using EditFlow.Engine.PreviewCache;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.PreviewCache;

/// <summary>Carpeta temporal que se borra al terminar la prueba.</summary>
internal sealed class Workspace : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("editflow-pcache-");

    public string Path => _directory.FullName;

    public void Dispose() => _directory.DeleteWithRetry();
}

public class SequenceSlicerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Media(string name, double seconds) =>
        new($"C:/no-existe/{name}.mp4", S(seconds), 1920, 1080, 30, "h264", true);

    private static EditSequence TwoClips()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a", 8)));
        sequence.Video.Append(new Clip(Media("b", 10), S(2), S(10)));   // 8 s: de 2 a 10
        return sequence;
    }

    [Fact]
    public void A_slice_across_two_clips_keeps_the_right_part_of_each()
    {
        var slice = SequenceSlicer.Slice(TwoClips(), S(6), S(10));

        Assert.Equal(2, slice.Video.Clips.Count);
        Assert.Equal(S(6), slice.Video.Clips[0].SourceIn);
        Assert.Equal(S(8), slice.Video.Clips[0].SourceOut);
        Assert.Equal(S(2), slice.Video.Clips[1].SourceIn);
        Assert.Equal(S(4), slice.Video.Clips[1].SourceOut);
        Assert.Equal(S(4), slice.Video.Duration);
    }

    [Fact]
    public void The_slices_of_a_sequence_add_up_to_the_whole_video()
    {
        var sequence = TwoClips();

        var total = TimeSpan.Zero;
        for (var start = TimeSpan.Zero; start < sequence.Video.Duration; start += S(5))
        {
            var end = TimeSpan.FromTicks(Math.Min((start + S(5)).Ticks, sequence.Video.Duration.Ticks));
            total += SequenceSlicer.Slice(sequence, start, end).Video.Duration;
        }

        Assert.Equal(sequence.Video.Duration, total);
    }

    [Fact]
    public void Slicing_never_touches_the_original_and_silences_the_copy()
    {
        var sequence = TwoClips();
        var before = sequence.Video.Clips.Select(c => (c.SourceIn, c.SourceOut)).ToList();

        var slice = SequenceSlicer.Slice(sequence, S(3), S(12));

        Assert.Equal(before, sequence.Video.Clips.Select(c => (c.SourceIn, c.SourceOut)).ToList());
        Assert.All(slice.Video.Clips, c => Assert.False(c.HasOwnAudio));
        Assert.Empty(slice.AudioTracks);
    }

    [Fact]
    public void Overlays_are_cut_to_the_interval_and_rebased_to_zero()
    {
        var sequence = TwoClips();
        var track = sequence.AddOverlayTrack();
        track.TryAdd(OverlayItem.CreateText(new TextStyle("Hola"), S(4), S(8)));   // de 4 a 12

        var middle = SequenceSlicer.Slice(sequence, S(5), S(10)).OverlayTracks.Single().Items.Single();
        Assert.Equal(TimeSpan.Zero, middle.Start);
        Assert.Equal(S(5), middle.Duration);

        var tail = SequenceSlicer.Slice(sequence, S(10), S(15)).OverlayTracks.Single().Items.Single();
        Assert.Equal(TimeSpan.Zero, tail.Start);
        Assert.Equal(S(2), tail.Duration);

        Assert.Empty(SequenceSlicer.Slice(sequence, S(0), S(4)).OverlayTracks);
        Assert.Equal("Hola", middle.Text!.Content);
    }

    [Fact]
    public void A_tiny_overlay_remainder_is_still_kept()
    {
        var sequence = TwoClips();
        sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateText(new TextStyle("Hola"), S(4), S(8.05)));

        var slice = SequenceSlicer.Slice(sequence, S(10), S(15));

        Assert.Equal(S(2.05), slice.OverlayTracks.Single().Items.Single().Duration);

        var sliver = SequenceSlicer.Slice(sequence, S(12), S(15));
        Assert.Equal(TimeSpan.FromMilliseconds(50), sliver.OverlayTracks.Single().Items.Single().Duration);
    }

    [Fact]
    public void Hidden_layers_are_left_out_because_they_are_not_drawn()
    {
        var sequence = TwoClips();
        var track = sequence.AddOverlayTrack();
        track.TryAdd(OverlayItem.CreateText(new TextStyle("Oculto"), S(1), S(3)));
        track.IsHidden = true;

        Assert.Empty(SequenceSlicer.Slice(sequence, S(0), S(5)).OverlayTracks);
    }

    [Fact]
    public void A_transition_moves_where_the_second_clip_starts_in_a_slice()
    {
        var sequence = TwoClips();
        sequence.Video.Clips[1].TransitionIn = new Transition(TransitionKind.Dissolve, S(2));

        // Sin la transición, b empezaría en el segundo 8 (Layout ya lo adelanta a 6): la
        // rebanada completa debe seguir devolviendo el clip entero de cada uno.
        var slice = SequenceSlicer.Slice(sequence, S(0), S(14));

        Assert.Equal(2, slice.Video.Clips.Count);
        Assert.Equal(S(0), slice.Video.Clips[0].SourceIn);
        Assert.Equal(S(8), slice.Video.Clips[0].SourceOut);
        Assert.Equal(S(2), slice.Video.Clips[1].SourceIn);
        Assert.Equal(S(10), slice.Video.Clips[1].SourceOut);
        Assert.Equal(TransitionKind.Dissolve, slice.Video.Clips[1].TransitionIn.Kind);
    }

    [Fact]
    public void A_slice_that_cuts_into_the_middle_of_a_transition_drops_it_instead_of_faking_it()
    {
        // Si el trozo no arranca justo donde empieza el clip en la timeline compuesta, se
        // perdió el tramo que se funde con el anterior: conservar la transición aquí la
        // fundiría con lo que sea que quede delante en esta rebanada, que ya no es el clip
        // correcto. Se prefiere un corte seco a un fundido mal hecho.
        var sequence = TwoClips();
        sequence.Video.Clips[1].TransitionIn = new Transition(TransitionKind.Dissolve, S(2));

        var slice = SequenceSlicer.Slice(sequence, S(10), S(14));

        Assert.Single(slice.Video.Clips);
        Assert.True(slice.Video.Clips[0].TransitionIn.IsNone);
    }

    [Fact]
    public void The_front_layer_stays_in_front()
    {
        var sequence = TwoClips();
        var back = sequence.AddOverlayTrack("fondo");
        var front = sequence.AddOverlayTrack("frente");
        back.TryAdd(OverlayItem.CreateText(new TextStyle("atrás"), S(0), S(5)));
        front.TryAdd(OverlayItem.CreateText(new TextStyle("delante"), S(0), S(5)));

        var slice = SequenceSlicer.Slice(sequence, S(0), S(5));

        Assert.Equal(["frente", "fondo"], slice.OverlayTracks.Select(t => t.Name).ToArray());
    }
}

public class PreviewCacheSectionsTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly FFmpegTools NoTools = new("ffmpeg", "ffprobe", "prueba");
    private static readonly PreviewCacheSettings Settings = PreviewCacheSettings.For(540, 30);

    private static PreviewCacheManager NewManager(string directory) => new(NoTools, directory);

    private static EditSequence Sequence(double firstSeconds = 8, double secondSeconds = 8)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo("C:/no-existe/a.mp4", S(firstSeconds), 1920, 1080, 30, "h264", true)));
        sequence.Video.Append(new Clip(new MediaInfo("C:/no-existe/b.mp4", S(secondSeconds), 1920, 1080, 30, "h264", true)));
        return sequence;
    }

    private static string[] Hashes(PreviewCacheManager manager) =>
        manager.Sections.Select(s => s.Hash).ToArray();

    [Fact]
    public void The_timeline_is_split_into_five_second_sections_and_a_short_remainder_joins_the_last()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        manager.Update(Sequence(8, 8), Settings);
        Assert.Equal(4, manager.Sections.Count);                       // 16 s = 5 + 5 + 5 + 1
        Assert.Equal(S(15), manager.Sections[3].Start);
        Assert.Equal(S(16), manager.Sections[3].End);

        manager.Update(Sequence(8, 8.01), Settings);
        Assert.Equal(4, manager.Sections.Count);                       // 10 ms de resto: no crea otra
        Assert.Equal(S(16.01), manager.Sections[3].End);
    }

    [Fact]
    public void Nothing_is_rendered_yet_so_every_section_needs_a_render()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        manager.Update(Sequence(), Settings);

        Assert.All(manager.Sections, s => Assert.Equal(SectionState.NeedsRender, s.State));
        Assert.Equal(0, manager.Summary.Ready);
        Assert.Null(manager.FindRun(S(1)));
    }

    [Fact]
    public void An_edit_only_changes_the_sections_it_touches()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence();
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        // Un título de 6 a 8 s cae solo en la segunda sección (5–10 s).
        sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateText(new TextStyle("Hola"), S(6), S(2)));
        manager.Update(sequence, Settings);
        var after = Hashes(manager);

        Assert.Equal(before[0], after[0]);
        Assert.NotEqual(before[1], after[1]);
        Assert.Equal(before[2], after[2]);
        Assert.Equal(before[3], after[3]);
    }

    [Fact]
    public void Appending_a_clip_leaves_the_earlier_sections_alone()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence();
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        sequence.Video.Append(new Clip(new MediaInfo("C:/no-existe/c.mp4", S(9), 1920, 1080, 30, "h264", true)));
        manager.Update(sequence, Settings);
        var after = Hashes(manager);

        Assert.Equal(before[..3], after[..3]);   // la de 15–16 s sí cambia: ahora hay más video detrás
        Assert.NotEqual(before[3], after[3]);
        Assert.Equal(5, after.Length);
    }

    [Fact]
    public void Trimming_the_start_of_the_first_clip_shifts_everything_after_it()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence();
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        sequence.Video.Clips[0].TrimStart(S(1));
        manager.Update(sequence, Settings);
        var after = Hashes(manager);

        for (var i = 0; i < after.Length; i++)
        {
            Assert.NotEqual(before[i], after[i]);
        }
    }

    [Fact]
    public void Undoing_an_edit_brings_back_the_same_hashes()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence();
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        var track = sequence.AddOverlayTrack();
        var item = OverlayItem.CreateText(new TextStyle("Hola"), S(6), S(2));
        track.TryAdd(item);
        manager.Update(sequence, Settings);
        Assert.NotEqual(before[1], Hashes(manager)[1]);

        track.Remove(item);
        manager.Update(sequence, Settings);

        Assert.Equal(before, Hashes(manager));
    }

    [Fact]
    public void Changing_the_render_quality_invalidates_every_section()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence();
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        manager.Update(sequence, PreviewCacheSettings.For(270, 30));
        var half = Hashes(manager);

        manager.Update(sequence, PreviewCacheSettings.For(540, 60));
        var faster = Hashes(manager);

        for (var i = 0; i < before.Length; i++)
        {
            Assert.NotEqual(before[i], half[i]);
            Assert.NotEqual(before[i], faster[i]);
        }
    }

    [Fact]
    public void Changing_a_clips_speed_shortens_the_timeline_and_invalidates_its_section()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence(8, 8);   // 16 s: 5+5+5+1
        manager.Update(sequence, Settings);
        var before = Hashes(manager);
        Assert.Equal(4, before.Length);

        sequence.Video.Clips[1].Speed = 2;   // el segundo clip pasa de 8s a 4s: 12s en total
        manager.Update(sequence, Settings);
        var after = Hashes(manager);

        Assert.Equal(3, after.Length);       // 12s = 5+5+2, ya no sobra un trozo de 1s
        Assert.Equal(before[0], after[0]);   // 0-5s: por completo dentro del primer clip, sin tocar
        Assert.NotEqual(before[1], after[1]);
    }

    [Fact]
    public void Lifting_a_clip_to_a_layer_invalidates_only_the_sections_it_covered()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence(8, 8);
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        new LiftClipToLayerCommand(sequence, sequence.Video.Clips[1]).Execute();   // sube 8-16 s
        manager.Update(sequence, Settings);
        var after = Hashes(manager);

        Assert.Equal(before[0], after[0]);          // 0-5 s
        Assert.NotEqual(before[1], after[1]);       // 5-10 s: mezcla lo que quedó y el hueco
        Assert.NotEqual(before[2], after[2]);       // 10-15 s
    }

    [Fact]
    public void Adding_a_transition_shortens_the_timeline_and_reshapes_the_boundary_section()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence(8, 8);   // 16 s: 5+5+5+1
        manager.Update(sequence, Settings);
        var before = Hashes(manager);
        Assert.Equal(4, before.Length);

        sequence.Video.Clips[1].TransitionIn = new Transition(TransitionKind.Dissolve, S(1));
        manager.Update(sequence, Settings);
        var after = Hashes(manager);

        // 16 - 1 de solape = 15 s exactos: ya no sobra un trozo de 1 s.
        Assert.Equal(3, after.Length);

        // La primera sección vive por completo dentro del primer clip: no le afecta que el
        // segundo se adelante más adelante en la timeline.
        Assert.Equal(before[0], after[0]);

        // El límite se adelantó del segundo 8 al 7: la sección 5-10 ahora funde en vez de
        // cortar en seco.
        Assert.NotEqual(before[1], after[1]);
    }

    [Fact]
    public void Two_different_videos_on_a_layer_at_the_same_time_hash_differently()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        string[] HashWith(string name)
        {
            var sequence = Sequence();
            var media = new MediaInfo($"C:/no-existe/{name}.mp4", S(20), 1920, 1080, 30, "h264", true);
            sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateVideo(media, TimeSpan.Zero, S(1), S(3)));
            manager.Update(sequence, Settings);
            return Hashes(manager);
        }

        Assert.NotEqual(HashWith("uno")[0], HashWith("dos")[0]);
    }

    [Fact]
    public void Hidden_layers_do_not_change_the_hash_because_they_are_not_drawn()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        var sequence = Sequence();
        manager.Update(sequence, Settings);
        var before = Hashes(manager);

        var track = sequence.AddOverlayTrack();
        track.TryAdd(OverlayItem.CreateText(new TextStyle("Hola"), S(6), S(2)));
        track.IsHidden = true;
        manager.Update(sequence, Settings);

        Assert.Equal(before, Hashes(manager));
    }

    [Fact]
    public void An_empty_sequence_or_no_settings_leaves_no_sections()
    {
        using var directory = new Workspace();
        using var manager = NewManager(directory.Path);

        manager.Update(new EditSequence(), Settings);
        Assert.Empty(manager.Sections);

        manager.Update(Sequence(), null);
        Assert.Empty(manager.Sections);
    }

    [Fact]
    public void The_preview_settings_use_whole_frame_rates_and_even_sizes()
    {
        var ntsc = PreviewCacheSettings.For(541, 29.97);
        Assert.Equal(30, ntsc.FrameRate);
        Assert.Equal(540, ntsc.Height);
        Assert.Equal(960, ntsc.Width);

        Assert.Equal(60, PreviewCacheSettings.For(1080, 120).FrameRate);
        Assert.Equal(24, PreviewCacheSettings.For(1080, 12).FrameRate);
    }
}

[Trait("Category", "Integration")]
public class PreviewCacheIntegrationTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private readonly ITestOutputHelper _output;

    public PreviewCacheIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<MediaInfo> MakeVideoAsync(FFmpegTools tools, string directory, int seconds, string source = "testsrc2")
    {
        var path = Path.Combine(directory, $"fuente-{source}.mp4");
        var input = source == "black" ? $"color=c=black:size=1280x720:rate=30:duration={seconds}" : $"testsrc2=size=1280x720:rate=30:duration={seconds}";
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", input, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return await new FFprobeService(tools).ProbeAsync(path, CancellationToken.None);
    }

    [Fact]
    public async Task Rendering_produces_playable_sections_and_an_edit_only_redoes_the_affected_one()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        using var workspace = new Workspace();
        var cache = Path.Combine(workspace.Path, "cache");
        var media = await MakeVideoAsync(tools, workspace.Path, 12);
        var originalStamp = (new FileInfo(media.Path).Length, new FileInfo(media.Path).LastWriteTimeUtc);

        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(media));

        using var manager = new PreviewCacheManager(tools, cache);
        var settings = PreviewCacheSettings.For(360, 30);
        manager.Update(sequence, settings);
        Assert.Equal(3, manager.Sections.Count);

        manager.StartRender(TimeSpan.Zero);
        await manager.WaitForRenderAsync();
        Assert.Null(manager.LastError);

        Assert.All(manager.Sections, s => Assert.Equal(SectionState.Ready, s.State));
        Assert.All(manager.Sections, s => Assert.True(File.Exists(manager.PathFor(s.Hash))));

        // Se reproduce como un solo video continuo desde cualquier punto.
        var run = manager.FindRun(S(3));
        Assert.NotNull(run);
        Assert.Equal(TimeSpan.Zero, run.Start);   // el trozo que contiene el 3 empieza en 0
        Assert.Equal(S(12), run.End);
        Assert.EndsWith(FrameReader.ConcatListExtension, run.Path, StringComparison.Ordinal);

        using var player = new VideoPlayer(tools, run.Settings.Width, run.Settings.Height, run.Settings.FrameRate);
        player.Configure(run.Settings.Width, run.Settings.Height, run.Settings.FrameRate, hardwareDecoding: false);
        var frames = 0;
        var width = 0;
        player.FrameReady = f => { Interlocked.Increment(ref frames); width = f.Width; };
        await player.OpenAsync(run.Path, S(3));
        player.Play();
        await Task.Delay(1500);
        player.Pause();
        Assert.Equal(run.Settings.Width, width);
        Assert.True(frames > 20, $"solo llegaron {frames} fotogramas del preview renderizado");

        // Editar solo afecta a la sección donde cae el cambio.
        var hashesBefore = manager.Sections.Select(s => s.Hash).ToArray();
        var writeTimes = hashesBefore.Select(h => File.GetLastWriteTimeUtc(manager.PathFor(h))).ToArray();

        sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateText(new TextStyle("Título"), S(6), S(2)));
        manager.Update(sequence, settings);

        Assert.Equal(
            [SectionState.Ready, SectionState.NeedsRender, SectionState.Ready],
            manager.Sections.Select(s => s.State).ToArray());
        Assert.Equal(1, manager.Summary.Pending);
        Assert.Null(manager.FindRun(S(6)));     // sin copia: se reproduce desde el original

        manager.StartRender(S(6));
        await manager.WaitForRenderAsync();

        Assert.All(manager.Sections, s => Assert.Equal(SectionState.Ready, s.State));
        Assert.Equal(writeTimes[0], File.GetLastWriteTimeUtc(manager.PathFor(hashesBefore[0])));
        Assert.Equal(writeTimes[2], File.GetLastWriteTimeUtc(manager.PathFor(hashesBefore[2])));

        // El original ni se tocó.
        Assert.Equal(originalStamp, (new FileInfo(media.Path).Length, new FileInfo(media.Path).LastWriteTimeUtc));
    }

    [Fact]
    public async Task Baked_text_is_visible_in_the_rendered_preview()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        using var workspace = new Workspace();
        var media = await MakeVideoAsync(tools, workspace.Path, 6, "black");

        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(media));
        sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateText(
            new TextStyle("MMMMMM", 0.4, "#FFFFFF", Bold: true, Italic: false, Shadow: false), S(0), S(6),
            new OverlayTransform(0.5, 0.5)));

        using var manager = new PreviewCacheManager(tools, Path.Combine(workspace.Path, "cache"));
        var settings = PreviewCacheSettings.For(360, 30);
        manager.Update(sequence, settings);
        manager.StartRender(TimeSpan.Zero);
        await manager.WaitForRenderAsync();
        Assert.Null(manager.LastError);

        var run = manager.FindRun(S(1));
        Assert.NotNull(run);

        using var player = new VideoPlayer(tools, run.Settings.Width, run.Settings.Height, run.Settings.FrameRate);
        player.Configure(run.Settings.Width, run.Settings.Height, run.Settings.FrameRate, hardwareDecoding: false);

        byte[]? pixels = null;
        player.FrameReady = f => { pixels ??= f.Pixels.ToArray(); };
        await player.OpenAsync(run.Path, S(1));
        await Task.Delay(1000);

        Assert.NotNull(pixels);
        var bright = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200)
            {
                bright++;
            }
        }

        _output.WriteLine($"{bright} píxeles blancos");
        Assert.True(bright > 500, "el texto debe estar dibujado en la copia de preview");
    }

    [Fact]
    public async Task The_disk_limit_removes_the_least_useful_sections()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        using var workspace = new Workspace();
        var media = await MakeVideoAsync(tools, workspace.Path, 12);
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(media));

        var cache = Path.Combine(workspace.Path, "cache");
        using var manager = new PreviewCacheManager(tools, cache, maxBytes: 1);
        manager.Update(sequence, PreviewCacheSettings.For(360, 30));
        manager.StartRender(TimeSpan.Zero);
        await manager.WaitForRenderAsync();

        Assert.Empty(Directory.GetFiles(cache, "*.mp4"));
        Assert.Equal(0, manager.Summary.Ready);
    }

    [Fact]
    public async Task Clearing_the_cache_removes_the_copies_and_the_originals_survive()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        using var workspace = new Workspace();
        var media = await MakeVideoAsync(tools, workspace.Path, 6);
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(media));

        var cache = Path.Combine(workspace.Path, "cache");
        using var manager = new PreviewCacheManager(tools, cache);
        manager.Update(sequence, PreviewCacheSettings.For(360, 30));
        manager.StartRender(TimeSpan.Zero);
        await manager.WaitForRenderAsync();
        Assert.Equal(2, manager.Summary.Ready);
        Assert.True(manager.Summary.Bytes > 0);

        manager.Clear();

        Assert.Empty(Directory.GetFiles(cache));
        Assert.All(manager.Sections, s => Assert.Equal(SectionState.NeedsRender, s.State));
        Assert.True(File.Exists(media.Path));
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_keeps_what_was_already_rendered()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        using var workspace = new Workspace();
        var media = await MakeVideoAsync(tools, workspace.Path, 30);
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(media));

        var cache = Path.Combine(workspace.Path, "cache");
        using var manager = new PreviewCacheManager(tools, cache);
        manager.Update(sequence, PreviewCacheSettings.For(360, 30));

        var firstDone = new TaskCompletionSource();
        manager.Changed += (_, _) =>
        {
            if (manager.Sections.Any(s => s.State == SectionState.Ready))
            {
                firstDone.TrySetResult();
            }
        };

        manager.StartRender(TimeSpan.Zero);
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(60));
        manager.CancelRender();
        await manager.WaitForRenderAsync();

        var states = manager.Sections.Select(s => s.State).ToArray();
        Assert.Contains(SectionState.Ready, states);
        Assert.DoesNotContain(SectionState.Rendering, states);
        Assert.DoesNotContain(SectionState.Queued, states);
        Assert.Empty(Directory.GetFiles(cache, "*.part.mp4"));
    }
}
