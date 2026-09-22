// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Engine.Tests.Timeline;

public class ClipTransformTests
{
    [Fact]
    public void The_default_transform_is_none()
    {
        Assert.True(ClipTransform.None.IsNone);
        Assert.Equal(1, ClipTransform.None.Scale);
        Assert.Equal(0, ClipTransform.None.OffsetX);
        Assert.Equal(0, ClipTransform.None.OffsetY);
        Assert.Equal(0, ClipTransform.None.Rotation);
    }

    [Theory]
    [InlineData(1.5, 0, 0, 0)]
    [InlineData(1, 0.1, 0, 0)]
    [InlineData(1, 0, 0.1, 0)]
    [InlineData(1, 0, 0, 15)]
    public void Any_non_trivial_value_is_not_none(double scale, double offsetX, double offsetY, double rotation)
    {
        Assert.False(new ClipTransform(scale, offsetX, offsetY, rotation).IsNone);
    }

    [Fact]
    public void Scale_is_clamped_to_the_supported_range()
    {
        Assert.Equal(ClipTransform.MinimumScale, new ClipTransform(0.2, 0, 0, 0).Clamped().Scale);
        Assert.Equal(ClipTransform.MaximumScale, new ClipTransform(50, 0, 0, 0).Clamped().Scale);
    }

    [Fact]
    public void Without_any_zoom_the_offset_cannot_move_at_all()
    {
        // A escala 1 se ve el fotograma completo: no hay margen que desplazar.
        var clamped = new ClipTransform(1, 0.3, 0.3, 0).Clamped();

        Assert.Equal(0, clamped.OffsetX);
        Assert.Equal(0, clamped.OffsetY);
    }

    [Fact]
    public void Doubling_the_zoom_allows_panning_up_to_half_the_frame()
    {
        var clamped = new ClipTransform(2, 10, -10, 0).Clamped();

        Assert.Equal(0.5, clamped.OffsetX);
        Assert.Equal(-0.5, clamped.OffsetY);
    }

    [Theory]
    [InlineData(370, 10)]
    [InlineData(-190, 170)]
    [InlineData(180, -180)]   // 180 y -180 son el mismo ángulo; el borde cae del lado negativo
    [InlineData(-180, -180)]
    public void Rotation_wraps_around_to_stay_within_a_half_turn(double requested, double expected)
    {
        var clamped = new ClipTransform(1, 0, 0, requested).Clamped();

        Assert.Equal(expected, clamped.Rotation, precision: 6);
    }
}

public class SetTransformCommandTests
{
    private static MediaInfo Source(string name = "a.mp4", double seconds = 20) =>
        new(name, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", true);

    [Fact]
    public void Setting_a_transform_applies_it_to_the_clip()
    {
        var clip = new Clip(Source());
        var undo = new UndoHistory();

        undo.Do(new SetTransformCommand(clip, new ClipTransform(2, 0.1, 0, 15)));

        Assert.Equal(2, clip.Transform.Scale);
        Assert.Equal(15, clip.Transform.Rotation);
    }

    [Fact]
    public void Undoing_restores_whatever_transform_was_there_before()
    {
        var clip = new Clip(Source()) { Transform = new ClipTransform(1.5, 0, 0, 0) };
        var undo = new UndoHistory();

        undo.Do(new SetTransformCommand(clip, new ClipTransform(3, 0, 0, 0)));
        undo.Undo();

        Assert.Equal(1.5, clip.Transform.Scale);
    }

    [Fact]
    public void The_transform_is_clamped_when_applied()
    {
        var clip = new Clip(Source());
        var undo = new UndoHistory();

        undo.Do(new SetTransformCommand(clip, new ClipTransform(100, 0, 0, 0)));

        Assert.Equal(ClipTransform.MaximumScale, clip.Transform.Scale);
    }
}
