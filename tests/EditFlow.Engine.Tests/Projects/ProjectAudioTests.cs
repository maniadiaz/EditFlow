// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Tests.Projects;

public class ProjectAudioTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-audio-project-");
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

    private MediaInfo FakeMedia(string name, double seconds = 20, bool audio = true)
    {
        var path = Path.Combine(_workspace.FullName, name);
        File.WriteAllText(path, "no es un video de verdad, pero existe");
        return new MediaInfo(path, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", audio);
    }

    private string ProjectPath(string name = "p.editflow") => Path.Combine(_workspace.FullName, name);

    [Fact]
    public async Task Audio_tracks_survive_saving_and_reopening()
    {
        var project = new EditProject();
        var video = project.AddMedia(FakeMedia("v.mp4", 20));
        var music = project.AddMedia(FakeMedia("musica.mp3", 120));

        project.Timeline.Append(new Clip(video));

        var track = project.Sequence.AddAudioTrack("Música");
        track.GainDb = -3;
        track.TryAdd(new AudioClip(music, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(7))
        {
            GainDb = -8,
            FadeIn = TimeSpan.FromSeconds(2),
            FadeOut = TimeSpan.FromSeconds(3),
        });

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None)).Project;

        var loadedTrack = Assert.Single(loaded.Sequence.AudioTracks);
        Assert.Equal("Música", loadedTrack.Name);
        Assert.Equal(-3, loadedTrack.GainDb);

        var clip = Assert.Single(loadedTrack.Clips);
        Assert.Equal(TimeSpan.FromSeconds(7), clip.TimelineStart);
        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(20), clip.Duration);
        Assert.Equal(-8, clip.GainDb);
        Assert.Equal(TimeSpan.FromSeconds(2), clip.FadeIn);
        Assert.Equal(TimeSpan.FromSeconds(3), clip.FadeOut);
    }

    [Fact]
    public async Task Detached_audio_state_survives_reopening()
    {
        // Si se perdiera este estado, al reabrir el audio sonaría dos veces: una desde el
        // video y otra desde su pista.
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("v.mp4", 10));
        var clip = new Clip(media);
        project.Timeline.Append(clip);
        new DetachAudioCommand(project.Sequence, clip).Execute();

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None)).Project;

        Assert.True(loaded.Timeline.Clips[0].IsAudioDetached);
        Assert.Single(loaded.Sequence.AudioTracks);
        Assert.Single(loaded.Sequence.AudioTracks[0].Clips);
    }

    [Fact]
    public async Task Track_order_is_preserved()
    {
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("v.mp4", 10));
        project.Sequence.AddAudioTrack("Primera");
        project.Sequence.AddAudioTrack("Segunda");
        project.Sequence.AddAudioTrack("Tercera");
        project.Sequence.MoveAudioTrack(project.Sequence.AudioTracks[2], 0);
        project.Timeline.Append(new Clip(media));

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None)).Project;

        Assert.Equal(["Tercera", "Primera", "Segunda"], loaded.Sequence.AudioTracks.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task Track_flags_are_preserved()
    {
        var project = new EditProject();
        project.AddMedia(FakeMedia("v.mp4"));
        var track = project.Sequence.AddAudioTrack();
        track.IsMuted = true;
        track.IsSolo = true;
        track.IsLocked = true;

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None)).Project;

        var reopened = Assert.Single(loaded.Sequence.AudioTracks);
        Assert.True(reopened.IsMuted);
        Assert.True(reopened.IsSolo);
        Assert.True(reopened.IsLocked);
    }

    [Fact]
    public async Task A_version_1_project_still_opens()
    {
        // Es el caso real: un proyecto guardado antes de existir las pistas de audio no
        // tiene ese campo. Añadir un campo no debe invalidar los archivos anteriores.
        var media = FakeMedia("v.mp4", 10);
        var json = $$"""
        {
          "version": 1,
          "media": [ { "id": "m0", "path": {{System.Text.Json.JsonSerializer.Serialize(media.Path)}},
                       "duration": "00:00:10", "width": 1920, "height": 1080,
                       "frameRate": 30, "codec": "h264", "hasAudio": true, "rotation": 0 } ],
          "clips": [ { "mediaId": "m0", "sourceIn": "00:00:00", "sourceOut": "00:00:10" } ]
        }
        """;
        await File.WriteAllTextAsync(ProjectPath("viejo.editflow"), json);

        var loaded = await ProjectSerializer.LoadAsync(ProjectPath("viejo.editflow"), CancellationToken.None);

        Assert.Single(loaded.Project.Timeline.Clips);
        Assert.False(loaded.Project.Timeline.Clips[0].IsAudioDetached);
        Assert.Empty(loaded.Project.Sequence.AudioTracks);
    }

    [Fact]
    public async Task Audio_from_a_missing_file_is_skipped_and_reported()
    {
        var project = new EditProject();
        var video = project.AddMedia(FakeMedia("v.mp4", 10));
        var music = project.AddMedia(FakeMedia("perdida.mp3", 60));
        project.Timeline.Append(new Clip(video));
        project.Sequence.AddAudioTrack().TryAdd(
            new AudioClip(music, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.Zero));

        await ProjectSerializer.SaveAsync(project, ProjectPath(), CancellationToken.None);
        File.Delete(music.Path);

        var loaded = await ProjectSerializer.LoadAsync(ProjectPath(), CancellationToken.None);

        Assert.True(loaded.HasMissingMedia);
        Assert.Single(loaded.Project.Timeline.Clips);
        Assert.Empty(loaded.Project.Sequence.AudioTracks[0].Clips);
    }

    [Fact]
    public async Task One_file_used_by_video_and_audio_is_stored_once()
    {
        var project = new EditProject();
        var media = project.AddMedia(FakeMedia("v.mp4", 10));
        var clip = new Clip(media);
        project.Timeline.Append(clip);
        new DetachAudioCommand(project.Sequence, clip).Execute();

        var file = ProjectSerializer.ToFile(project, ProjectPath());

        Assert.Single(file.Media);
        Assert.Equal(file.Clips[0].MediaId, file.AudioTracks[0].Clips[0].MediaId);
    }

    [Fact]
    public void The_format_version_moved_forward()
    {
        Assert.Equal(3, ProjectSerializer.CurrentVersion);
    }
}
