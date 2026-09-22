// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class OverlayTrackTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static OverlayItem Title(double start, double seconds, string text = "Hola") =>
        OverlayItem.CreateText(new TextStyle(text), S(start), S(seconds));

    // ------------------------------------------------------------ elemento

    [Fact]
    public void A_text_starts_low_and_centred_like_a_lower_third()
    {
        var item = Title(0, 3);

        Assert.Equal(OverlayKind.Text, item.Kind);
        Assert.Equal(0.5, item.Transform.CenterX);
        Assert.Equal(0.85, item.Transform.CenterY);
        Assert.Equal(1, item.Transform.Opacity);
    }

    [Fact]
    public void An_item_below_the_minimum_duration_or_before_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Title(0, 0.05));
        Assert.Throws<ArgumentOutOfRangeException>(() => Title(-1, 3));
    }

    [Fact]
    public void An_item_is_visible_from_its_start_up_to_but_not_including_its_end()
    {
        var item = Title(2, 3);

        Assert.False(item.IsVisibleAt(S(1.99)));
        Assert.True(item.IsVisibleAt(S(2)));
        Assert.True(item.IsVisibleAt(S(4.99)));
        Assert.False(item.IsVisibleAt(S(5)));
    }

    [Fact]
    public void The_transform_is_clamped_to_its_valid_range()
    {
        var t = new OverlayTransform(-2, 3, 9, 4).Clamped();

        Assert.Equal(0, t.CenterX);
        Assert.Equal(1, t.CenterY);
        Assert.Equal(1, t.Width);
        Assert.Equal(1, t.Opacity);
    }

    [Fact]
    public void An_items_two_fades_together_cannot_exceed_how_long_it_is_shown()
    {
        var item = Title(0, 5);
        item.FadeIn = S(-1);
        Assert.Equal(TimeSpan.Zero, item.FadeIn);

        item.FadeIn = S(4);
        item.FadeOut = S(4);

        Assert.Equal(S(4), item.FadeIn);
        Assert.Equal(S(1), item.FadeOut);
    }

    [Fact]
    public void Slicing_keeps_a_fade_only_on_the_edge_the_slice_actually_reaches()
    {
        var item = Title(2, 6);
        item.FadeIn = S(1);
        item.FadeOut = S(1);

        // Llega hasta los dos bordes reales (2 a 8): conserva los dos fundidos, con los
        // tiempos medidos desde el inicio del trozo.
        var whole = item.Slice(S(0), S(10))!;
        Assert.Equal(S(1), whole.FadeIn);
        Assert.Equal(S(1), whole.FadeOut);

        // Un trozo interno, que no llega a ninguno de los dos bordes reales, pierde los dos.
        var middle = item.Slice(S(4), S(6))!;
        Assert.Equal(TimeSpan.Zero, middle.FadeIn);
        Assert.Equal(TimeSpan.Zero, middle.FadeOut);

        // Llega solo al borde de salida (hasta el 8, pero empieza a cortar el trozo en el 5).
        var tail = item.Slice(S(5), S(10))!;
        Assert.Equal(TimeSpan.Zero, tail.FadeIn);
        Assert.Equal(S(1), tail.FadeOut);
    }

    [Fact]
    public void Setting_an_items_fades_is_undoable()
    {
        var item = Title(0, 5);
        item.FadeIn = S(2);
        var history = new UndoHistory();

        history.Do(new SetOverlayFadeCommand(item, S(1), S(3)));
        Assert.Equal(S(1), item.FadeIn);
        Assert.Equal(S(3), item.FadeOut);

        history.Undo();
        Assert.Equal(S(2), item.FadeIn);
        Assert.Equal(TimeSpan.Zero, item.FadeOut);

        history.Redo();
        Assert.Equal(S(1), item.FadeIn);
        Assert.Equal(S(3), item.FadeOut);
    }

    // --------------------------------------------------------------- pista

    [Fact]
    public void Overlapping_items_are_refused_but_touching_ones_are_fine()
    {
        var track = new OverlayTrack("T1");

        Assert.True(track.TryAdd(Title(0, 5)));
        Assert.False(track.TryAdd(Title(4, 3)));
        Assert.True(track.TryAdd(Title(5, 3)));
        Assert.Equal(2, track.Items.Count);
    }

    [Fact]
    public void Items_stay_sorted_by_position()
    {
        var track = new OverlayTrack("T1");
        track.TryAdd(Title(10, 2, "c"));
        track.TryAdd(Title(0, 2, "a"));
        track.TryAdd(Title(5, 2, "b"));

        Assert.Equal(["a", "b", "c"], track.Items.Select(i => i.Text!.Content));
    }

    [Fact]
    public void Moving_and_resizing_respect_neighbours_and_the_lock()
    {
        var track = new OverlayTrack("T1");
        var a = Title(0, 3);
        var b = Title(5, 3);
        track.TryAdd(a);
        track.TryAdd(b);

        Assert.False(track.TryMove(a, S(4)));            // pisaría a b
        Assert.True(track.TryMove(a, S(1)));
        Assert.False(track.TryPlace(a, S(1), S(6)));     // alargarlo choca
        Assert.True(track.TryPlace(a, S(1), S(4)));      // hasta tocar a b

        track.IsLocked = true;
        Assert.False(track.TryMove(a, S(0)));
        Assert.False(track.Remove(a));
    }

    // ----------------------------------------------------------- secuencia

    [Fact]
    public void A_new_layer_goes_on_top_and_names_use_the_first_free_number()
    {
        var sequence = new EditSequence();
        var first = sequence.AddOverlayTrack();
        var second = sequence.AddOverlayTrack();

        Assert.Equal("T1", first.Name);
        Assert.Equal("T2", second.Name);
        Assert.Same(second, sequence.OverlayTracks[0]);     // la última creada queda delante

        sequence.RemoveOverlayTrack(first);
        Assert.Equal("T1", sequence.AddOverlayTrack().Name);
    }

    [Fact]
    public void FindOrCreate_reuses_a_layer_with_room_and_makes_another_when_full()
    {
        var sequence = new EditSequence();
        var layer = sequence.AddOverlayTrack();
        layer.TryAdd(Title(0, 5));

        Assert.Same(layer, sequence.FindOrCreateOverlayTrackFor(S(6), S(2)));

        var other = sequence.FindOrCreateOverlayTrackFor(S(2), S(2));
        Assert.NotSame(layer, other);
        Assert.Equal(2, sequence.OverlayTracks.Count);
    }

    [Fact]
    public void An_overlay_that_outlasts_the_video_extends_the_sequence()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(
            new MediaInfo("v.mp4", S(10), 1920, 1080, 30, "h264", true), S(0), S(10)));
        sequence.AddOverlayTrack().TryAdd(Title(8, 5));

        Assert.Equal(S(13), sequence.Duration);
    }

    // ------------------------------------------------------------- comandos

    [Fact]
    public void Adding_an_item_can_be_undone_and_redone()
    {
        var sequence = new EditSequence();
        var layer = sequence.AddOverlayTrack();
        var item = Title(1, 3);
        var history = new UndoHistory();

        history.Do(new AddOverlayItemCommand(layer, item));
        Assert.Single(layer.Items);

        history.Undo();
        Assert.Empty(layer.Items);

        history.Redo();
        Assert.Same(item, layer.Items[0]);
    }

    [Fact]
    public void A_rejected_add_leaves_nothing_to_undo_wrongly()
    {
        var layer = new OverlayTrack("T1");
        var existing = Title(0, 5);
        layer.TryAdd(existing);

        var command = new AddOverlayItemCommand(layer, Title(2, 2));
        command.Execute();
        Assert.False(command.Added);

        command.Undo();
        Assert.Same(existing, Assert.Single(layer.Items));
    }

    [Fact]
    public void Placing_an_item_is_undone_to_the_exact_previous_placement()
    {
        var layer = new OverlayTrack("T1");
        var item = Title(2, 3);
        layer.TryAdd(item);
        var history = new UndoHistory();

        history.Do(new PlaceOverlayItemCommand(layer, item, S(6), S(1)));
        Assert.Equal(S(6), item.Start);
        Assert.Equal(S(1), item.Duration);

        history.Undo();
        Assert.Equal(S(2), item.Start);
        Assert.Equal(S(3), item.Duration);
    }

    [Fact]
    public void Changing_the_look_can_be_undone_including_the_text()
    {
        var item = Title(0, 3, "antes");
        var history = new UndoHistory();

        history.Do(new SetOverlayLookCommand(
            item, new OverlayTransform(0.2, 0.3, 0.25, 0.5), new TextStyle("después", 0.2, "#FF0000")));

        Assert.Equal("después", item.Text!.Content);
        Assert.Equal(0.5, item.Transform.Opacity);

        history.Undo();

        Assert.Equal("antes", item.Text!.Content);
        Assert.Equal(0.85, item.Transform.CenterY);
    }

    [Fact]
    public void The_look_command_clamps_out_of_range_values()
    {
        var item = Title(0, 3);

        new SetOverlayLookCommand(item, new OverlayTransform(5, -5, 0, 9), new TextStyle("x", 9)).Execute();

        Assert.Equal(1, item.Transform.CenterX);
        Assert.Equal(0, item.Transform.CenterY);
        Assert.Equal(1, item.Transform.Opacity);
        Assert.Equal(TextStyle.MaximumSize, item.Text!.Size);
    }

    [Fact]
    public void Removing_a_layer_restores_its_position_and_content_on_undo()
    {
        var sequence = new EditSequence();
        var bottom = sequence.AddOverlayTrack();
        var top = sequence.AddOverlayTrack();
        top.TryAdd(Title(0, 2));
        var history = new UndoHistory();

        history.Do(new RemoveOverlayTrackCommand(sequence, top));
        Assert.Single(sequence.OverlayTracks);

        history.Undo();

        Assert.Same(top, sequence.OverlayTracks[0]);
        Assert.Same(bottom, sequence.OverlayTracks[1]);
        Assert.Single(top.Items);
    }
}

