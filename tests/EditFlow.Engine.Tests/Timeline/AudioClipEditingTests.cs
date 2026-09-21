// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class AudioClipEditingTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Song(double seconds = 60) =>
        new("cancion.mp3", S(seconds), 0, 0, 0, string.Empty, true);

    /// <summary>Clip de 10 s tomado del segundo 20 del archivo, colocado en el segundo 5.</summary>
    private static (AudioTrack Track, AudioClip Clip) Setup()
    {
        var track = new AudioTrack("A1");
        var clip = new AudioClip(Song(), S(20), S(30), S(5));
        Assert.True(track.TryAdd(clip));
        return (track, clip);
    }

    // ------------------------------------------------------------------ recortar

    [Fact]
    public void Trimming_the_start_keeps_the_remaining_audio_playing_at_the_same_instant()
    {
        var (track, clip) = Setup();

        Assert.True(track.TryTrim(clip, ClipEdge.Start, S(8)));

        // El audio que se conserva sonaba del segundo 23 al 30 del archivo, del 8 al 15 de la
        // timeline: recortar no debe desplazar lo que queda.
        Assert.Equal(S(23), clip.SourceIn);
        Assert.Equal(S(30), clip.SourceOut);
        Assert.Equal(S(8), clip.TimelineStart);
        Assert.Equal(S(15), clip.TimelineEnd);
    }

    [Fact]
    public void Trimming_the_end_shortens_the_clip_without_moving_it()
    {
        var (track, clip) = Setup();

        Assert.True(track.TryTrim(clip, ClipEdge.End, S(12)));

        Assert.Equal(S(5), clip.TimelineStart);
        Assert.Equal(S(27), clip.SourceOut);
        Assert.Equal(S(12), clip.TimelineEnd);
    }

    [Fact]
    public void A_clip_can_be_extended_up_to_the_material_the_file_has()
    {
        var (track, clip) = Setup();

        Assert.True(track.TryTrim(clip, ClipEdge.End, S(45)));   // pide hasta el segundo 60 del archivo
        Assert.Equal(S(60), clip.SourceOut);

        Assert.False(track.TryTrim(clip, ClipEdge.End, S(46)));  // más allá no hay audio
        Assert.Equal(S(60), clip.SourceOut);
    }

    [Fact]
    public void The_start_cannot_be_extended_past_the_beginning_of_the_file()
    {
        // Clip que empieza en el segundo 2 del archivo, colocado en el 30 de la timeline:
        // hay sitio de sobra a su izquierda, pero solo dos segundos de audio antes de él.
        var track = new AudioTrack("A1");
        var clip = new AudioClip(Song(), S(2), S(10), S(30));
        track.TryAdd(clip);

        Assert.False(track.TryTrim(clip, ClipEdge.Start, S(27)));   // pediría el segundo -1
        Assert.True(track.TryTrim(clip, ClipEdge.Start, S(28)));    // el segundo 0 sí existe
        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
    }

    [Fact]
    public void Extending_into_a_neighbour_is_rejected_and_changes_nothing()
    {
        var (track, clip) = Setup();
        Assert.True(track.TryAdd(new AudioClip(Song(), S(0), S(5), S(16))));

        Assert.False(track.TryTrim(clip, ClipEdge.End, S(17)));

        Assert.Equal(S(30), clip.SourceOut);
        Assert.Equal(S(15), clip.TimelineEnd);
    }

    [Fact]
    public void A_clip_cannot_be_trimmed_below_the_minimum_duration()
    {
        var (track, clip) = Setup();

        Assert.False(track.TryTrim(clip, ClipEdge.End, S(5.01)));
        Assert.False(track.TryTrim(clip, ClipEdge.Start, S(15)));
    }

    [Fact]
    public void A_locked_track_refuses_trimming()
    {
        var (track, clip) = Setup();
        track.IsLocked = true;

        Assert.False(track.TryTrim(clip, ClipEdge.End, S(8)));
        Assert.Equal(S(15), clip.TimelineEnd);
    }

    [Fact]
    public void Trimming_shortens_fades_that_no_longer_fit()
    {
        var (track, clip) = Setup();
        clip.FadeIn = S(4);
        clip.FadeOut = S(5);

        Assert.True(track.TryTrim(clip, ClipEdge.End, S(8)));  // dura 3 s

        Assert.True(clip.FadeIn + clip.FadeOut <= clip.Duration);
    }

    [Fact]
    public void Undoing_a_trim_restores_the_clip_and_its_fades_exactly()
    {
        var (track, clip) = Setup();
        clip.FadeIn = S(4);
        clip.FadeOut = S(5);
        var history = new UndoHistory();

        history.Do(new TrimAudioClipCommand(track, clip, ClipEdge.End, S(8)));
        Assert.True(history.Undo());

        Assert.Equal(S(20), clip.SourceIn);
        Assert.Equal(S(30), clip.SourceOut);
        Assert.Equal(S(5), clip.TimelineStart);
        Assert.Equal(S(4), clip.FadeIn);
        Assert.Equal(S(5), clip.FadeOut);
    }

    [Fact]
    public void A_rejected_trim_leaves_nothing_to_undo_wrongly()
    {
        var (track, clip) = Setup();
        var command = new TrimAudioClipCommand(track, clip, ClipEdge.End, S(500));
        command.Execute();

        Assert.False(command.Applied);

        clip.GainDb = -3;      // un cambio posterior que Undo no debe pisar
        command.Undo();

        Assert.Equal(S(30), clip.SourceOut);
        Assert.Equal(-3, clip.GainDb);
    }

    // ------------------------------------------------------------------- dividir

    [Fact]
    public void Splitting_produces_two_contiguous_clips_covering_the_same_audio()
    {
        var (track, clip) = Setup();

        var second = track.SplitAt(S(9));

        Assert.NotNull(second);
        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(S(5), clip.TimelineStart);
        Assert.Equal(S(9), clip.TimelineEnd);
        Assert.Equal(S(24), clip.SourceOut);
        Assert.Equal(S(9), second.TimelineStart);
        Assert.Equal(S(24), second.SourceIn);
        Assert.Equal(S(30), second.SourceOut);
    }

    [Fact]
    public void The_second_half_inherits_gain_and_mute()
    {
        var (track, clip) = Setup();
        clip.GainDb = -4.5;
        clip.IsMuted = true;

        var second = track.SplitAt(S(9))!;

        Assert.Equal(-4.5, second.GainDb);
        Assert.True(second.IsMuted);
    }

    [Fact]
    public void Fades_stay_at_the_outer_ends_and_no_fade_appears_at_the_cut()
    {
        var (track, clip) = Setup();
        clip.FadeIn = S(1);
        clip.FadeOut = S(2);

        var second = track.SplitAt(S(9))!;

        Assert.Equal(S(1), clip.FadeIn);
        Assert.Equal(TimeSpan.Zero, clip.FadeOut);
        Assert.Equal(TimeSpan.Zero, second.FadeIn);
        Assert.Equal(S(2), second.FadeOut);
    }

    [Theory]
    [InlineData(4)]      // antes del clip
    [InlineData(15)]     // justo en el final
    [InlineData(5.01)]   // dejaría una mitad de 10 ms
    [InlineData(14.99)]
    public void Cuts_outside_the_clip_or_too_close_to_an_edge_are_rejected(double at)
    {
        var (track, _) = Setup();

        Assert.Null(track.SplitAt(S(at)));
        Assert.Single(track.Clips);
    }

    [Fact]
    public void A_locked_track_refuses_splitting()
    {
        var (track, _) = Setup();
        track.IsLocked = true;

        Assert.Null(track.SplitAt(S(9)));
    }

    [Fact]
    public void Undoing_a_split_rejoins_the_clip_with_its_original_fade()
    {
        var (track, clip) = Setup();
        clip.FadeOut = S(2);
        var history = new UndoHistory();

        history.Do(new SplitAudioClipCommand(track, S(9)));
        Assert.Equal(2, track.Clips.Count);
        Assert.True(history.Undo());

        Assert.Single(track.Clips);
        Assert.Equal(S(30), clip.SourceOut);
        Assert.Equal(S(15), clip.TimelineEnd);
        Assert.Equal(S(2), clip.FadeOut);
    }

    [Fact]
    public void Redo_after_undo_splits_again()
    {
        var (track, _) = Setup();
        var history = new UndoHistory();

        history.Do(new SplitAudioClipCommand(track, S(9)));
        history.Undo();
        history.Redo();

        Assert.Equal(2, track.Clips.Count);
    }

    [Fact]
    public void A_rejected_split_does_not_disturb_the_clip_when_undone()
    {
        var (track, clip) = Setup();
        var command = new SplitAudioClipCommand(track, S(4));
        command.Execute();

        Assert.Null(command.SecondHalf);
        command.Undo();

        Assert.Single(track.Clips);
        Assert.Equal(S(15), clip.TimelineEnd);
    }
}
