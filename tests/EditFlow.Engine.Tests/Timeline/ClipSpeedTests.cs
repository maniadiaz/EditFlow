// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class ClipSpeedTests
{
    private static MediaInfo Source(string name = "a.mp4", double seconds = 20) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    [Fact]
    public void A_fresh_clip_plays_at_normal_speed()
    {
        var clip = new Clip(Source());

        Assert.Equal(1, clip.Speed);
        Assert.Equal(clip.SourceDuration, clip.Duration);
    }

    [Fact]
    public void Doubling_the_speed_halves_how_long_the_clip_lasts_on_the_timeline()
    {
        var clip = new Clip(Source(), TimeSpan.Zero, TimeSpan.FromSeconds(10)) { Speed = 2 };

        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), clip.Duration);
    }

    [Fact]
    public void Halving_the_speed_doubles_how_long_the_clip_lasts_on_the_timeline()
    {
        var clip = new Clip(Source(), TimeSpan.Zero, TimeSpan.FromSeconds(10)) { Speed = 0.5 };

        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceDuration);
        Assert.Equal(TimeSpan.FromSeconds(20), clip.Duration);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.1)]
    [InlineData(16)]
    [InlineData(50)]
    public void Speed_is_clamped_to_the_supported_range(double requested)
    {
        var clip = new Clip(Source()) { Speed = requested };

        Assert.InRange(clip.Speed, Clip.MinimumSpeed, Clip.MaximumSpeed);
    }

    [Fact]
    public void SourceTimeAt_converts_timeline_time_to_source_time_using_the_speed()
    {
        var clip = new Clip(Source()) { Speed = 2 };

        // A doble velocidad, un segundo de timeline son dos segundos de archivo.
        Assert.Equal(TimeSpan.FromSeconds(2), clip.SourceTimeAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SourceTimeAt_is_the_identity_at_normal_speed()
    {
        var clip = new Clip(Source());

        Assert.Equal(TimeSpan.FromSeconds(3.5), clip.SourceTimeAt(TimeSpan.FromSeconds(3.5)));
    }

    [Fact]
    public void Cloning_copies_the_speed()
    {
        var clip = new Clip(Source()) { Speed = 4 };

        Assert.Equal(4, clip.Clone().Speed);
    }

    [Fact]
    public void Splitting_a_sped_up_clip_cuts_the_right_amount_of_source_material()
    {
        // A doble velocidad, 10s de archivo duran 5s en la timeline. Cortar a los 2s de
        // timeline debe caer a los 4s de archivo, no a los 2s.
        var clip = new Clip(Source(), TimeSpan.Zero, TimeSpan.FromSeconds(10)) { Speed = 2 };

        var second = clip.SplitAt(TimeSpan.FromSeconds(2));

        Assert.NotNull(second);
        Assert.Equal(TimeSpan.FromSeconds(4), clip.SourceOut);
        Assert.Equal(TimeSpan.FromSeconds(4), second.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(10), second.SourceOut);
    }

    [Fact]
    public void Splitting_a_sped_up_clip_keeps_the_speed_on_both_halves()
    {
        var clip = new Clip(Source(), TimeSpan.Zero, TimeSpan.FromSeconds(10)) { Speed = 2 };

        var second = clip.SplitAt(TimeSpan.FromSeconds(2));

        Assert.Equal(2, clip.Speed);
        Assert.Equal(2, second!.Speed);
    }

    [Fact]
    public void Splitting_still_respects_the_minimum_duration_in_timeline_time()
    {
        // A 4x, 10s de archivo duran 2,5s en la timeline. Cortar a 40ms de timeline (el
        // mínimo) debe aceptarse; por debajo, rechazarse.
        var clip = new Clip(Source(), TimeSpan.Zero, TimeSpan.FromSeconds(10)) { Speed = 4 };

        Assert.Null(clip.SplitAt(TimeSpan.FromMilliseconds(39)));
        Assert.NotNull(clip.SplitAt(TimeSpan.FromMilliseconds(40)));
    }
}

public class SetSpeedCommandTests
{
    private static MediaInfo Source(string name = "a.mp4", double seconds = 20) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    [Fact]
    public void Setting_a_speed_applies_it_to_the_clip()
    {
        var clip = new Clip(Source());
        var undo = new UndoHistory();

        undo.Do(new SetSpeedCommand(clip, 2));

        Assert.Equal(2, clip.Speed);
    }

    [Fact]
    public void Undoing_restores_whatever_speed_was_there_before()
    {
        var clip = new Clip(Source()) { Speed = 0.5 };
        var undo = new UndoHistory();

        undo.Do(new SetSpeedCommand(clip, 3));
        undo.Undo();

        Assert.Equal(0.5, clip.Speed);
    }

    [Fact]
    public void An_explicit_previous_value_is_used_instead_of_the_clips_current_one()
    {
        // El mismo caso que color y transición: mientras se arrastra un deslizador el clip ya
        // lleva el valor provisional, así que deshacer debe volver a lo que había ANTES del
        // arrastre, no a lo último que se mostró.
        var clip = new Clip(Source()) { Speed = 5 }; // valor provisional, ya aplicado sin historial
        var undo = new UndoHistory();

        undo.Do(new SetSpeedCommand(clip, 5, previous: 1));
        undo.Undo();

        Assert.Equal(1, clip.Speed);
    }
}

public class VideoTimelineSpeedTests
{
    private static MediaInfo Source(string name, double seconds) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    [Fact]
    public void A_sped_up_clip_shortens_the_whole_timeline()
    {
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(Source("a.mp4", 10)));
        timeline.Append(new Clip(Source("b.mp4", 10)) { Speed = 2 });

        // 10 + (10/2) = 15, no 20.
        Assert.Equal(TimeSpan.FromSeconds(15), timeline.Duration);
    }

    [Fact]
    public void A_slowed_down_clip_lengthens_the_whole_timeline()
    {
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(Source("a.mp4", 10)));
        timeline.Append(new Clip(Source("b.mp4", 10)) { Speed = 0.5 });

        // 10 + (10/0.5) = 30.
        Assert.Equal(TimeSpan.FromSeconds(30), timeline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), timeline.StartOf(timeline.Clips[1]));
    }

    [Fact]
    public void ClipAt_locates_positions_inside_a_sped_up_clip_correctly()
    {
        var timeline = new VideoTimeline();
        var slow = new Clip(Source("a.mp4", 10)) { Speed = 2 }; // dura 5s en la timeline
        timeline.Append(slow);

        var located = timeline.ClipAt(TimeSpan.FromSeconds(3));

        Assert.NotNull(located);
        Assert.Same(slow, located.Value.Clip);
        Assert.Equal(TimeSpan.FromSeconds(3), located.Value.Offset);
        Assert.Null(timeline.ClipAt(TimeSpan.FromSeconds(5)));
    }
}
