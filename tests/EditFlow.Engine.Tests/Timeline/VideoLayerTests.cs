// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Playback;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Timeline;

public class VideoLayerModelTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-layers-");

    public void Dispose()
    {
        try { _workspace.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private MediaInfo Media(string name, double seconds = 10, bool audio = true, int width = 1920, int height = 1080)
    {
        var path = Path.Combine(_workspace.FullName, name);
        File.WriteAllText(path, "no es un video");
        return new MediaInfo(path, S(seconds), width, height, 30, "h264", audio);
    }

    private (EditSequence Sequence, Clip First, Clip Second) SplitSequence()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 10)));
        var second = sequence.Video.SplitAt(S(4))!;
        return (sequence, sequence.Video.Clips[0], second);
    }

    // ------------------------------------------------------------------- subir

    [Fact]
    public void Lifting_a_clip_leaves_a_gap_and_keeps_everything_in_place()
    {
        var (sequence, first, second) = SplitSequence();

        var command = new LiftClipToLayerCommand(sequence, second);
        command.Execute();

        Assert.Equal(S(10), sequence.Video.Duration);                 // nada se corre
        Assert.Equal(2, sequence.Video.Clips.Count);
        Assert.Same(first, sequence.Video.Clips[0]);
        Assert.True(sequence.Video.Clips[1].IsGap);
        Assert.Equal(S(6), sequence.Video.Clips[1].Duration);

        var item = Assert.Single(Assert.Single(sequence.OverlayTracks).Items);
        Assert.Equal(OverlayKind.Video, item.Kind);
        Assert.Equal(S(4), item.Start);                               // donde estaba el clip
        Assert.Equal(S(6), item.Duration);
        Assert.Equal(second.SourceIn, item.SourceIn);
        Assert.Same(second.Source, item.Media);
        Assert.True(item.PlaysAudio);
    }

    [Fact]
    public void A_lifted_clip_keeps_filling_the_frame_at_first()
    {
        var (sequence, _, second) = SplitSequence();
        new LiftClipToLayerCommand(sequence, second).Execute();

        var transform = sequence.OverlayTracks[0].Items[0].Transform;
        Assert.Equal(1.0, transform.Width);
        Assert.Equal(0.5, transform.CenterX);
        Assert.Equal(0.5, transform.CenterY);
        Assert.Equal(1.0, transform.Opacity);
    }

    [Fact]
    public void A_narrow_vertical_video_only_takes_the_width_it_needs()
    {
        var transform = OverlayItem.FullFrame(Media("v.mp4", 5, width: 1080, height: 1920));

        Assert.InRange(transform.Width, 0.3, 0.33);   // 9:16 dentro de 16:9
    }

    [Fact]
    public void Lifting_can_be_undone_and_redone()
    {
        var (sequence, _, second) = SplitSequence();
        var history = new UndoHistory();
        history.Do(new LiftClipToLayerCommand(sequence, second));

        history.Undo();
        Assert.Same(second, sequence.Video.Clips[1]);
        Assert.Empty(sequence.OverlayTracks);                          // la capa que se creó también se retira
        Assert.DoesNotContain(sequence.Video.Clips, c => c.IsGap);

        history.Redo();
        Assert.True(sequence.Video.Clips[1].IsGap);
        Assert.Single(Assert.Single(sequence.OverlayTracks).Items);
    }

    [Fact]
    public void A_gap_cannot_be_lifted()
    {
        var (sequence, _, second) = SplitSequence();
        new LiftClipToLayerCommand(sequence, second).Execute();

        Assert.False(LiftClipToLayerCommand.CanLift(sequence.Video.Clips[1]));
        Assert.True(LiftClipToLayerCommand.CanLift(sequence.Video.Clips[0]));
    }

    [Fact]
    public void A_lifted_clip_without_its_own_audio_stays_silent()
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media("a.mp4", 10)) { IsAudioMuted = true };
        sequence.Video.Append(clip);

        new LiftClipToLayerCommand(sequence, clip).Execute();

        Assert.False(sequence.OverlayTracks[0].Items[0].PlaysAudio);
    }

    // --------------------------------------------------------- recortar y mover

    private static OverlayItem LiftedItem(EditSequence sequence, Clip clip)
    {
        new LiftClipToLayerCommand(sequence, clip).Execute();
        return sequence.OverlayTracks[0].Items[0];
    }

    [Fact]
    public void Trimming_the_left_edge_advances_the_point_where_the_video_starts()
    {
        var (sequence, _, second) = SplitSequence();
        var item = LiftedItem(sequence, second);
        var track = sequence.OverlayTracks[0];
        var before = item.SourceIn;

        Assert.True(track.TryPlace(item, item.Start + S(1), item.Duration - S(1)));

        Assert.Equal(before + S(1), item.SourceIn);
        Assert.Equal(S(5), item.Start);
    }

    [Fact]
    public void Moving_does_not_change_which_part_of_the_video_is_shown()
    {
        var (sequence, _, second) = SplitSequence();
        var item = LiftedItem(sequence, second);
        var before = item.SourceIn;

        Assert.True(sequence.OverlayTracks[0].TryMove(item, S(2)));

        Assert.Equal(before, item.SourceIn);
    }

    [Fact]
    public void The_video_cannot_be_stretched_past_its_material()
    {
        var (sequence, _, second) = SplitSequence();
        var item = LiftedItem(sequence, second);
        var track = sequence.OverlayTracks[0];

        // Le quedan 6 s de archivo: pedir 7 no cabe.
        Assert.False(track.TryPlace(item, item.Start, S(7)));
        // Y no se puede retroceder el inicio más allá del principio del archivo.
        Assert.False(track.TryPlace(item, item.Start - S(5), item.Duration + S(5)));
    }

    [Fact]
    public void Undoing_a_left_trim_restores_the_start_point()
    {
        var (sequence, _, second) = SplitSequence();
        var item = LiftedItem(sequence, second);
        var track = sequence.OverlayTracks[0];
        var start = item.Start;
        var duration = item.Duration;
        var sourceIn = item.SourceIn;

        var history = new UndoHistory();
        history.Do(new PlaceOverlayItemCommand(track, item, start + S(2), duration - S(2)));
        Assert.Equal(sourceIn + S(2), item.SourceIn);

        history.Undo();

        Assert.Equal(sourceIn, item.SourceIn);
        Assert.Equal(start, item.Start);
        Assert.Equal(duration, item.Duration);
    }

    // ------------------------------------------------------------------ cortar

    [Fact]
    public void Slicing_a_video_overlay_shifts_its_start_point()
    {
        var (sequence, _, second) = SplitSequence();
        LiftedItem(sequence, second);                                   // de 4 a 10 s, desde el segundo 4 del archivo

        var slice = SequenceSlicer.Slice(sequence, S(5), S(8));
        var piece = slice.OverlayTracks.Single().Items.Single();

        Assert.Equal(OverlayKind.Video, piece.Kind);
        Assert.Equal(TimeSpan.Zero, piece.Start);
        Assert.Equal(S(3), piece.Duration);
        Assert.Equal(S(5), piece.SourceIn);                             // 4 + (5 - 4)
        Assert.True(slice.Video.Clips[0].IsGap);
    }

    // ---------------------------------------------------------------- guardado

    [Fact]
    public async Task Gaps_and_video_layers_survive_saving_and_reopening()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media("a.mp4", 10));
        project.Timeline.Append(new Clip(media));
        var second = project.Timeline.SplitAt(S(4))!;
        new LiftClipToLayerCommand(project.Sequence, second).Execute();

        var path = Path.Combine(_workspace.FullName, "p.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

        Assert.Equal(2, loaded.Timeline.Clips.Count);
        Assert.True(loaded.Timeline.Clips[1].IsGap);
        Assert.Equal(S(10), loaded.Timeline.Duration);

        var item = Assert.Single(Assert.Single(loaded.Sequence.OverlayTracks).Items);
        Assert.Equal(OverlayKind.Video, item.Kind);
        Assert.Equal(S(4), item.Start);
        Assert.Equal(S(4), item.SourceIn);
        Assert.Equal(S(6), item.Duration);
        Assert.True(item.PlaysAudio);
        Assert.Equal(media.Path, item.Media!.Path);
    }

    [Fact]
    public async Task A_video_layer_whose_file_is_gone_stays_put_so_it_can_be_relinked()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media("a.mp4", 10));
        var extra = project.AddMedia(Media("b.mp4", 10));
        project.Timeline.Append(new Clip(media));
        project.Sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateVideo(extra, TimeSpan.Zero, S(1), S(3)));

        var path = Path.Combine(_workspace.FullName, "p.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        File.Delete(extra.Path);

        var loaded = await ProjectSerializer.LoadAsync(path, CancellationToken.None);

        Assert.True(loaded.HasMissingMedia);

        var item = Assert.Single(loaded.Project.Sequence.OverlayTracks.Single().Items);
        Assert.True(item.Media!.IsOffline);
        Assert.Equal(S(1), item.Start);
    }

    // ------------------------------------------------------------------- grafo

    private static ExportSettings Settings() => new()
    {
        OutputPath = "salida.mp4",
        Resolution = VideoResolution.P720,
        EncoderName = "libx264",
    };

    [Fact]
    public void A_gap_becomes_generated_black_video_and_the_graph_still_closes()
    {
        var (sequence, _, second) = SplitSequence();
        new LiftClipToLayerCommand(sequence, second).Execute();

        var plan = FilterGraphBuilder.Build(sequence, Settings());
        var arguments = string.Join(' ', plan.InputArguments);

        Assert.Contains("color=c=black:s=1280x720", arguments, StringComparison.Ordinal);
        Assert.Contains("overlay=", plan.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("[vout]", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_video_overlay_reads_its_own_file_from_the_right_point_and_is_scaled_before_the_conversion()
    {
        var (sequence, _, second) = SplitSequence();
        var item = LiftedItem(sequence, second);
        sequence.OverlayTracks[0].TryPlace(item, item.Start, item.Duration);

        var plan = FilterGraphBuilder.Build(sequence, Settings());

        // El archivo del overlay se abre con su propio punto de entrada (4 s) y duración (6 s).
        var inputs = plan.InputArguments.ToList();
        var overlayInput = inputs.FindLastIndex(a => a == second.Source.Path);
        Assert.Equal("-i", inputs[overlayInput - 1]);
        Assert.Equal("6", inputs[overlayInput - 2]);
        Assert.Equal("4", inputs[overlayInput - 4]);

        Assert.Matches(@"fps=30,scale=1280:-2,format=rgba", plan.FilterGraph);
        Assert.Contains("setpts=PTS-STARTPTS+4/TB", plan.FilterGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sound_of_a_lifted_video_is_mixed_in_and_a_muted_one_is_not()
    {
        var (sequence, _, second) = SplitSequence();
        var item = LiftedItem(sequence, second);

        var loud = FilterGraphBuilder.BuildAudioOnly(sequence);
        Assert.Contains("adelay=delays=4000", loud.FilterGraph, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2", loud.FilterGraph, StringComparison.Ordinal);

        var mutedSequence = new EditSequence();
        var clip = new Clip(Media("m.mp4", 10)) { IsAudioMuted = true };
        mutedSequence.Video.Append(clip);
        LiftedItem(mutedSequence, clip);

        Assert.DoesNotContain("amix", FilterGraphBuilder.BuildAudioOnly(mutedSequence).FilterGraph, StringComparison.Ordinal);
        Assert.NotNull(item);
    }

    [Fact]
    public void A_hidden_layer_is_neither_seen_nor_heard()
    {
        var (sequence, _, second) = SplitSequence();
        LiftedItem(sequence, second);
        sequence.OverlayTracks[0].IsHidden = true;

        Assert.DoesNotContain("amix", FilterGraphBuilder.BuildAudioOnly(sequence).FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("overlay=", FilterGraphBuilder.Build(sequence, Settings()).FilterGraph, StringComparison.Ordinal);
    }
}

[Trait("Category", "Integration")]
public class VideoLayerExportTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private readonly ITestOutputHelper _output;

    public VideoLayerExportTests(ITestOutputHelper output) => _output = output;

    private static async Task<MediaInfo> MakeAsync(FFmpegTools tools, string directory, string name, string lavfi, int seconds)
    {
        var path = Path.Combine(directory, name + ".mp4");
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"{lavfi}:duration={seconds}",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
            ],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return await new FFprobeService(tools).ProbeAsync(path, CancellationToken.None);
    }

    /// <summary>Brillo medio (0 a 255) del fotograma de un video en un instante, en el cuadro central y en una esquina.</summary>
    private static async Task<(double Center, double Corner)> BrightnessAtAsync(FFmpegTools tools, string path, double seconds)
    {
        using var player = new VideoPlayer(tools, 320, 180, 30);
        player.Configure(320, 180, 30, hardwareDecoding: false);

        byte[]? pixels = null;
        player.FrameReady = f => { pixels ??= f.Pixels.ToArray(); };
        await player.OpenAsync(path, S(seconds));
        for (var i = 0; i < 40 && pixels is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(pixels);

        double Mean(int x0, int y0, int x1, int y1)
        {
            double total = 0;
            var count = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var i = ((y * 320) + x) * 4;
                    total += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                    count++;
                }
            }

            return total / count;
        }

        return (Mean(140, 80, 180, 100), Mean(0, 0, 20, 12));
    }

    [Fact]
    public async Task A_lifted_clip_is_composited_over_a_black_gap_and_keeps_its_sound()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-layerexport-");

        try
        {
            var white = await MakeAsync(tools, workspace.FullName, "blanco", "color=c=white:size=640x360:rate=30", 6);

            // Un video blanco de 6 s, dividido a los 2 s; el trozo de 4 s sube a una capa y se reduce a la mitad.
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(white));
            var second = sequence.Video.SplitAt(S(2))!;
            new LiftClipToLayerCommand(sequence, second).Execute();

            var track = sequence.OverlayTracks[0];
            var item = track.Items[0];
            new SetOverlayLookCommand(item, new OverlayTransform(0.5, 0.5, 0.5, 1)).Execute();

            var output = Path.Combine(workspace.FullName, "salida.mp4");
            var result = await new ExportJob(tools).RunAsync(
                sequence,
                new ExportSettings
                {
                    OutputPath = output,
                    Resolution = new VideoResolution(320, 180, "prueba"),
                    EncoderName = "libx264",
                    Speed = EncodingSpeed.Fastest,
                },
                null,
                CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);

            var probed = await new FFprobeService(tools).ProbeAsync(output, CancellationToken.None);
            Assert.InRange(probed.Duration.TotalSeconds, 5.8, 6.3);
            Assert.True(probed.HasAudio);

            // 1 s: la parte que se quedó en la pista principal, blanca a pantalla completa.
            var first = await BrightnessAtAsync(tools, output, 1);
            Assert.True(first.Center > 200 && first.Corner > 200, $"centro {first.Center}, esquina {first.Corner}");

            // 4 s: el hueco es negro y el trozo subido, reducido a la mitad, queda blanco en el centro.
            var lifted = await BrightnessAtAsync(tools, output, 4);
            _output.WriteLine($"centro {lifted.Center:0}, esquina {lifted.Corner:0}");
            Assert.True(lifted.Center > 200, "el video superpuesto debe verse en el centro");
            Assert.True(lifted.Corner < 40, "el hueco de la pista principal debe verse negro");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}
