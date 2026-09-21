// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;

namespace EditFlow.Engine.Tests.Media;

public class MediaInfoTests
{
    private static MediaInfo Landscape(int rotation) => new(
        Path: "phone.mp4",
        Duration: TimeSpan.FromSeconds(10),
        Width: 1920,
        Height: 1080,
        FrameRate: 30,
        VideoCodec: "h264",
        HasAudio: true,
        Rotation: rotation);

    [Fact]
    public void Without_rotation_display_size_matches_the_encoded_size()
    {
        var info = Landscape(0);

        Assert.Equal(1920, info.DisplayWidth);
        Assert.Equal(1080, info.DisplayHeight);
        Assert.False(info.IsPortrait);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    [InlineData(-90)]
    public void A_quarter_turn_swaps_the_dimensions(int rotation)
    {
        // Así se graba en vertical con un móvil: el archivo es 1920x1080 con una
        // rotación en los metadatos. Sin corregirlo, se exportaría tumbado.
        var info = Landscape(rotation);

        Assert.Equal(1080, info.DisplayWidth);
        Assert.Equal(1920, info.DisplayHeight);
        Assert.True(info.IsPortrait);
    }

    [Fact]
    public void A_half_turn_keeps_the_dimensions()
    {
        // 180 grados pone el video boca abajo, pero no cambia su forma.
        var info = Landscape(180);

        Assert.Equal(1920, info.DisplayWidth);
        Assert.Equal(1080, info.DisplayHeight);
    }

    [Fact]
    public void Aspect_ratio_follows_the_visible_orientation()
    {
        Assert.Equal(16.0 / 9.0, Landscape(0).AspectRatio, precision: 4);
        Assert.Equal(9.0 / 16.0, Landscape(90).AspectRatio, precision: 4);
    }

    [Fact]
    public void A_zero_height_does_not_divide_by_zero()
    {
        var broken = new MediaInfo("x.mp4", TimeSpan.Zero, 0, 0, 0, "none", false);

        Assert.Equal(0, broken.AspectRatio);
    }
}
