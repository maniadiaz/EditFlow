// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class TransitionMathTests
{
    private static MediaInfo Source(string name, double seconds) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    private static Clip Plain(string name, double seconds) => new(Source(name, seconds));

    [Fact]
    public void No_transition_means_no_overlap()
    {
        var previous = Plain("a.mp4", 5);
        var current = Plain("b.mp4", 5);

        Assert.Equal(TimeSpan.Zero, TransitionMath.Overlap(previous, current));
    }

    [Fact]
    public void Without_a_previous_clip_there_is_nothing_to_overlap_with()
    {
        var current = Plain("b.mp4", 5);
        current.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, TransitionMath.Overlap(null, current));
    }

    [Fact]
    public void A_requested_transition_is_honored_when_both_clips_are_long_enough()
    {
        var previous = Plain("a.mp4", 5);
        var current = Plain("b.mp4", 5);
        current.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), TransitionMath.Overlap(previous, current));
    }

    [Fact]
    public void The_overlap_never_exceeds_the_shorter_of_the_two_clips()
    {
        // Un clip de 1 segundo no puede prestar 2 segundos de solape.
        var previous = Plain("a.mp4", 1);
        var current = Plain("b.mp4", 5);
        current.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(1), TransitionMath.Overlap(previous, current));
    }

    [Fact]
    public void A_gap_never_overlaps_with_anything()
    {
        var gap = Clip.CreateGap(TimeSpan.FromSeconds(3));
        var current = Plain("b.mp4", 5);
        current.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, TransitionMath.Overlap(gap, current));

        var previous = Plain("a.mp4", 5);
        var currentGap = Clip.CreateGap(TimeSpan.FromSeconds(3));
        currentGap.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, TransitionMath.Overlap(previous, currentGap));
    }
}

public class TransitionOnTimelineTests
{
    private static MediaInfo Source(string name, double seconds) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    private static Clip Plain(string name, double seconds) => new(Source(name, seconds));

    private static VideoTimeline TwoClipsWithDissolve(double firstSeconds, double secondSeconds, double transitionSeconds)
    {
        var timeline = new VideoTimeline();
        var first = Plain("a.mp4", firstSeconds);
        var second = Plain("b.mp4", secondSeconds);
        second.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(transitionSeconds));

        timeline.Append(first);
        timeline.Append(second);
        return timeline;
    }

    [Fact]
    public void A_transition_shortens_the_composed_duration_by_the_overlap()
    {
        var timeline = TwoClipsWithDissolve(5, 5, 1);

        // 5 + 5 - 1 de solape, no 10.
        Assert.Equal(TimeSpan.FromSeconds(9), timeline.Duration);
    }

    [Fact]
    public void The_incoming_clip_starts_before_the_previous_one_ends()
    {
        var timeline = TwoClipsWithDissolve(5, 5, 1);

        Assert.Equal(TimeSpan.FromSeconds(4), timeline.StartOf(timeline.Clips[1]));
    }

    [Fact]
    public void During_the_overlap_the_outgoing_clip_still_wins_the_hit_test()
    {
        // Simplificación deliberada para el preview en vivo: durante el tramo compartido,
        // el saliente (antes en la lista) gana hasta su propio final.
        var timeline = TwoClipsWithDissolve(5, 5, 1);

        var located = timeline.ClipAt(TimeSpan.FromSeconds(4.5));

        Assert.NotNull(located);
        Assert.Same(timeline.Clips[0], located.Value.Clip);
    }

    [Fact]
    public void Past_the_outgoing_clips_end_the_incoming_clip_takes_over()
    {
        var timeline = TwoClipsWithDissolve(5, 5, 1);

        var located = timeline.ClipAt(TimeSpan.FromSeconds(5));

        Assert.NotNull(located);
        Assert.Same(timeline.Clips[1], located.Value.Clip);
    }

    [Fact]
    public void Without_any_transition_the_layout_matches_plain_concatenation()
    {
        var timeline = new VideoTimeline();
        timeline.Append(Plain("a.mp4", 5));
        timeline.Append(Plain("b.mp4", 3));
        timeline.Append(Plain("c.mp4", 7));

        var layout = timeline.Layout();

        Assert.Equal(TimeSpan.Zero, layout[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), layout[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(8), layout[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(15), layout[2].End);
    }

    [Fact]
    public void Several_transitions_in_a_row_each_shorten_the_total()
    {
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        b.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));
        c.TransitionIn = new Transition(TransitionKind.FadeToBlack, TimeSpan.FromSeconds(2));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        // 5 + 5 + 5 - 1 - 2.
        Assert.Equal(TimeSpan.FromSeconds(12), timeline.Duration);
    }
}

