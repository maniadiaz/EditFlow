// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Projects;

public class MediaLibraryTests
{
    private static MediaInfo Media(string path = "a.mp4", double seconds = 10, bool offline = false) =>
        new(path, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", HasAudio: true)
        {
            IsOffline = offline,
        };

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // ------------------------------------------------------------- carpetas

    [Fact]
    public void Everything_starts_in_the_root()
    {
        var library = new MediaLibrary();
        var media = Media();

        Assert.True(library.Root.IsRoot);
        Assert.Same(library.Root, library.BinOf(media));
        Assert.Empty(library.Root.Children);
    }

    [Fact]
    public void A_bin_can_hold_another_and_knows_its_path()
    {
        var library = new MediaLibrary();
        var camera = library.CreateBin("Cámara A");
        var takes = library.CreateBin("Tomas", camera);

        Assert.Equal("Cámara A", camera.DisplayPath);
        Assert.Equal("Cámara A / Tomas", takes.DisplayPath);
        Assert.Equal([library.Root, camera, takes], library.AllBins());
    }

    [Fact]
    public void A_bin_refuses_to_go_inside_itself()
    {
        // Arrastrar una carpeta sobre una de sus hijas dejaría el árbol con un ciclo, y recorrerlo
        // colgaría la aplicación.
        var library = new MediaLibrary();
        var outer = library.CreateBin("Fuera");
        var inner = library.CreateBin("Dentro", outer);

        Assert.False(library.MoveBin(outer, inner));
        Assert.False(library.MoveBin(outer, outer));
        Assert.Same(library.Root, outer.Parent);
    }

    [Fact]
    public void Deleting_a_bin_keeps_what_was_inside_it()
    {
        // Que una operación de organización pudiera tirar material sería una trampa.
        var library = new MediaLibrary();
        var media = Media();
        var outer = library.CreateBin("Fuera");
        var inner = library.CreateBin("Dentro", outer);
        library.MoveToBin(media, outer);

        Assert.True(library.RemoveBin(outer));

        Assert.Same(library.Root, inner.Parent);
        Assert.Same(library.Root, library.BinOf(media));
    }

    [Fact]
    public void The_root_cannot_be_deleted()
    {
        var library = new MediaLibrary();
        Assert.False(library.RemoveBin(library.Root));
    }

    // ------------------------------------------------------------ etiquetas

    [Fact]
    public void A_medium_can_be_marked_and_unmarked()
    {
        var library = new MediaLibrary();
        var media = Media();

        Assert.Equal(MediaLabel.None, library.LabelOf(media));

        library.SetLabel(media, MediaLabel.Blue);
        Assert.Equal(MediaLabel.Blue, library.LabelOf(media));

        library.SetLabel(media, MediaLabel.None);
        Assert.Equal(MediaLabel.None, library.LabelOf(media));
    }

    [Fact]
    public void Organising_and_undoing_returns_things_where_they_were()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media());
        var history = new UndoHistory();

        var create = new CreateBinCommand(project.Library, "Cámara A");
        history.Do(create);
        var bin = create.Result!;

        history.Do(new MoveMediaCommand(project.Library, media, bin));
        history.Do(new SetMediaLabelCommand(project.Library, media, MediaLabel.Green));

        Assert.Same(bin, project.Library.BinOf(media));
        Assert.Equal(MediaLabel.Green, project.Library.LabelOf(media));

        history.Undo();
        Assert.Equal(MediaLabel.None, project.Library.LabelOf(media));

        history.Undo();
        Assert.Same(project.Library.Root, project.Library.BinOf(media));

        history.Undo();
        Assert.Empty(project.Library.Root.Children);

