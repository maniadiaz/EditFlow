// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Playback;
using EditFlow.Engine.PreviewCache;

namespace EditFlow.Engine.Tests.PreviewCache;

public class SplitDoesNotInvalidateTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static EditSequence OneClip(double seconds = 20)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo("C:/no-existe/a.mp4", S(seconds), 1920, 1080, 30, "h264", true)));
        return sequence;
    }

    private static PreviewCacheManager Manager(Workspace directory) =>
        new(new FFmpegTools("ffmpeg", "ffprobe", "prueba"), directory.Path);

    [Fact]
    public void Splitting_a_clip_does_not_change_any_preview_section_hash()
    {
        using var directory = new Workspace();
        using var manager = Manager(directory);
        var settings = PreviewCacheSettings.For(540, 30);

        var sequence = OneClip();
        manager.Update(sequence, settings);
        var before = manager.Sections.Select(s => s.Hash).ToArray();

        sequence.Video.SplitAt(S(7.3));      // corta en mitad de una sección
        manager.Update(sequence, settings);

        Assert.Equal(before, manager.Sections.Select(s => s.Hash).ToArray());
    }

    [Fact]
    public void Trimming_one_half_after_a_split_does_change_the_sections_it_touches()
    {
        using var directory = new Workspace();
        using var manager = Manager(directory);
        var settings = PreviewCacheSettings.For(540, 30);

        var sequence = OneClip();
        var second = sequence.Video.SplitAt(S(10))!;
        manager.Update(sequence, settings);
        var before = manager.Sections.Select(s => s.Hash).ToArray();

        second.TrimStart(S(2));               // ya no son seguidos: se nota
        manager.Update(sequence, settings);

        Assert.NotEqual(before[2], manager.Sections[2].Hash);
    }

    [Fact]
    public void The_audio_signature_ignores_a_split_but_notices_real_changes()
    {
        var sequence = OneClip();
        var before = AudioMixSignature.Compute(sequence);

        var second = sequence.Video.SplitAt(S(8))!;
        Assert.Equal(before, AudioMixSignature.Compute(sequence));

        second.AudioGainDb = -6;
        Assert.NotEqual(before, AudioMixSignature.Compute(sequence));

        second.AudioGainDb = 0;
        Assert.Equal(before, AudioMixSignature.Compute(sequence));

        second.TrimStart(S(1));
        Assert.NotEqual(before, AudioMixSignature.Compute(sequence));
    }

    [Fact]
    public void The_audio_signature_ignores_titles()
    {
        var sequence = OneClip();
        var before = AudioMixSignature.Compute(sequence);

        sequence.AddOverlayTrack().TryAdd(OverlayItem.CreateText(new TextStyle("hola"), S(1), S(2)));

        Assert.Equal(before, AudioMixSignature.Compute(sequence));
    }

    [Fact]
    public void A_lifted_clip_moves_its_sound_to_the_layer_and_hiding_the_layer_silences_it()
    {
        var sequence = OneClip();
        new LiftClipToLayerCommand(sequence, sequence.Video.Clips[0]).Execute();
        var audible = AudioMixSignature.Compute(sequence);

        sequence.OverlayTracks[0].IsHidden = true;

        Assert.NotEqual(audible, AudioMixSignature.Compute(sequence));
    }
}
