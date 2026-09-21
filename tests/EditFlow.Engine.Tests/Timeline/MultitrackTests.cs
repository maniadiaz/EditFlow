// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class MultitrackTests
{
    private static MediaInfo Media(string name = "a.mp4", double seconds = 10, bool audio = true) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", audio);

    private static AudioClip Audio(double start, double seconds, string name = "m.mp3") =>
        new(Media(name, 60), TimeSpan.Zero, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(start));

    // ------------------------------------------------------------------ pista

    [Fact]
    public void Audio_clips_keep_their_own_free_position()
    {
        var track = new AudioTrack("A1");

        Assert.True(track.TryAdd(Audio(start: 7, seconds: 3)));
        Assert.Equal(TimeSpan.FromSeconds(7), track.Clips[0].TimelineStart);
        Assert.Equal(TimeSpan.FromSeconds(10), track.End);
    }

    [Fact]
    public void Overlapping_clips_on_one_track_are_refused()
    {
        var track = new AudioTrack("A1");
        track.TryAdd(Audio(0, 5));

        Assert.False(track.TryAdd(Audio(3, 5)));
        Assert.Single(track.Clips);
    }

    [Fact]
    public void Clips_that_only_touch_at_the_edge_do_not_collide()
    {
        // Uno termina exactamente donde empieza el otro. Tratarlo como choque impediría
        // colocar clips seguidos, que es lo más normal del mundo.
        var track = new AudioTrack("A1");
        track.TryAdd(Audio(0, 5));

        Assert.True(track.TryAdd(Audio(5, 3)));
    }

    [Fact]
    public void The_track_stays_ordered_by_position()
    {
        var track = new AudioTrack("A1");
        track.TryAdd(Audio(20, 2));
        track.TryAdd(Audio(0, 2));
        track.TryAdd(Audio(10, 2));

        var starts = track.Clips.Select(c => c.TimelineStart.TotalSeconds).ToArray();
        Assert.Equal([0.0, 10.0, 20.0], starts);
    }

    [Fact]
    public void A_locked_track_refuses_edits()
    {
        var track = new AudioTrack("A1");
        var clip = Audio(0, 4);
        track.TryAdd(clip);
        track.IsLocked = true;

        Assert.False(track.TryAdd(Audio(10, 2)));
        Assert.False(track.Remove(clip));
        Assert.False(track.TryMove(clip, TimeSpan.FromSeconds(20)));
        Assert.Single(track.Clips);
    }

    [Fact]
    public void Moving_a_clip_does_not_collide_with_itself()
    {
        var track = new AudioTrack("A1");
        var clip = Audio(0, 5);
        track.TryAdd(clip);

        // Desplazarlo 2 s solapa con su propia posición anterior; eso no cuenta como choque.
        Assert.True(track.TryMove(clip, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(2), clip.TimelineStart);
    }

    [Fact]
    public void A_source_without_audio_cannot_become_an_audio_clip()
    {
        Assert.Throws<ArgumentException>(() =>
            new AudioClip(Media(audio: false), TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.Zero));
    }

    // ------------------------------------------------------------ mute y solo

    [Fact]
    public void A_muted_track_is_not_audible()
    {
        var track = new AudioTrack("A1") { IsMuted = true };
        Assert.False(track.IsAudible(anySolo: false));
    }

    [Fact]
    public void Solo_silences_every_other_track()
    {
        // Es la razón de ser del solo: escuchar una pista sin silenciar las demás a mano.
        var soloed = new AudioTrack("A1") { IsSolo = true };
        var other = new AudioTrack("A2");

        Assert.True(soloed.IsAudible(anySolo: true));
        Assert.False(other.IsAudible(anySolo: true));
        Assert.True(other.IsAudible(anySolo: false));
    }

    [Fact]
    public void Mute_wins_over_solo()
    {
        var track = new AudioTrack("A1") { IsSolo = true, IsMuted = true };
        Assert.False(track.IsAudible(anySolo: true));
    }

    // ------------------------------------------------------------ volumen

    [Theory]
    [InlineData(-200, -60)]
    [InlineData(50, 12)]
    [InlineData(-6, -6)]
    public void Gain_is_clamped_to_a_usable_range(double requested, double expected)
    {
        var clip = Audio(0, 3);
        clip.GainDb = requested;
        Assert.Equal(expected, clip.GainDb);
    }

    [Fact]
    public void Muting_does_not_forget_the_volume()
    {
        // Silenciar y reactivar debe devolver el volumen que había, no dejarlo a cero.
        var clip = Audio(0, 3);
        clip.GainDb = -4;
        clip.IsMuted = true;
        clip.IsMuted = false;

        Assert.Equal(-4, clip.GainDb);
    }

    [Fact]
    public void Fades_cannot_overlap_each_other()
    {
        // Dos fundidos que se solaparan darían una curva incoherente: el clip estaría
        // subiendo y bajando a la vez.
        var clip = Audio(0, 4);
        clip.FadeIn = TimeSpan.FromSeconds(3);
        clip.FadeOut = TimeSpan.FromSeconds(3);

        Assert.True(clip.FadeIn + clip.FadeOut <= clip.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), clip.FadeOut);
    }

    // ---------------------------------------------------------- secuencia

    [Fact]
    public void Track_names_reuse_the_first_free_number()
    {
        var sequence = new EditSequence();
        sequence.AddAudioTrack();
        var second = sequence.AddAudioTrack();
        sequence.AddAudioTrack();

        sequence.RemoveAudioTrack(second);

        Assert.Equal("A2", sequence.AddAudioTrack().Name);
    }

    [Fact]
    public void Tracks_can_be_reordered()
    {
        var sequence = new EditSequence();
        var a = sequence.AddAudioTrack();
        var b = sequence.AddAudioTrack();
        var c = sequence.AddAudioTrack();

        sequence.MoveAudioTrack(c, 0);

        Assert.Same(c, sequence.AudioTracks[0]);
        Assert.Same(a, sequence.AudioTracks[1]);
        Assert.Same(b, sequence.AudioTracks[2]);
    }

    [Fact]
    public void Dropping_a_track_past_the_end_puts_it_last()
    {
        var sequence = new EditSequence();
        var first = sequence.AddAudioTrack();
        sequence.AddAudioTrack();

        sequence.MoveAudioTrack(first, 99);

        Assert.Same(first, sequence.AudioTracks[^1]);
    }

    [Fact]
    public void Music_longer_than_the_video_extends_the_sequence()
    {
        // Ignorar las pistas de audio recortaría la exportación justo donde termina el
        // video, cortando la música.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("v.mp4", 5)));
        sequence.AddAudioTrack().TryAdd(Audio(0, 12));

        Assert.Equal(TimeSpan.FromSeconds(12), sequence.Duration);
    }

    [Fact]
    public void A_new_clip_prefers_an_existing_track_with_room()
    {
        var sequence = new EditSequence();
        var track = sequence.AddAudioTrack();
        track.TryAdd(Audio(0, 5));

        var chosen = sequence.FindOrCreateTrackFor(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2));

        Assert.Same(track, chosen);
        Assert.Single(sequence.AudioTracks);
    }

    [Fact]
    public void A_clip_that_does_not_fit_gets_a_new_track()
    {
        var sequence = new EditSequence();
        sequence.AddAudioTrack().TryAdd(Audio(0, 10));

        var chosen = sequence.FindOrCreateTrackFor(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

        Assert.Equal(2, sequence.AudioTracks.Count);
        Assert.Equal("A2", chosen.Name);
    }

    [Fact]
    public void Locked_tracks_are_skipped_when_looking_for_room()
    {
        var sequence = new EditSequence();
        sequence.AddAudioTrack().IsLocked = true;

        var chosen = sequence.FindOrCreateTrackFor(TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.False(chosen.IsLocked);
        Assert.Equal(2, sequence.AudioTracks.Count);
    }

    // ------------------------------------------------------ separar audio

    [Fact]
    public void Detaching_audio_creates_an_audio_clip_at_the_same_moment()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("intro.mp4", 4)));
        var interview = new Clip(Media("entrevista.mp4", 9));
        sequence.Video.Append(interview);

        var command = new DetachAudioCommand(sequence, interview);
        command.Execute();

        Assert.NotNull(command.Result);
        Assert.Equal(TimeSpan.FromSeconds(4), command.Result.TimelineStart);
        Assert.Equal(TimeSpan.FromSeconds(9), command.Result.Duration);
        Assert.True(interview.IsAudioDetached);
    }

    [Fact]
    public void Detaching_audio_from_a_silent_clip_does_nothing()
    {
        // Sin esto quedaría una pista vacía y el clip marcado como separado por error.
        var sequence = new EditSequence();
        var silent = new Clip(Media("mudo.mp4", 5, audio: false));
        sequence.Video.Append(silent);

        var command = new DetachAudioCommand(sequence, silent);
        command.Execute();

        Assert.Null(command.Result);
        Assert.Empty(sequence.AudioTracks);
        Assert.False(silent.IsAudioDetached);
    }

    [Fact]
    public void Detaching_twice_does_not_duplicate_the_audio()
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media());
        sequence.Video.Append(clip);

        new DetachAudioCommand(sequence, clip).Execute();
        new DetachAudioCommand(sequence, clip).Execute();

        Assert.Single(sequence.AudioTracks);
        Assert.Single(sequence.AudioTracks[0].Clips);
    }

    [Fact]
    public void Undoing_a_detach_restores_the_video_audio_and_removes_the_track()
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media());
        sequence.Video.Append(clip);
        var history = new UndoHistory();

        history.Do(new DetachAudioCommand(sequence, clip));
        history.Undo();

        Assert.False(clip.IsAudioDetached);
        Assert.Empty(sequence.AudioTracks);
    }

    [Fact]
    public void Undoing_a_detach_keeps_a_track_that_already_existed()
    {
        // Borrar una pista que ya tenía otros clips se los llevaría por delante.
        var sequence = new EditSequence();
        var existing = sequence.AddAudioTrack();
        existing.TryAdd(Audio(20, 5));

        var clip = new Clip(Media("v.mp4", 6));
        sequence.Video.Append(clip);
        var history = new UndoHistory();

        history.Do(new DetachAudioCommand(sequence, clip));
        Assert.Single(sequence.AudioTracks);

        history.Undo();

        Assert.Single(sequence.AudioTracks);
        Assert.Single(existing.Clips);
    }

    [Fact]
    public void Splitting_a_clip_with_detached_audio_does_not_bring_the_sound_back()
    {
        // La segunda mitad debe heredar que su audio vive en la pista; si no, volvería
        // a sonar por su cuenta y el audio se oiría duplicado a partir del corte.
        var sequence = new EditSequence();
        var clip = new Clip(Media("v.mp4", 10));
        sequence.Video.Append(clip);
        new DetachAudioCommand(sequence, clip).Execute();

        var second = sequence.Video.SplitAt(TimeSpan.FromSeconds(4));

        Assert.NotNull(second);
        Assert.True(clip.IsAudioDetached);
        Assert.True(second.IsAudioDetached);
    }

    [Fact]
    public void Moving_a_track_can_be_undone()
    {
        var sequence = new EditSequence();
        var a = sequence.AddAudioTrack();
        sequence.AddAudioTrack();
        var c = sequence.AddAudioTrack();
        var history = new UndoHistory();

        history.Do(new MoveAudioTrackCommand(sequence, c, 0));
        history.Undo();

        Assert.Same(a, sequence.AudioTracks[0]);
        Assert.Same(c, sequence.AudioTracks[2]);
    }

    [Fact]
    public void A_rejected_clip_move_leaves_nothing_to_undo()
    {
        // Un movimiento rechazado por chocar no cambió nada; "deshacerlo" movería el clip
        // desde un sitio al que nunca fue.
        var track = new AudioTrack("A1");
        var moving = Audio(0, 4);
        track.TryAdd(moving);
        track.TryAdd(Audio(10, 4));

        var command = new MoveAudioClipCommand(track, moving, TimeSpan.FromSeconds(11));
        command.Execute();
        Assert.False(command.Moved);

        command.Undo();

        Assert.Equal(TimeSpan.Zero, moving.TimelineStart);
    }

    [Fact]
    public void Gain_changes_can_be_undone()
    {
        var clip = Audio(0, 3);
        clip.GainDb = 2;
        var history = new UndoHistory();

        history.Do(new SetAudioGainCommand(clip, -10));
        Assert.Equal(-10, clip.GainDb);

        history.Undo();
        Assert.Equal(2, clip.GainDb);
    }
}