        // Rehacer devuelve la misma carpeta, no una nueva: lo que se moviera a ella la sigue.
        history.Redo();
        Assert.Same(bin, project.Library.Root.Children.Single());
    }

    // ------------------------------------------------- reconectar y sustituir

    private static EditProject ProjectUsing(MediaInfo media)
    {
        var project = new EditProject();
        project.AddMedia(media);
        project.Timeline.Append(new Clip(media, TimeSpan.Zero, S(6)));

        var audioTrack = project.Sequence.AddAudioTrack("A1");
        Assert.True(audioTrack.TryAdd(new AudioClip(media, TimeSpan.Zero, S(5), TimeSpan.Zero)));

        var layer = project.Sequence.AddOverlayTrack("V2");
        Assert.True(layer.TryAdd(OverlayItem.CreateVideo(media, S(1), TimeSpan.Zero, S(4))));

        return project;
    }

    [Fact]
    public void Relinking_swaps_the_file_everywhere_it_was_used()
    {
        var missing = Media("viejo.mp4", offline: true);
        var project = ProjectUsing(missing);
        var found = Media("nuevo.mp4");

        var command = new ReplaceMediaCommand(project, missing, found);
        command.Execute();

        Assert.Equal(3, command.AffectedCount);
        Assert.False(command.Trimmed);
        Assert.Equal("nuevo.mp4", project.Timeline.Clips.Single().Source.Path);
        Assert.Equal("nuevo.mp4", project.Sequence.AudioTracks.Single().Clips.Single().Source.Path);
        Assert.Equal("nuevo.mp4", project.Sequence.OverlayTracks.Single().Items.Single().Media!.Path);
        Assert.Equal("nuevo.mp4", project.Media.Single().Path);
        Assert.False(project.HasOfflineMedia);
    }

    [Fact]
    public void Relinking_keeps_every_cut_exactly_where_it_was()
    {
        // Es el sentido de todo esto: reconectar devuelve la imagen sin rehacer el montaje.
        var missing = Media("viejo.mp4", offline: true);
        var project = ProjectUsing(missing);

        new ReplaceMediaCommand(project, missing, Media("nuevo.mp4")).Execute();

        var clip = project.Timeline.Clips.Single();
        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(S(6), clip.SourceOut);
        Assert.Equal(S(1), project.Sequence.OverlayTracks.Single().Items.Single().SourceIn);
    }

    [Fact]
    public void A_shorter_replacement_trims_what_no_longer_fits_and_says_so()
    {
        var original = Media("largo.mp4", seconds: 10);
        var project = ProjectUsing(original);

        var command = new ReplaceMediaCommand(project, original, Media("corto.mp4", seconds: 3));
        command.Execute();

        Assert.True(command.Trimmed);

        var clip = project.Timeline.Clips.Single();
        Assert.True(clip.SourceOut <= S(3), $"El clip se salió del archivo nuevo: {clip.SourceOut}.");
    }

    [Fact]
    public void Undoing_a_shorter_replacement_restores_the_ranges_it_had_to_trim()
    {
        // Acotar pierde información: sin anotar los intervalos, deshacer no podría recuperarla.
        var original = Media("largo.mp4", seconds: 10);
        var project = ProjectUsing(original);
        var history = new UndoHistory();

        history.Do(new ReplaceMediaCommand(project, original, Media("corto.mp4", seconds: 3)));
        history.Undo();

        var clip = project.Timeline.Clips.Single();
        Assert.Equal("largo.mp4", clip.Source.Path);
        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(S(6), clip.SourceOut);
        Assert.Equal(S(5), project.Sequence.AudioTracks.Single().Clips.Single().SourceOut);
        Assert.Equal(S(1), project.Sequence.OverlayTracks.Single().Items.Single().SourceIn);
    }

    [Fact]
    public void Relinking_carries_the_folder_and_the_label_along()
    {
        // Todo se guarda por ruta: sin trasladarlo, reconectar devolvería el medio a la raíz sin
        // marcar, deshaciendo la organización justo cuando más molesta.
        var missing = Media("viejo.mp4", offline: true);
        var project = ProjectUsing(missing);

        var bin = project.Library.CreateBin("Cámara A");
        project.Library.MoveToBin(missing, bin);
        project.Library.SetLabel(missing, MediaLabel.Purple);

        var found = Media("nuevo.mp4");
        new ReplaceMediaCommand(project, missing, found).Execute();

        Assert.Same(bin, project.Library.BinOf(found));
        Assert.Equal(MediaLabel.Purple, project.Library.LabelOf(found));
    }

    [Fact]
    public void Relinking_to_a_file_that_was_already_imported_does_not_leave_a_duplicate()
    {
        var missing = Media("viejo.mp4", offline: true);
        var project = ProjectUsing(missing);
        var other = project.AddMedia(Media("nuevo.mp4"));

        new ReplaceMediaCommand(project, missing, other).Execute();

        Assert.Single(project.Media);
        Assert.Equal("nuevo.mp4", project.Media[0].Path);
    }

    // -------------------------------------------------------- la exportación

    [Fact]
    public void Exporting_with_a_missing_file_says_which_one_instead_of_failing_halfway()
    {
        var project = ProjectUsing(Media("se-movio.mp4", offline: true));

        var error = Assert.Throws<ArgumentException>(() => FilterGraphBuilder.Build(
            project.Sequence,
            new ExportSettings
            {
                OutputPath = "out.mp4",
                Resolution = VideoResolution.P1080,
                EncoderName = "libx264",
                FrameRate = 30,
            }));

        Assert.Contains("se-movio.mp4", error.Message, StringComparison.Ordinal);
        Assert.Contains("Reconéctalos", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exporting_works_again_once_the_file_is_relinked()
    {
        var missing = Media("se-movio.mp4", offline: true);
        var project = ProjectUsing(missing);
        new ReplaceMediaCommand(project, missing, Media("encontrado.mp4")).Execute();

        var plan = FilterGraphBuilder.Build(
            project.Sequence,
            new ExportSettings
            {
                OutputPath = "out.mp4",
                Resolution = VideoResolution.P1080,
                EncoderName = "libx264",
                FrameRate = 30,
            });

        Assert.Contains("encontrado.mp4", plan.InputArguments);
    }
}
