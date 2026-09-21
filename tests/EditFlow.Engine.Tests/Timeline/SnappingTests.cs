// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Tests.Timeline;

public class SnappingTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void The_start_snaps_to_a_nearby_point()
    {
        var result = Snapping.Snap(S(4.95), S(2), [S(5)], Threshold);

        Assert.Equal(S(5), result);
    }

    [Fact]
    public void The_end_snaps_too_so_a_clip_can_finish_where_something_else_finishes()
    {
        // Lo habitual es querer que la música TERMINE donde termina el video, no solo que
        // empiece donde empieza algo. El clip dura 3 s y su final cae a 5 cs del punto 10.
        var result = Snapping.Snap(S(6.95), S(3), [S(10)], Threshold);

        Assert.Equal(S(7), result);
        Assert.Equal(S(10), result + S(3));
    }

    [Fact]
    public void Whichever_edge_is_closer_is_the_one_that_snaps()
    {
        // El inicio (4,4) está a 0,6 del punto 5 y el final (5,4) a 0,4: el que se imanta
        // es el final, y el inicio se desplaza en consecuencia.
        var result = Snapping.Snap(S(4.4), S(1), [S(5)], TimeSpan.FromSeconds(1));

        Assert.Equal(S(4), result);
        Assert.Equal(S(5), result + S(1));
    }

    [Fact]
    public void Nothing_snaps_beyond_the_threshold()
    {
        var result = Snapping.Snap(S(4.5), S(2), [S(5)], Threshold);

        Assert.Equal(S(4.5), result);
    }

    [Fact]
    public void The_closest_of_several_points_wins()
    {
        // 5,03 está a 0,03 de 5,0 y a 0,05 de 5,08: no hay empate.
        var result = Snapping.Snap(S(5.03), S(1), [S(5.0), S(5.08)], Threshold);

        Assert.Equal(S(5.0), result);
    }

    [Fact]
    public void An_exact_hit_stays_put()
    {
        Assert.Equal(S(3), Snapping.Snap(S(3), S(2), [S(3)], Threshold));
    }

    [Fact]
    public void Snapping_the_end_near_the_origin_never_yields_a_negative_start()
    {
        // El final de un clip de 5 s imantado al segundo 1 pondría su inicio en -4.
        var result = Snapping.Snap(S(-3.95), S(5), [S(1)], Threshold);

        Assert.True(result >= TimeSpan.Zero);
    }

    [Fact]
    public void With_no_points_the_request_is_returned_unchanged()
    {
        Assert.Equal(S(7.3), Snapping.Snap(S(7.3), S(2), [], Threshold));
    }

    [Fact]
    public void A_wider_threshold_reaches_farther_points()
    {
        var wide = TimeSpan.FromSeconds(1);

        // El inicio (4,7) está a 0,3 del punto 5; el final (5,7) a 0,7. Gana el inicio.
        Assert.Equal(S(5), Snapping.Snap(S(4.7), S(1), [S(5)], wide));
    }

    // ---------------------------------------------------------- puntos

    private static MediaInfo Media(string name, double seconds) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    private static AudioClip Audio(double start, double seconds) =>
        new(Media("m.mp3", 120), TimeSpan.Zero, S(seconds), S(start));

    [Fact]
    public void Video_cut_points_are_included()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 4)));
        sequence.Video.Append(new Clip(Media("b.mp4", 6)));

        var points = Snapping.PointsFor(sequence, S(0), moving: null);

        Assert.Contains(S(4), points);
        Assert.Contains(S(10), points);
    }

    [Fact]
    public void The_playhead_and_the_origin_are_included()
    {
        var points = Snapping.PointsFor(new EditSequence(), S(7.5), moving: null);

        Assert.Contains(S(7.5), points);
        Assert.Contains(S(0), points);
    }

    [Fact]
    public void Other_audio_clips_contribute_both_edges()
    {
        var sequence = new EditSequence();
        sequence.AddAudioTrack().TryAdd(Audio(start: 20, seconds: 5));

        var points = Snapping.PointsFor(sequence, S(0), moving: null);

        Assert.Contains(S(20), points);
        Assert.Contains(S(25), points);
    }

    [Fact]
    public void The_dragged_clip_does_not_snap_to_itself()
    {
        // Imantarse a su propia posición anterior haría que arrastrarlo unos píxeles no lo
        // moviera: siempre volvería a donde estaba.
        var sequence = new EditSequence();
        var moving = Audio(start: 20, seconds: 5);
        sequence.AddAudioTrack().TryAdd(moving);

        var points = Snapping.PointsFor(sequence, S(0), moving);

        Assert.DoesNotContain(S(20), points);
        Assert.DoesNotContain(S(25), points);
    }

    [Fact]
    public void Clips_on_other_tracks_are_snap_targets_too()
    {
        // Alinear la voz con la música de otra pista es el uso más habitual.
        var sequence = new EditSequence();
        sequence.AddAudioTrack().TryAdd(Audio(0, 3));
        sequence.AddAudioTrack().TryAdd(Audio(12, 2));

        var points = Snapping.PointsFor(sequence, S(0), moving: null);

        Assert.Contains(S(12), points);
        Assert.Contains(S(14), points);
    }
}
