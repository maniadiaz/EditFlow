// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Timeline;

public class AudioEffectFieldTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-audioeffect-");

    public void Dispose()
    {
        try { _workspace.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private MediaInfo Media(string name, double seconds = 20)
    {
        var path = Path.Combine(_workspace.FullName, name);
        File.WriteAllText(path, "no es un video");
        return new MediaInfo(path, S(seconds), 1920, 1080, 30, "h264", true);
    }

    // ---------------------------------------------------------------- modelo: Clip

    [Fact]
    public void A_clips_pan_is_clamped_to_the_valid_range()
    {
        var clip = new Clip(Media("a.mp4")) { Pan = 5 };
        Assert.Equal(1, clip.Pan);

        clip.Pan = -5;
        Assert.Equal(-1, clip.Pan);
    }

    [Fact]
    public void A_clips_pan_and_audio_effect_survive_cloning_and_splitting()
    {
        var clip = new Clip(Media("a.mp4", 10)) { Pan = 0.5, AudioEffect = AudioEffectKind.Denoise };

        var clone = clip.Clone();
        Assert.Equal(0.5, clone.Pan);
        Assert.Equal(AudioEffectKind.Denoise, clone.AudioEffect);

        var second = clip.SplitAt(S(5))!;
        Assert.Equal(0.5, clip.Pan);
        Assert.Equal(AudioEffectKind.Denoise, clip.AudioEffect);
        Assert.Equal(0.5, second.Pan);
        Assert.Equal(AudioEffectKind.Denoise, second.AudioEffect);
    }

    [Fact]
    public void Applying_a_clips_audio_effect_and_pan_are_undoable()
    {
        var clip = new Clip(Media("a.mp4"));
        var history = new UndoHistory();

        history.Do(new SetClipAudioEffectCommand(clip, AudioEffectKind.Compressor));
        history.Do(new SetClipPanCommand(clip, -0.8));
        Assert.Equal(AudioEffectKind.Compressor, clip.AudioEffect);
        Assert.Equal(-0.8, clip.Pan);

        history.Undo();
        Assert.Equal(0, clip.Pan);
        Assert.Equal(AudioEffectKind.Compressor, clip.AudioEffect);

        history.Undo();
        Assert.Equal(AudioEffectKind.None, clip.AudioEffect);

        history.Redo();
        history.Redo();
        Assert.Equal(AudioEffectKind.Compressor, clip.AudioEffect);
        Assert.Equal(-0.8, clip.Pan);
    }

    // ------------------------------------------------------------- modelo: AudioClip

    [Fact]
    public void An_audio_clips_pan_is_clamped_to_the_valid_range()
    {
        var clip = new AudioClip(Media("a.mp4"), TimeSpan.Zero, S(5), TimeSpan.Zero) { Pan = 2 };
        Assert.Equal(1, clip.Pan);
    }

    [Fact]
    public void Applying_an_audio_clips_effect_and_pan_are_undoable()
    {
        var clip = new AudioClip(Media("a.mp4"), TimeSpan.Zero, S(5), TimeSpan.Zero);
        var history = new UndoHistory();

        history.Do(new SetAudioEffectCommand(clip, AudioEffectKind.Reverb));
        history.Do(new SetAudioPanCommand(clip, 0.3));
        Assert.Equal(AudioEffectKind.Reverb, clip.Effect);
        Assert.Equal(0.3, clip.Pan);

        history.Undo();
        history.Undo();
        Assert.Equal(AudioEffectKind.None, clip.Effect);
        Assert.Equal(0, clip.Pan);
    }

    // ------------------------------------------------------------------- grafo

    [Fact]
    public void A_clips_audio_effect_and_pan_land_in_the_main_clip_audio_branch()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media("a.mp4", 5)) { AudioEffect = AudioEffectKind.Limiter, Pan = -0.5 });

        var graph = FilterGraphBuilder.Build(sequence, new ExportSettings
        {
            OutputPath = "o.mp4",
            Resolution = VideoResolution.P720,
            EncoderName = "libx264",
        }).FilterGraph;

        Assert.Contains("alimiter=limit=0.9,stereotools=balance_in=-0.5", graph, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- guardado

    [Fact]
    public async Task A_clips_pan_and_audio_effect_are_saved_and_an_old_project_opens_without_them()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media("a.mp4"));
        project.Timeline.Append(new Clip(media) { Pan = -0.6, AudioEffect = AudioEffectKind.Voice });

        var path = Path.Combine(_workspace.FullName, "p.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

        Assert.Equal(-0.6, loaded.Timeline.Clips[0].Pan);
        Assert.Equal(AudioEffectKind.Voice, loaded.Timeline.Clips[0].AudioEffect);

        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["version"] = 11;
        foreach (var node in document["clips"]!.AsArray())
        {
            var obj = node!.AsObject();
            obj.Remove("pan");
            obj.Remove("audioEffect");
        }

        var oldPath = Path.Combine(_workspace.FullName, "old.editflow");
        await File.WriteAllTextAsync(oldPath, document.ToJsonString());
        var reopened = (await ProjectSerializer.LoadAsync(oldPath, CancellationToken.None)).Project;

        Assert.Equal(0, reopened.Timeline.Clips[0].Pan);
        Assert.Equal(AudioEffectKind.None, reopened.Timeline.Clips[0].AudioEffect);
    }

    [Fact]
    public async Task An_audio_clips_pan_and_effect_are_saved_and_an_old_project_opens_without_them()
    {
        var project = new EditProject();
        var media = project.AddMedia(Media("a.mp4"));
        var track = project.Sequence.AddAudioTrack();
        Assert.True(track.TryAdd(new AudioClip(media, TimeSpan.Zero, S(5), TimeSpan.Zero)
        {
            Pan = 0.7,
            Effect = AudioEffectKind.Chorus,
        }));

        var path = Path.Combine(_workspace.FullName, "p2.editflow");
        await ProjectSerializer.SaveAsync(project, path, CancellationToken.None);
        var loaded = (await ProjectSerializer.LoadAsync(path, CancellationToken.None)).Project;

        var loadedClip = loaded.Sequence.AudioTracks[0].Clips[0];
        Assert.Equal(0.7, loadedClip.Pan);
        Assert.Equal(AudioEffectKind.Chorus, loadedClip.Effect);

        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["version"] = 11;
        foreach (var track2 in document["audioTracks"]!.AsArray())
        {
            foreach (var node in track2!["clips"]!.AsArray())
            {
                var obj = node!.AsObject();
                obj.Remove("pan");
                obj.Remove("effect");
            }
        }

        var oldPath = Path.Combine(_workspace.FullName, "old2.editflow");
        await File.WriteAllTextAsync(oldPath, document.ToJsonString());
        var reopened = (await ProjectSerializer.LoadAsync(oldPath, CancellationToken.None)).Project;

        var reopenedClip = reopened.Sequence.AudioTracks[0].Clips[0];
        Assert.Equal(0, reopenedClip.Pan);
        Assert.Equal(AudioEffectKind.None, reopenedClip.Effect);
    }
}
