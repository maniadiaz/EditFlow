// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class TransformFilterTests
{
    [Fact]
    public void The_normal_transform_produces_no_filter()
    {
        Assert.Null(TransformFilter.Build(ClipTransform.None, 800, 600));
        Assert.Null(TransformFilter.Build(null, 800, 600));
    }

    [Fact]
    public void Zooming_in_scales_up_and_crops_back_down_centered()
    {
        var filter = TransformFilter.Build(new ClipTransform(2, 0, 0, 0), 800, 600);

        Assert.Equal("scale=1600:1200,crop=800:600:400:300", filter);
    }

    [Fact]
    public void Panning_shifts_the_crop_window()
    {
        var filter = TransformFilter.Build(new ClipTransform(2, 0.25, 0, 0), 800, 600);

        // 400 de centrado + 0,25*800 = 200 de desplazamiento.
        Assert.Equal("scale=1600:1200,crop=800:600:600:300", filter);
    }

    [Fact]
    public void Rotating_adds_a_rotate_filter_after_the_crop()
    {
        var filter = TransformFilter.Build(new ClipTransform(2, 0, 0, 30), 800, 600);

        Assert.Equal("scale=1600:1200,crop=800:600:400:300,rotate=0.523599:ow=800:oh=600:c=black", filter);
    }

    [Fact]
    public void A_tiny_rotation_is_treated_as_none_to_avoid_a_no_op_filter()
    {
        var filter = TransformFilter.Build(new ClipTransform(1, 0, 0, 0.01), 800, 600);

        Assert.Null(filter);
    }

    [Fact]
    public void The_crop_never_reads_outside_the_scaled_frame()
    {
        // Un desplazamiento en el límite no debe producir un crop de x/y negativo ni mayor
        // que lo que la escala permite.
        var filter = TransformFilter.Build(new ClipTransform(2, 0.5, -0.5, 0), 800, 600);

        Assert.Equal("scale=1600:1200,crop=800:600:800:0", filter);
    }

    [Theory]
    [InlineData(1.3)]
    [InlineData(2.7)]
    [InlineData(5)]
    public void Scaled_dimensions_are_always_even_for_yuv420p(double scale)
    {
        var filter = TransformFilter.Build(new ClipTransform(scale, 0, 0, 0), 801, 601);

        Assert.NotNull(filter);
        var scaleClause = filter.Split(',')[0];
        var dims = scaleClause["scale=".Length..].Split(':');
        Assert.Equal(0, int.Parse(dims[0]) % 2);
        Assert.Equal(0, int.Parse(dims[1]) % 2);
    }

    [Fact]
    public void Zero_sized_canvases_produce_no_filter_instead_of_throwing()
    {
        Assert.Null(TransformFilter.Build(new ClipTransform(2, 0, 0, 0), 0, 0));
    }
}
