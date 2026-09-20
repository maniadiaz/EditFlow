// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Tests.Timeline;

public class ClipTests
{
    private static MediaInfo Source(double seconds = 10) => new(
        Path: "video.mp4",
        Duration: TimeSpan.FromSeconds(seconds),
        Width: 1920,
        Height: 1080,
        FrameRate: 30,
        VideoCodec: "h264",
        HasAudio: true);

    [Fact]
    public void Spans_the_whole_file_by_default()
    {
        var clip = new Clip(Source(10));

        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceOut);
        Assert.Equal(TimeSpan.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void Rejects_a_range_beyond_the_source()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Clip(Source(10), TimeSpan.Zero, TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void Rejects_an_inverted_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Clip(Source(10), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Rejects_an_empty_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Clip(Source(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Trimming_the_start_beyond_the_file_stops_at_zero()
    {
        var clip = new Clip(Source(10));

        var applied = clip.TrimStart(TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(TimeSpan.Zero, applied);
    }

    [Fact]
    public void Trimming_the_end_beyond_the_file_stops_at_its_duration()
    {
        var clip = new Clip(Source(10), TimeSpan.Zero, TimeSpan.FromSeconds(8));

        clip.TrimEnd(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(10), clip.SourceOut);
    }

    [Fact]
    public void Trimming_reports_how_much_was_actually_applied()
    {
        var clip = new Clip(Source(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));

        // Se piden 5 segundos hacia atrás pero solo quedan 2 de material.
        var applied = clip.TrimStart(TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.FromSeconds(-2), applied);
        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
    }

    [Fact]
    public void Trimming_never_collapses_a_clip_to_nothing()
    {
        var clip = new Clip(Source(10));

        clip.TrimStart(TimeSpan.FromSeconds(100));

        Assert.True(clip.Duration >= Clip.MinimumDuration);
    }

    [Fact]
    public void Splitting_produces_two_halves_that_add_up_to_the_original()
    {
        var clip = new Clip(Source(10));
        var originalDuration = clip.Duration;

        var second = clip.SplitAt(TimeSpan.FromSeconds(4));

        Assert.NotNull(second);
        Assert.Equal(TimeSpan.FromSeconds(4), clip.Duration);
        Assert.Equal(TimeSpan.FromSeconds(6), second.Duration);
        Assert.Equal(originalDuration, clip.Duration + second.Duration);
    }

    [Fact]
    public void Splitting_keeps_the_halves_pointing_at_the_right_source_range()
    {
        // Un clip que ya estaba recortado: el corte debe medirse desde su inicio,
        // no desde el inicio del archivo.
        var clip = new Clip(Source(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

        var second = clip.SplitAt(TimeSpan.FromSeconds(3));

        Assert.NotNull(second);
        Assert.Equal(TimeSpan.FromSeconds(5), clip.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(8), clip.SourceOut);
        Assert.Equal(TimeSpan.FromSeconds(8), second.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(15), second.SourceOut);
    }

    [Fact]
    public void Splitting_at_the_very_start_is_refused()
    {
        var clip = new Clip(Source(10));

        Assert.Null(clip.SplitAt(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void Splitting_at_the_very_end_is_refused()
    {
        var clip = new Clip(Source(10));

        Assert.Null(clip.SplitAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void A_refused_split_leaves_the_clip_untouched()
    {
        // Si un corte inválido acortara el clip antes de rendirse, el usuario perdería
        // material sin haber obtenido nada a cambio.
        var clip = new Clip(Source(10));
        var before = (clip.SourceIn, clip.SourceOut);

        clip.SplitAt(TimeSpan.FromMilliseconds(1));

        Assert.Equal(before, (clip.SourceIn, clip.SourceOut));
    }

    [Fact]
    public void A_clone_is_independent_of_the_original()
    {
        var clip = new Clip(Source(10));
        var copy = clip.Clone();

        copy.TrimStart(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.Zero, clip.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(3), copy.SourceIn);
        Assert.NotEqual(clip.Id, copy.Id);
    }
}
