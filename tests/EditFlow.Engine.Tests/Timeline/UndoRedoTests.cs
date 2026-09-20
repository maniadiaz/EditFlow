using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class UndoRedoTests
{
    private static MediaInfo Source(string name = "a.mp4", double seconds = 10) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    private static (VideoTimeline Timeline, UndoHistory Undo) Setup(params double[] durations)
    {
        var timeline = new VideoTimeline();
        var undo = new UndoHistory();

        for (var i = 0; i < durations.Length; i++)
        {
            undo.Do(new AppendClipCommand(timeline, new Clip(Source($"clip{i}.mp4", durations[i]))));
        }

        return (timeline, undo);
    }

    [Fact]
    public void A_fresh_stack_has_nothing_to_undo()
    {
        var undo = new UndoHistory();

        Assert.False(undo.CanUndo);
        Assert.False(undo.CanRedo);
        Assert.Null(undo.NextUndoDescription);
    }

    [Fact]
    public void Undoing_an_append_removes_the_clip()
    {
        var (timeline, undo) = Setup(5, 3);

        undo.Undo();

        Assert.Single(timeline.Clips);
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.Duration);
    }

    [Fact]
    public void Redoing_puts_it_back_in_the_same_place()
    {
        var (timeline, undo) = Setup(5, 3, 7);

        undo.Undo();
        undo.Undo();
        undo.Redo();

        Assert.Equal(2, timeline.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(8), timeline.Duration);
    }

    [Fact]
    public void Undoing_a_removal_restores_the_original_position()
    {
        var (timeline, undo) = Setup(5, 3, 7);
        var middle = timeline.Clips[1];

        undo.Do(new RemoveClipCommand(timeline, middle));
        Assert.Equal(2, timeline.Clips.Count);

        undo.Undo();

        Assert.Equal(3, timeline.Clips.Count);
        Assert.Same(middle, timeline.Clips[1]);
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.StartOf(middle));
    }

    [Fact]
    public void Undoing_a_split_merges_the_halves_back()
    {
        var (timeline, undo) = Setup(10);
        var original = timeline.Clips[0];

        undo.Do(new SplitClipCommand(timeline, TimeSpan.FromSeconds(4)));
        Assert.Equal(2, timeline.Clips.Count);

        undo.Undo();

        Assert.Single(timeline.Clips);
        Assert.Same(original, timeline.Clips[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), original.Duration);
    }

    [Fact]
    public void A_refused_split_leaves_nothing_to_undo_wrongly()
    {
        // Cortar justo en el borde se rechaza. Si la operación guardara la referencia
        // al clip de todos modos, deshacerla lo recortaría sin que nadie lo hubiera
        // dividido.
        var (timeline, undo) = Setup(10);
        var original = timeline.Clips[0];
        var command = new SplitClipCommand(timeline, TimeSpan.Zero);

        undo.Do(command);
        Assert.Null(command.SecondHalf);

        undo.Undo();

        Assert.Single(timeline.Clips);
        Assert.Equal(TimeSpan.FromSeconds(10), original.Duration);
    }

    [Fact]
    public void Undoing_a_trim_restores_the_exact_range()
    {
        var (timeline, undo) = Setup(10);
        var clip = timeline.Clips[0];

        undo.Do(new TrimClipCommand(clip, ClipEdge.Start, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(3), clip.SourceIn);

        undo.Undo();

        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceOut);
    }

    [Fact]
    public void Undoing_a_clamped_trim_still_restores_exactly()
    {
        // El recorte se acota al material disponible, así que lo aplicado puede ser
        // menor que lo pedido. Deshacer con el desplazamiento original, en lugar de
        // con el intervalo guardado, desplazaría de más.
        var (timeline, undo) = Setup(10);
        var clip = timeline.Clips[0];

        undo.Do(new TrimClipCommand(clip, ClipEdge.Start, TimeSpan.FromSeconds(-50)));
        undo.Undo();

        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceOut);
    }

    [Fact]
    public void Undoing_a_move_returns_the_clip_to_its_original_index()
    {
        var (timeline, undo) = Setup(5, 3, 7);
        var last = timeline.Clips[2];

        undo.Do(new MoveClipCommand(timeline, last, 0));
        Assert.Same(last, timeline.Clips[0]);

        undo.Undo();

        Assert.Same(last, timeline.Clips[2]);
    }

    [Fact]
    public void A_new_action_discards_the_redo_history()
    {
        // Conservarla permitiría rehacer operaciones que asumían un estado que ya
        // no existe.
        var (timeline, undo) = Setup(5, 3);

        undo.Undo();
        Assert.True(undo.CanRedo);

        undo.Do(new AppendClipCommand(timeline, new Clip(Source("new.mp4", 2))));

        Assert.False(undo.CanRedo);
        Assert.Equal(2, timeline.Clips.Count);
    }

    [Fact]
    public void A_long_sequence_of_edits_undoes_back_to_the_start()
    {
        var timeline = new VideoTimeline();
        var undo = new UndoHistory();

        undo.Do(new AppendClipCommand(timeline, new Clip(Source("a.mp4", 10))));
        undo.Do(new AppendClipCommand(timeline, new Clip(Source("b.mp4", 6))));
        undo.Do(new SplitClipCommand(timeline, TimeSpan.FromSeconds(4)));
        undo.Do(new MoveClipCommand(timeline, timeline.Clips[2], 0));
        undo.Do(new TrimClipCommand(timeline.Clips[0], ClipEdge.End, TimeSpan.FromSeconds(-2)));
        undo.Do(new RemoveClipCommand(timeline, timeline.Clips[1]));

        while (undo.CanUndo)
        {
            undo.Undo();
        }

        Assert.True(timeline.IsEmpty);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Redoing_the_whole_sequence_reproduces_the_final_state()
    {
        var timeline = new VideoTimeline();
        var undo = new UndoHistory();

        undo.Do(new AppendClipCommand(timeline, new Clip(Source("a.mp4", 10))));
        undo.Do(new AppendClipCommand(timeline, new Clip(Source("b.mp4", 6))));
        undo.Do(new SplitClipCommand(timeline, TimeSpan.FromSeconds(4)));

        var expectedCount = timeline.Clips.Count;
        var expectedDuration = timeline.Duration;

        while (undo.CanUndo) { undo.Undo(); }
        while (undo.CanRedo) { undo.Redo(); }

        Assert.Equal(expectedCount, timeline.Clips.Count);
        Assert.Equal(expectedDuration, timeline.Duration);
    }

    [Fact]
    public void The_description_names_the_next_action()
    {
        var (timeline, undo) = Setup(10);

        undo.Do(new SplitClipCommand(timeline, TimeSpan.FromSeconds(4)));

        Assert.Equal("Dividir clip", undo.NextUndoDescription);

        undo.Undo();

        Assert.Equal("Dividir clip", undo.NextRedoDescription);
    }

    [Fact]
    public void Changes_are_announced()
    {
        var (timeline, undo) = Setup(10);
        var notifications = 0;
        undo.Changed += (_, _) => notifications++;

        undo.Do(new SplitClipCommand(timeline, TimeSpan.FromSeconds(4)));
        undo.Undo();
        undo.Redo();

        Assert.Equal(3, notifications);
    }

    [Fact]
    public void Clearing_an_already_empty_history_announces_nothing()
    {
        var undo = new UndoHistory();
        var notifications = 0;
        undo.Changed += (_, _) => notifications++;

        undo.Clear();

        Assert.Equal(0, notifications);
    }
}