public sealed class OverlayProjectTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-overlay-project-");

    public void Dispose() => _workspace.DeleteWithRetry();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private string ProjectPath() => Path.Combine(_workspace.FullName, "p.editflow");

    [Fact]
    public async Task Text_and_image_overlays_survive_saving_and_reopening()
    {
        var logo = Path.Combine(_workspace.FullName, "logo.png");
        await File.WriteAllBytesAsync(logo, [1, 2, 3]);

        var project = new EditProject();
        var layer = project.Sequence.AddOverlayTrack("Títulos");
        var text = OverlayItem.CreateText(new TextStyle("Hola\nmundo", 0.12, "#FFCC00", Bold: false, Italic: true, Shadow: false), S(1), S(4));
        text.Transform = new OverlayTransform(0.3, 0.2, 0.25, 0.6);
        layer.TryAdd(text);

        var branding = project.Sequence.AddOverlayTrack("Marca");
        var image = OverlayItem.CreateImage(logo, 2.5, S(0), S(10));
        image.Transform = new OverlayTransform(0.9, 0.1, 0.15, 0.8);
        branding.TryAdd(image);
        branding.IsHidden = true;

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None)).Project;

        // Orden de capas conservado: la última creada iba delante.
        Assert.Equal(["Marca", "Títulos"], loaded.Sequence.OverlayTracks.Select(t => t.Name));
        Assert.True(loaded.Sequence.OverlayTracks[0].IsHidden);

        var loadedText = loaded.Sequence.OverlayTracks[1].Items[0];
        Assert.Equal("Hola\nmundo", loadedText.Text!.Content);
        Assert.Equal(0.12, loadedText.Text.Size);
        Assert.Equal("#FFCC00", loadedText.Text.Color);
        Assert.False(loadedText.Text.Bold);
        Assert.True(loadedText.Text.Italic);
        Assert.False(loadedText.Text.Shadow);
        Assert.Equal(S(1), loadedText.Start);
        Assert.Equal(S(4), loadedText.Duration);
        Assert.Equal(new OverlayTransform(0.3, 0.2, 0.25, 0.6), loadedText.Transform);

        var loadedImage = loaded.Sequence.OverlayTracks[0].Items[0];
        Assert.Equal(OverlayKind.Image, loadedImage.Kind);
        Assert.Equal(logo, loadedImage.ImagePath);
        Assert.Equal(2.5, loadedImage.AspectRatio);
    }

    [Fact]
    public async Task A_missing_image_is_reported_and_skipped_instead_of_failing_the_project()
    {
        var logo = Path.Combine(_workspace.FullName, "logo.png");
        await File.WriteAllBytesAsync(logo, [1]);

        var project = new EditProject();
        project.Sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateImage(logo, 1, S(0), S(3)));
        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);

        File.Delete(logo);
        var result = await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None);

        Assert.True(result.HasMissingMedia);
        Assert.Empty(Assert.Single(result.Project.Sequence.OverlayTracks).Items);
    }

    [Fact]
    public async Task A_project_from_before_overlays_opens_without_any()
    {
        // Versión 2: sin el campo overlayTracks.
        await File.WriteAllTextAsync(
            ProjectPath(),
            """{ "version": 2, "media": [], "clips": [], "audioTracks": [] }""");

        var loaded = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None)).Project;

        Assert.Empty(loaded.Sequence.OverlayTracks);
    }

    [Fact]
    public async Task Out_of_range_values_in_a_hand_edited_file_are_repaired()
    {
        await File.WriteAllTextAsync(
            ProjectPath(),
            """
            { "version": 3, "media": [], "clips": [], "audioTracks": [],
              "overlayTracks": [ { "name": "T1", "items": [
                { "kind": "text", "start": "-00:00:05", "duration": "00:00:00", "text": "x",
                  "textSize": 50, "centerX": 9, "opacity": -1 } ] } ] }
            """);

        var item = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None))
            .Project.Sequence.OverlayTracks[0].Items[0];

        Assert.Equal(TimeSpan.Zero, item.Start);
        Assert.Equal(OverlayItem.MinimumDuration, item.Duration);
        Assert.Equal(TextStyle.MaximumSize, item.Text!.Size);
        Assert.Equal(1, item.Transform.CenterX);
        Assert.Equal(0, item.Transform.Opacity);
    }
}

public class OverlaySnappingTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Overlay_edges_are_snap_points_for_everything_else_but_not_for_the_item_being_dragged()
    {
        var sequence = new EditSequence();
        var layer = sequence.AddOverlayTrack();
        var mine = OverlayItem.CreateText(new TextStyle("a"), S(10), S(2));
        var other = OverlayItem.CreateText(new TextStyle("b"), S(20), S(3));
        layer.TryAdd(mine);
        layer.TryAdd(other);

        var forOverlay = Snapping.PointsForOverlay(sequence, S(1), mine);
        Assert.Contains(S(20), forOverlay);
        Assert.Contains(S(23), forOverlay);
        Assert.DoesNotContain(S(10), forOverlay);
        Assert.DoesNotContain(S(12), forOverlay);

        // Un clip de audio que se arrastra sí puede imantarse a los bordes de un título.
        var forAudio = Snapping.PointsFor(sequence, S(1), (AudioClip?)null);
        Assert.Contains(S(10), forAudio);
        Assert.Contains(S(23), forAudio);
    }
}