public class SetTransitionCommandTests
{
    private static MediaInfo Source(string name = "a.mp4", double seconds = 10) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    [Fact]
    public void Setting_a_transition_applies_it_to_the_clip()
    {
        var clip = new Clip(Source());
        var undo = new UndoHistory();

        undo.Do(new SetTransitionCommand(clip, new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1))));

        Assert.Equal(TransitionKind.Dissolve, clip.TransitionIn.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), clip.TransitionIn.Duration);
    }

    [Fact]
    public void Undoing_restores_whatever_transition_was_there_before()
    {
        var clip = new Clip(Source())
        {
            TransitionIn = new Transition(TransitionKind.WipeLeft, TimeSpan.FromSeconds(2)),
        };
        var undo = new UndoHistory();

        undo.Do(new SetTransitionCommand(clip, new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1))));
        undo.Undo();

        Assert.Equal(TransitionKind.WipeLeft, clip.TransitionIn.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), clip.TransitionIn.Duration);
    }

    [Fact]
    public void Passing_none_removes_the_transition()
    {
        var clip = new Clip(Source())
        {
            TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1)),
        };
        var undo = new UndoHistory();

        undo.Do(new SetTransitionCommand(clip, Transition.None));

        Assert.True(clip.TransitionIn.IsNone);
    }
}

public class StaleTransitionClearingTests
{
    private static MediaInfo Source(string name, double seconds) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    private static Clip Plain(string name, double seconds) => new(Source(name, seconds));

    [Fact]
    public void Removing_a_clip_clears_the_stale_transition_of_whoever_followed_it()
    {
        // A -> B (disolvencia) -> C. Al quitar B, C queda pegado a A directamente, y la
        // transición que tenía pensada para B ya no describe a su nuevo vecino.
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        c.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        var undo = new UndoHistory();
        undo.Do(new RemoveClipCommand(timeline, b));

        Assert.True(c.TransitionIn.IsNone);
    }

    [Fact]
    public void Undoing_a_removal_restores_the_transition_it_had_cleared()
    {
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        c.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        var undo = new UndoHistory();
        undo.Do(new RemoveClipCommand(timeline, b));
        undo.Undo();

        Assert.Equal(TransitionKind.Dissolve, c.TransitionIn.Kind);
    }

    [Fact]
    public void Moving_a_clip_clears_its_own_transition_because_its_neighbor_changed()
    {
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        c.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        var undo = new UndoHistory();
        undo.Do(new MoveClipCommand(timeline, c, 0));

        Assert.True(c.TransitionIn.IsNone);
    }

    [Fact]
    public void Moving_a_clip_clears_the_stale_transition_of_who_used_to_follow_it()
    {
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        b.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        var undo = new UndoHistory();
        // Mover A al final: B pasa a seguir directamente a nada (es el primero), y su
        // transición pensada para A deja de tener sentido.
        undo.Do(new MoveClipCommand(timeline, a, 2));

        Assert.True(b.TransitionIn.IsNone);
    }

    [Fact]
    public void Moving_a_clip_clears_the_transition_of_whoever_now_follows_it()
    {
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        c.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        var undo = new UndoHistory();
        // Insertar A entre B y C: C ahora sigue a A en vez de a B, así que su transición
        // —pensada para venir de B— ya no describe a quien tiene delante.
        undo.Do(new MoveClipCommand(timeline, a, 1));

        Assert.True(c.TransitionIn.IsNone);
    }

    [Fact]
    public void Undoing_a_move_restores_every_transition_it_had_cleared()
    {
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        c.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);

        var undo = new UndoHistory();
        undo.Do(new MoveClipCommand(timeline, a, 1));
        undo.Undo();

        Assert.Equal(TransitionKind.Dissolve, c.TransitionIn.Kind);
    }

    [Fact]
    public void A_hard_cut_boundary_is_left_alone_by_a_move_elsewhere()
    {
        // Mover un clip sin tocar a los vecinos de una transición existente no debe
        // desactivarla por accidente.
        var timeline = new VideoTimeline();
        var a = Plain("a.mp4", 5);
        var b = Plain("b.mp4", 5);
        var c = Plain("c.mp4", 5);
        var d = Plain("d.mp4", 5);
        c.TransitionIn = new Transition(TransitionKind.Dissolve, TimeSpan.FromSeconds(1));

        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);
        timeline.Append(d);

        var undo = new UndoHistory();
        // Mover D al principio no afecta al par B-C.
        undo.Do(new MoveClipCommand(timeline, d, 0));

        Assert.Equal(TransitionKind.Dissolve, c.TransitionIn.Kind);
    }
}
