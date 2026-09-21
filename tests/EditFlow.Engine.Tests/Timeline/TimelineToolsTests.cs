// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class TimelineToolsTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Video(string name, double seconds = 60) =>
        new(name, S(seconds), 1920, 1080, 30, "h264", true);

    /// <summary>Tres clips de 10 s: A usa 20–30 de su archivo, B 20–30 y C 20–30 (archivos de 60 s).</summary>
    private static (VideoTimeline Timeline, Clip A, Clip B, Clip C) Setup()
    {
        var timeline = new VideoTimeline();
        var a = new Clip(Video("a.mp4"), S(20), S(30));
        var b = new Clip(Video("b.mp4"), S(20), S(30));
        var c = new Clip(Video("c.mp4"), S(20), S(30));
        timeline.Append(a);
        timeline.Append(b);
        timeline.Append(c);
        return (timeline, a, b, c);
    }

    // ------------------------------------------------------------------ slip

    [Fact]
    public void Slip_shows_a_different_part_of_the_file_without_changing_duration_or_position()
    {
        var (timeline, _, b, _) = Setup();
        var startBefore = timeline.StartOf(b);

        var applied = TimelineTools.Slip(b, S(5));

        Assert.Equal(S(5), applied);
        Assert.Equal(S(25), b.SourceIn);
        Assert.Equal(S(35), b.SourceOut);
        Assert.Equal(S(10), b.Duration);
        Assert.Equal(startBefore, timeline.StartOf(b));
        Assert.Equal(S(30), timeline.Duration);
    }

    [Fact]
    public void Slip_is_limited_by_both_ends_of_the_file()
    {
        var (_, _, b, _) = Setup();

        Assert.Equal(S(-20), TimelineTools.ClampSlip(b, S(-100)));   // hasta el inicio del archivo
        Assert.Equal(S(30), TimelineTools.ClampSlip(b, S(100)));     // hasta el final (60 s)

        TimelineTools.Slip(b, S(100));
        Assert.Equal(S(60), b.SourceOut);
        Assert.Equal(S(10), b.Duration);
    }

    [Fact]
    public void Slip_of_a_clip_using_the_whole_file_cannot_move()
    {
        var clip = new Clip(Video("todo.mp4", 10), S(0), S(10));

        Assert.Equal(TimeSpan.Zero, TimelineTools.Slip(clip, S(3)));
        Assert.Equal(TimeSpan.Zero, TimelineTools.Slip(clip, S(-3)));
    }

    [Fact]
    public void Slip_undo_restores_the_exact_range_and_redo_reapplies_it()
    {
        var (_, _, b, _) = Setup();
        var history = new UndoHistory();

        history.Do(new SlipClipCommand(b, S(4)));
        Assert.Equal(S(24), b.SourceIn);

        Assert.True(history.Undo());
        Assert.Equal(S(20), b.SourceIn);
        Assert.Equal(S(30), b.SourceOut);

        Assert.True(history.Redo());
        Assert.Equal(S(24), b.SourceIn);
    }

    // ------------------------------------------------------------------ roll

    [Fact]
    public void Roll_moves_the_cut_and_the_total_duration_stays_the_same()
    {
        var (timeline, a, b, c) = Setup();
        var total = timeline.Duration;

        var applied = TimelineTools.Roll(timeline, a, S(3));

        Assert.Equal(S(3), applied);
        Assert.Equal(S(13), a.Duration);          // el anterior gana 3 s
        Assert.Equal(S(7), b.Duration);           // el siguiente los pierde
        Assert.Equal(S(23), b.SourceIn);
        Assert.Equal(total, timeline.Duration);
        Assert.Equal(S(20), timeline.StartOf(c)); // lo que viene detrás no se mueve
        Assert.Equal(S(20), c.SourceIn);
    }

    [Fact]
    public void Roll_backwards_shortens_the_first_and_lengthens_the_second()
    {
        var (timeline, a, b, _) = Setup();

        TimelineTools.Roll(timeline, a, S(-4));

        Assert.Equal(S(6), a.Duration);
        Assert.Equal(S(14), b.Duration);
        Assert.Equal(S(16), b.SourceIn);
    }

    [Fact]
    public void Roll_never_leaves_either_clip_shorter_than_the_minimum()
    {
        var (timeline, a, b, _) = Setup();

        TimelineTools.Roll(timeline, a, S(100));

        Assert.Equal(Clip.MinimumDuration, b.Duration);
        Assert.Equal(S(20), a.Duration + b.Duration);
    }

    [Fact]
    public void Roll_is_limited_by_the_material_the_files_have()
    {
        var timeline = new VideoTimeline();
        var a = new Clip(Video("a.mp4", 32), S(20), S(30));   // solo 2 s de material tras el final
        var b = new Clip(Video("b.mp4", 60), S(20), S(30));
        timeline.Append(a);
        timeline.Append(b);

        Assert.Equal(S(2), TimelineTools.ClampRoll(timeline, a, S(9)));

        var c = new Clip(Video("c.mp4", 60), S(1), S(30));    // solo 1 s de material antes de su inicio
        var d = new Clip(Video("d.mp4", 60), S(1), S(30));
        var t2 = new VideoTimeline();
        t2.Append(c);
        t2.Append(d);

        Assert.Equal(S(-1), TimelineTools.ClampRoll(t2, c, S(-9)));
    }

    [Fact]
    public void The_last_clip_has_no_cut_to_roll()
    {
        var (timeline, _, _, c) = Setup();

        Assert.Equal(TimeSpan.Zero, TimelineTools.Roll(timeline, c, S(2)));
        Assert.Equal(S(20), c.SourceIn);
    }

    [Fact]
    public void Roll_undo_restores_both_clips()
    {
        var (timeline, a, b, _) = Setup();
        var history = new UndoHistory();

        history.Do(new RollEditCommand(timeline, a, S(3)));
        Assert.True(history.Undo());

        Assert.Equal(S(30), a.SourceOut);
        Assert.Equal(S(20), b.SourceIn);
        Assert.Equal(S(30), timeline.Duration);
    }

    // ----------------------------------------------------------------- slide

    [Fact]
    public void Slide_moves_a_clip_later_keeping_its_content_and_the_total_duration()
    {
        var (timeline, a, b, c) = Setup();
        var total = timeline.Duration;

        var applied = TimelineTools.Slide(timeline, b, S(3));

        Assert.Equal(S(3), applied);
        Assert.Equal(S(20), b.SourceIn);          // el contenido del clip no cambia
        Assert.Equal(S(30), b.SourceOut);
        Assert.Equal(S(13), a.Duration);          // el anterior se alarga
        Assert.Equal(S(7), c.Duration);           // el siguiente se acorta
        Assert.Equal(S(13), timeline.StartOf(b)); // y el clip empieza más tarde
        Assert.Equal(total, timeline.Duration);
        Assert.Equal(S(23), c.SourceIn);
    }

    [Fact]
    public void Slide_needs_a_neighbour_on_both_sides()
    {
        var (timeline, a, _, c) = Setup();

        Assert.Equal(TimeSpan.Zero, TimelineTools.Slide(timeline, a, S(2)));
        Assert.Equal(TimeSpan.Zero, TimelineTools.Slide(timeline, c, S(-2)));
        Assert.Equal(TimeSpan.Zero, TimelineTools.ClampSlide(timeline, a, S(2)));
    }

    [Fact]
    public void Slide_undo_restores_the_neighbours()
    {
        var (timeline, a, b, c) = Setup();
        var history = new UndoHistory();

        history.Do(new SlideClipCommand(timeline, b, S(-4)));
        Assert.Equal(S(6), a.Duration);

        Assert.True(history.Undo());

        Assert.Equal(S(10), a.Duration);
        Assert.Equal(S(10), c.Duration);
        Assert.Equal(S(20), c.SourceIn);
        Assert.Equal(S(30), timeline.Duration);
    }

    [Fact]
    public void A_rejected_tool_leaves_nothing_to_undo_wrongly()
    {
        var (timeline, a, _, _) = Setup();
        var command = new SlideClipCommand(timeline, a, S(2));   // el primero no puede deslizarse
        command.Execute();

        Assert.Equal(TimeSpan.Zero, command.Applied);

        a.RestoreRange(S(21), S(31));   // un cambio posterior que Undo no debe pisar
        command.Undo();

        Assert.Equal(S(21), a.SourceIn);
    }

    [Fact]
    public void Tools_reject_clips_that_are_not_in_the_timeline()
    {
        var (timeline, _, _, _) = Setup();
        var stranger = new Clip(Video("x.mp4"), S(0), S(10));

        Assert.Equal(TimeSpan.Zero, TimelineTools.Roll(timeline, stranger, S(1)));
        Assert.Equal(TimeSpan.Zero, TimelineTools.Slide(timeline, stranger, S(1)));
    }
}
