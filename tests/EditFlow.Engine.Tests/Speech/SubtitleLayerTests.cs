// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Speech;

public class SubtitleLayerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    private static EditSequence Sequence()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo("C:/no-existe/a.mp4", S(20), 1920, 1080, 30, "h264", true)));
        return sequence;
    }

    [Fact]
    public void New_layers_are_created_below_the_subtitle_layer()
    {
        var sequence = Sequence();
        new AddSubtitlesCommand(sequence, [new SubtitleCue(S(1), S(3), "hola")]).Execute();

        var first = sequence.AddOverlayTrack();
        var second = sequence.AddOverlayTrack();

        Assert.True(sequence.OverlayTracks[0].IsSubtitles);
        Assert.Same(second, sequence.OverlayTracks[1]);
        Assert.Same(first, sequence.OverlayTracks[2]);
    }

    [Fact]
    public void Reinserting_a_layer_at_the_front_never_covers_the_subtitles()
    {
        var sequence = Sequence();
        new AddSubtitlesCommand(sequence, [new SubtitleCue(S(1), S(3), "hola")]).Execute();
        var other = new OverlayTrack("T1");

        sequence.InsertOverlayTrack(0, other);

        Assert.True(sequence.OverlayTracks[0].IsSubtitles);
        Assert.Same(other, sequence.OverlayTracks[1]);
    }

    [Fact]
    public void Other_content_is_never_placed_in_the_subtitle_layer()
    {
        var sequence = Sequence();
        new AddSubtitlesCommand(sequence, [new SubtitleCue(S(1), S(3), "hola")]).Execute();

        var track = sequence.FindOrCreateOverlayTrackFor(S(10), S(2));   // hay hueco en Sub, pero no es para esto

        Assert.False(track.IsSubtitles);
        Assert.Equal(2, sequence.OverlayTracks.Count);
    }

    [Fact]
    public void A_clip_lifted_over_the_subtitle_layer_goes_to_a_normal_layer()
    {
        var sequence = Sequence();
        new AddSubtitlesCommand(sequence, [new SubtitleCue(S(1), S(3), "hola")]).Execute();
        var sub = sequence.SubtitleLayer!;

        new LiftClipToLayerCommand(sequence, sequence.Video.Clips[0], sub).Execute();

        Assert.Single(sub.Items);                                          // Sub sigue teniendo solo el subtítulo
        Assert.Equal(2, sequence.OverlayTracks.Count);
        Assert.Equal(OverlayKind.Video, sequence.OverlayTracks[1].Items[0].Kind);
    }

    [Fact]
    public void A_clip_can_be_lifted_to_the_chosen_layer_when_it_fits()
    {
        var sequence = Sequence();
        var chosen = sequence.AddOverlayTrack("Mi capa");
        sequence.AddOverlayTrack("Otra");

        new LiftClipToLayerCommand(sequence, sequence.Video.Clips[0], chosen).Execute();

        Assert.Single(chosen.Items);
        Assert.Equal(2, sequence.OverlayTracks.Count);                     // no se creó una capa más
    }

    [Fact]
    public void Adding_subtitles_twice_keeps_the_first_ones_and_undo_removes_only_the_new()
    {
        var sequence = Sequence();
        var history = new UndoHistory();
        history.Do(new AddSubtitlesCommand(sequence, [new SubtitleCue(S(1), S(3), "uno")]));
        var command = new AddSubtitlesCommand(sequence, [new SubtitleCue(S(2), S(4), "choca"), new SubtitleCue(S(5), S(6), "dos")]);
        history.Do(command);

        var layer = Assert.Single(sequence.OverlayTracks);
        Assert.Equal(["uno", "dos"], layer.Items.Select(i => i.Text!.Content).ToArray());

        history.Undo();
        Assert.Equal(["uno"], layer.Items.Select(i => i.Text!.Content).ToArray());
        Assert.Single(sequence.OverlayTracks);
    }

    [Fact]
    public async Task The_subtitle_layer_survives_saving_and_stays_in_front()
    {
        var directory = Directory.CreateTempSubdirectory("editflow-sublayer-");
        try
        {
            var media = Path.Combine(directory.FullName, "a.mp4");
            File.WriteAllText(media, "x");

            var project = new EditProject();
            project.Timeline.Append(new Clip(project.AddMedia(new MediaInfo(media, S(20), 1920, 1080, 30, "h264", true))));
            project.Sequence.AddOverlayTrack("T1").TryAdd(OverlayItem.CreateText(new TextStyle("titulo"), S(1), S(2)));
            new AddSubtitlesCommand(project.Sequence, [new SubtitleCue(S(4), S(6), "sub")]).Execute();
            project.Sequence.AddOverlayTrack("T2");

            var path = Path.Combine(directory.FullName, "p.editflow");
            await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
            var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

            Assert.True(loaded.Sequence.OverlayTracks[0].IsSubtitles);
            Assert.Equal("Sub", loaded.Sequence.OverlayTracks[0].Name);
            Assert.Equal(1, loaded.Sequence.OverlayTracks.Count(t => t.IsSubtitles));
            Assert.Equal(3, loaded.Sequence.OverlayTracks.Count);
        }
        finally
        {
            directory.DeleteWithRetry();
        }
    }
}
