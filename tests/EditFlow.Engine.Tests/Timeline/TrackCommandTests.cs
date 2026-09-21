// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class TrackCommandTests
{
    private static AudioClip Audio(double start, double seconds) =>
        new(new MediaInfo("m.mp3", TimeSpan.FromSeconds(120), 0, 0, 0, "none", true),
            TimeSpan.Zero, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(start));

    [Fact]
    public void Adding_a_track_can_be_undone_and_redone_as_the_same_track()
    {
        // Al rehacer debe volver la MISMA pista: los clips añadidos después apuntan a ella.
        var sequence = new EditSequence();
        var history = new UndoHistory();
        var command = new AddAudioTrackCommand(sequence);

        history.Do(command);
        var track = command.Result;
        Assert.Single(sequence.AudioTracks);

        history.Undo();
        Assert.Empty(sequence.AudioTracks);

        history.Redo();
        Assert.Same(track, Assert.Single(sequence.AudioTracks));
    }

    [Fact]
    public void Removing_a_track_brings_back_its_clips_on_undo()
    {
        var sequence = new EditSequence();
        var track = sequence.AddAudioTrack("Música");
        track.TryAdd(Audio(0, 5));
        track.TryAdd(Audio(10, 5));
        var history = new UndoHistory();

        history.Do(new RemoveAudioTrackCommand(sequence, track));
        Assert.Empty(sequence.AudioTracks);

        history.Undo();

        Assert.Same(track, Assert.Single(sequence.AudioTracks));
        Assert.Equal(2, track.Clips.Count);
    }

    [Fact]
    public void A_removed_track_returns_to_its_original_position()
    {
        var sequence = new EditSequence();
        sequence.AddAudioTrack("A");
        var middle = sequence.AddAudioTrack("B");
        sequence.AddAudioTrack("C");
        var history = new UndoHistory();

        history.Do(new RemoveAudioTrackCommand(sequence, middle));
        history.Undo();

        Assert.Equal(["A", "B", "C"], sequence.AudioTracks.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void Adding_an_audio_clip_can_be_undone()
    {
        var track = new AudioTrack("A1");
        var clip = Audio(0, 4);
        var history = new UndoHistory();

        history.Do(new AddAudioClipCommand(track, clip));
        Assert.Single(track.Clips);

        history.Undo();
        Assert.Empty(track.Clips);
    }

    [Fact]
    public void A_rejected_add_leaves_nothing_to_undo()
    {
        // Si el clip chocó y no se añadió, deshacer no debe quitar otro clip que sí estaba.
        var track = new AudioTrack("A1");
        var existing = Audio(0, 10);
        track.TryAdd(existing);

        var command = new AddAudioClipCommand(track, Audio(5, 4));
        command.Execute();
        Assert.False(command.Added);

        command.Undo();

        Assert.Single(track.Clips);
        Assert.Same(existing, track.Clips[0]);
    }

    [Fact]
    public void Removing_an_audio_clip_can_be_undone()
    {
        var track = new AudioTrack("A1");
        var clip = Audio(3, 4);
        track.TryAdd(clip);
        var history = new UndoHistory();

        history.Do(new RemoveAudioClipCommand(track, clip));
        Assert.Empty(track.Clips);

        history.Undo();
        Assert.Same(clip, Assert.Single(track.Clips));
        Assert.Equal(TimeSpan.FromSeconds(3), clip.TimelineStart);
    }

    [Fact]
    public void Muting_a_clip_can_be_undone()
    {
        var clip = Audio(0, 3);
        var history = new UndoHistory();

        history.Do(new SetAudioMutedCommand(clip, muted: true));
        Assert.True(clip.IsMuted);

        history.Undo();
        Assert.False(clip.IsMuted);
    }

    [Fact]
    public void Changing_fades_can_be_undone()
    {
        var clip = Audio(0, 10);
        clip.FadeIn = TimeSpan.FromSeconds(1);
        clip.FadeOut = TimeSpan.FromSeconds(2);
        var history = new UndoHistory();

        history.Do(new SetAudioFadeCommand(clip, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(4), clip.FadeIn);
        Assert.Equal(TimeSpan.FromSeconds(5), clip.FadeOut);

        history.Undo();
        Assert.Equal(TimeSpan.FromSeconds(1), clip.FadeIn);
        Assert.Equal(TimeSpan.FromSeconds(2), clip.FadeOut);
    }

    [Fact]
    public void Both_fades_are_applied_even_when_the_new_ones_replace_larger_old_ones()
    {
        // Cada fundido se acota contra el otro. Con los antiguos aún puestos, fijar el
        // nuevo de entrada lo recortaría por un fundido de salida que ya no debería contar.
        var clip = Audio(0, 10);
        clip.FadeIn = TimeSpan.FromSeconds(2);
        clip.FadeOut = TimeSpan.FromSeconds(8);

        new SetAudioFadeCommand(clip, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(3)).Execute();

        Assert.Equal(TimeSpan.FromSeconds(7), clip.FadeIn);
        Assert.Equal(TimeSpan.FromSeconds(3), clip.FadeOut);
    }
}
