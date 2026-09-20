using EditFlow.Core.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Tests.Timeline;

public class VideoTimelineTests
{
    private static MediaInfo Source(string name, double seconds) => new(
        Path: name,
        Duration: TimeSpan.FromSeconds(seconds),
        Width: 1920,
        Height: 1080,
        FrameRate: 30,
        VideoCodec: "h264",
        HasAudio: true);

    private static VideoTimeline WithClips(params double[] durations)
    {
        var timeline = new VideoTimeline();
        for (var i = 0; i < durations.Length; i++)
        {
            timeline.Append(new Clip(Source($"clip{i}.mp4", durations[i])));
        }

        return timeline;
    }

    [Fact]
    public void Joining_videos_adds_up_their_durations()
    {
        var timeline = WithClips(5, 3, 7);

        Assert.Equal(TimeSpan.FromSeconds(15), timeline.Duration);
        Assert.Equal(3, timeline.Clips.Count);
    }

    [Fact]
    public void Positions_follow_the_order_with_no_gaps()
    {
        var timeline = WithClips(5, 3, 7);

        Assert.Equal(TimeSpan.Zero, timeline.StartOf(timeline.Clips[0]));
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.StartOf(timeline.Clips[1]));
        Assert.Equal(TimeSpan.FromSeconds(8), timeline.StartOf(timeline.Clips[2]));
    }

    [Fact]
    public void Removing_a_clip_closes_the_gap_automatically()
    {
        var timeline = WithClips(5, 3, 7);
        var middle = timeline.Clips[1];

        timeline.Remove(middle);

        Assert.Equal(TimeSpan.FromSeconds(12), timeline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.StartOf(timeline.Clips[1]));
    }

    [Fact]
    public void Reordering_recomputes_positions()
    {
        var timeline = WithClips(5, 3, 7);
        var last = timeline.Clips[2];

        timeline.Move(last, 0);

        Assert.Same(last, timeline.Clips[0]);
        Assert.Equal(TimeSpan.Zero, timeline.StartOf(last));
        Assert.Equal(TimeSpan.FromSeconds(7), timeline.StartOf(timeline.Clips[1]));
        Assert.Equal(TimeSpan.FromSeconds(15), timeline.Duration);
    }

    [Fact]
    public void Moving_past_the_end_lands_on_the_last_position()
    {
        // Soltar un clip más allá del extremo debe significar "ponlo al final",
        // no interrumpir el arrastre con un error.
        var timeline = WithClips(5, 3, 7);
        var first = timeline.Clips[0];

        timeline.Move(first, 99);

        Assert.Same(first, timeline.Clips[2]);
    }

    [Fact]
    public void Locates_the_clip_playing_at_a_given_moment()
    {
        var timeline = WithClips(5, 3, 7);

        var located = timeline.ClipAt(TimeSpan.FromSeconds(6));

        Assert.NotNull(located);
        Assert.Same(timeline.Clips[1], located.Value.Clip);
        Assert.Equal(TimeSpan.FromSeconds(1), located.Value.Offset);
    }

    [Fact]
    public void A_boundary_belongs_to_the_clip_that_starts_there()
    {
        // Con clips de 5 y 3 segundos, el instante 5 es el primer fotograma del
        // segundo clip. Asignarlo al primero mostraría un fotograma ya pasado.
        var timeline = WithClips(5, 3);

        var located = timeline.ClipAt(TimeSpan.FromSeconds(5));

        Assert.NotNull(located);
        Assert.Same(timeline.Clips[1], located.Value.Clip);
        Assert.Equal(TimeSpan.Zero, located.Value.Offset);
    }

    [Fact]
    public void Nothing_is_playing_past_the_end()
    {
        var timeline = WithClips(5);

        Assert.Null(timeline.ClipAt(TimeSpan.FromSeconds(5)));
        Assert.Null(timeline.ClipAt(TimeSpan.FromSeconds(99)));
        Assert.Null(timeline.ClipAt(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Splitting_inserts_the_second_half_right_after_the_first()
    {
        var timeline = WithClips(10);

        var second = timeline.SplitAt(TimeSpan.FromSeconds(4));

        Assert.NotNull(second);
        Assert.Equal(2, timeline.Clips.Count);
        Assert.Same(second, timeline.Clips[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), timeline.Clips[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(6), timeline.Clips[1].Duration);
    }

    [Fact]
    public void Splitting_never_changes_the_total_duration()
    {
        var timeline = WithClips(5, 3, 7);
        var before = timeline.Duration;

        timeline.SplitAt(TimeSpan.FromSeconds(6));

        Assert.Equal(before, timeline.Duration);
        Assert.Equal(4, timeline.Clips.Count);
    }

    [Fact]
    public void Splitting_exactly_on_a_clip_boundary_does_nothing()
    {
        // En el instante 5 empieza el segundo clip, así que cortar ahí pediría
        // una primera mitad de duración cero. Debe rechazarse sin efectos.
        var timeline = WithClips(5, 3);

        var result = timeline.SplitAt(TimeSpan.FromSeconds(5));

        Assert.Null(result);
        Assert.Equal(2, timeline.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(8), timeline.Duration);
    }

    [Fact]
    public void Splitting_past_the_end_does_nothing()
    {
        var timeline = WithClips(5);

        Assert.Null(timeline.SplitAt(TimeSpan.FromSeconds(50)));
        Assert.Single(timeline.Clips);
    }

    [Fact]
    public void Asking_for_the_start_of_a_foreign_clip_is_an_error()
    {
        var timeline = WithClips(5);
        var stranger = new Clip(Source("other.mp4", 3));

        Assert.Throws<ArgumentException>(() => timeline.StartOf(stranger));
    }

    [Fact]
    public void An_empty_timeline_has_no_duration_and_nothing_playing()
    {
        var timeline = new VideoTimeline();

        Assert.True(timeline.IsEmpty);
        Assert.Equal(TimeSpan.Zero, timeline.Duration);
        Assert.Null(timeline.ClipAt(TimeSpan.Zero));
    }

    [Fact]
    public void The_same_source_can_appear_several_times()
    {
        // Es el resultado normal de cortar un clip: dos entradas que apuntan al
        // mismo archivo. La timeline debe tratarlas como clips distintos.
        var source = Source("repeated.mp4", 10);
        var timeline = new VideoTimeline();
        timeline.Append(new Clip(source, TimeSpan.Zero, TimeSpan.FromSeconds(4)));
        timeline.Append(new Clip(source, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromSeconds(8), timeline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(4), timeline.StartOf(timeline.Clips[1]));
    }
}
