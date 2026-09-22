// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Tests.Projects;

public class ProjectSerializerTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-project-");
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _workspace.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Crea un archivo de video simulado para que exista en disco al resolverlo.</summary>
    private MediaInfo FakeMedia(string name, double seconds = 10, string? subdirectory = null)
    {
        var directory = subdirectory is null
            ? _workspace.FullName
            : Directory.CreateDirectory(Path.Combine(_workspace.FullName, subdirectory)).FullName;

        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "no es un video de verdad, pero existe");

        return new MediaInfo(path, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);
    }

    private string ProjectPath(string name = "proyecto.editflow") =>
        Path.Combine(_workspace.FullName, name);

    [Fact]
    public async Task A_saved_project_reopens_with_the_same_montage()
    {
        var project = new EditProject();
        var a = project.AddMedia(FakeMedia("a.mp4", 10));
        var b = project.AddMedia(FakeMedia("b.mp4", 6));

        project.Timeline.Append(new Clip(a, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7)));
        project.Timeline.Append(new Clip(b));

        var path = ProjectPath();
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);

        var loaded = await ProjectSerializer.LoadAsync(path, CancellationToken.None);

        Assert.False(loaded.HasMissingMedia);
        Assert.Equal(2, loaded.Project.Timeline.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(11), loaded.Project.Timeline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), loaded.Project.Timeline.Clips[0].SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(7), loaded.Project.Timeline.Clips[0].SourceOut);
    }

    [Fact]
    public async Task A_clips_transition_survives_a_save_and_reload()
    {
        var project = new EditProject();
        var a = project.AddMedia(FakeMedia("a.mp4", 10));
        var b = project.AddMedia(FakeMedia("b.mp4", 10));

        project.Timeline.Append(new Clip(a));
        project.Timeline.Append(new Clip(b)
        {
            TransitionIn = new Transition(TransitionKind.WipeLeft, TimeSpan.FromSeconds(1.5)),
        });

        var path = ProjectPath();
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);

        var loaded = await ProjectSerializer.LoadAsync(path, CancellationToken.None);

        var incoming = loaded.Project.Timeline.Clips[1];
        Assert.Equal(TransitionKind.WipeLeft, incoming.TransitionIn.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1.5), incoming.TransitionIn.Duration);
    }

    [Fact]
    public async Task A_clip_without_a_transition_saves_nothing_for_it()
    {
        // Igual que el ajuste de color: si no hay nada que guardar, no se llena el archivo
        // de proyecto con ceros. Es lo que permite que los proyectos de antes de que las
        // transiciones existieran (versión 5 e inferior) se abran exactamente igual.
        var project = new EditProject();
        project.Timeline.Append(new Clip(project.AddMedia(FakeMedia("a.mp4"))));

        var file = ProjectSerializer.ToFile(project, ProjectPath());

        Assert.Null(file.Clips[0].TransitionIn);
    }

    [Fact]
    public async Task A_project_from_before_transitions_existed_still_opens()
    {
        // Los proyectos de la versión 5 e inferiores no tienen el campo 'transitionIn' en
        // absoluto. Deben seguir abriendo, con los clips sin transición.
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("a.mp4", 10));
        project.Timeline.Append(new Clip(media));
        project.Timeline.Append(new Clip(media, TimeSpan.Zero, TimeSpan.FromSeconds(5)));

        var path = ProjectPath();
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);

        var json = await File.ReadAllTextAsync(path);
        json = json.Replace("\"version\": 6", "\"version\": 5", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        var loaded = await ProjectSerializer.LoadAsync(path, CancellationToken.None);

        Assert.Equal(2, loaded.Project.Timeline.Clips.Count);
        Assert.All(loaded.Project.Timeline.Clips, c => Assert.True(c.TransitionIn.IsNone));
    }

    [Fact]
    public async Task Several_clips_from_one_file_share_a_single_media_entry()
    {
        // Cortar un video en trozos produce varios clips del mismo archivo. Repetir sus
        // metadatos en cada clip multiplicaría el tamaño del proyecto y abriría la puerta
        // a que las copias dejaran de coincidir.
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("largo.mp4", 30));

        project.Timeline.Append(new Clip(media, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        project.Timeline.Append(new Clip(media, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)));
        project.Timeline.Append(new Clip(media, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25)));

        var file = ProjectSerializer.ToFile(project, ProjectPath());

        Assert.Single(file.Media);
        Assert.Equal(3, file.Clips.Count);
        Assert.All(file.Clips, c => Assert.Equal(file.Media[0].Id, c.MediaId));
    }

    [Fact]
    public async Task Importing_the_same_file_twice_does_not_duplicate_it()
    {
        var project = new EditProject();
        var first = project.AddMedia(FakeMedia("repetido.mp4"));
        var second = project.AddMedia(new MediaInfo(
            first.Path, first.Duration, 1920, 1080, 30, "h264", true));

        Assert.Single(project.Media);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_project_moved_with_its_videos_still_opens()
    {
        // El caso real: copiar la carpeta entera a otro disco u otro equipo. La ruta
        // absoluta guardada deja de existir, pero la relativa sigue siendo válida.
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("clip.mp4", 8, subdirectory: "material"));
        project.Timeline.Append(new Clip(media));

        var originalPath = ProjectPath();
        await ProjectSerializer.SaveAsync(project, originalPath, CancellationToken.None);

        // Simular el traslado: mover proyecto y material juntos a otra carpeta.
        var moved = Directory.CreateDirectory(Path.Combine(_workspace.FullName, "mudanza"));
        var movedMaterial = Directory.CreateDirectory(Path.Combine(moved.FullName, "material"));
        File.Move(media.Path, Path.Combine(movedMaterial.FullName, "clip.mp4"));
        var movedProject = Path.Combine(moved.FullName, "proyecto.editflow");
        File.Move(originalPath, movedProject);

        var loaded = await ProjectSerializer.LoadAsync(movedProject, CancellationToken.None);

        Assert.False(loaded.HasMissingMedia);
        Assert.Single(loaded.Project.Timeline.Clips);
        Assert.StartsWith(moved.FullName, loaded.Project.Timeline.Clips[0].Source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_video_is_reported_instead_of_failing()
    {
        // Borrar un archivo no debe impedir abrir el proyecto: el usuario necesita verlo
        // para saber qué le falta y volver a vincularlo.
        var project = new EditProject();
        var present = project.AddMedia(FakeMedia("esta.mp4"));
        var absent = project.AddMedia(FakeMedia("desaparecido.mp4"));

        project.Timeline.Append(new Clip(present));
        project.Timeline.Append(new Clip(absent));

        var path = ProjectPath();
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        File.Delete(absent.Path);

        var loaded = await ProjectSerializer.LoadAsync(path, CancellationToken.None);

        Assert.True(loaded.HasMissingMedia);
        Assert.Single(loaded.MissingMedia);
        Assert.Contains("desaparecido.mp4", loaded.MissingMedia[0], StringComparison.Ordinal);
        Assert.Single(loaded.Project.Timeline.Clips);
    }

    [Fact]
    public async Task A_newer_format_is_refused_rather_than_half_understood()
    {
        // Interpretar a medias un formato posterior haría que guardar encima perdiera
        // en silencio lo que esa versión añadiera.
        var path = ProjectPath("futuro.editflow");
        await File.WriteAllTextAsync(path,
            """{ "version": 999, "media": [], "clips": [] }""");

        var error = await Assert.ThrowsAsync<ProjectFormatException>(
            () => ProjectSerializer.LoadAsync(path, CancellationToken.None));

        Assert.Contains("999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_file_reports_which_file_failed()
    {
        var path = ProjectPath("roto.editflow");
        await File.WriteAllTextAsync(path, "esto no es JSON {{{");

        var error = await Assert.ThrowsAsync<ProjectFormatException>(
            () => ProjectSerializer.LoadAsync(path, CancellationToken.None));

        Assert.Contains("roto.editflow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_file_that_does_not_exist_says_so()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ProjectSerializer.LoadAsync(ProjectPath("no-existe.editflow"), CancellationToken.None));
    }

    [Fact]
    public async Task An_interrupted_save_does_not_destroy_the_previous_project()
    {
        // El guardado escribe a un temporal y reemplaza al final. Aquí se comprueba lo
        // observable: tras guardar dos veces no queda ningún .tmp abandonado y el
        // contenido es el de la última versión.
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("v.mp4", 12));
        project.Timeline.Append(new Clip(media));

        var path = ProjectPath();
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);

        project.Timeline.Append(new Clip(media, TimeSpan.Zero, TimeSpan.FromSeconds(3)));
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);

        Assert.False(File.Exists(path + ".tmp"), "quedó un archivo temporal abandonado");

        var loaded = await ProjectSerializer.LoadAsync(path, CancellationToken.None);
        Assert.Equal(2, loaded.Project.Timeline.Clips.Count);
    }

    [Fact]
    public async Task Saving_clears_the_unsaved_flag()
    {
        var project = new EditProject();
        project.AddMedia(FakeMedia("x.mp4"));

        Assert.True(project.HasUnsavedChanges);

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);

        Assert.False(project.HasUnsavedChanges);
        Assert.Equal("proyecto", project.DisplayName);
    }

    [Fact]
    public void An_unsaved_project_has_a_placeholder_name()
    {
        Assert.Equal("Proyecto sin título", new EditProject().DisplayName);
    }

    [Fact]
    public async Task Clip_ranges_beyond_the_file_are_clamped_not_rejected()
    {
        // Si el archivo origen se recortó después de guardar el proyecto, los intervalos
        // quedarían fuera de rango. Acotar conserva el montaje; rechazar lo perdería entero.
        var media = FakeMedia("recortado.mp4", 10);

        var file = new ProjectFile
        {
            Version = 1,
            Media =
            [
                new ProjectMedia
                {
                    Id = "m0", Path = media.Path, Duration = TimeSpan.FromSeconds(4),
                    Width = 1920, Height = 1080, FrameRate = 30, Codec = "h264", HasAudio = true,
                },
            ],
            Clips =
            [
                new ProjectClip
                {
                    MediaId = "m0",
                    SourceIn = TimeSpan.Zero,
                    SourceOut = TimeSpan.FromSeconds(60),
                },
            ],
        };

        var loaded = ProjectSerializer.FromFile(file, ProjectPath());

        Assert.Single(loaded.Project.Timeline.Clips);
        Assert.Equal(TimeSpan.FromSeconds(4), loaded.Project.Timeline.Clips[0].SourceOut);
    }
}
