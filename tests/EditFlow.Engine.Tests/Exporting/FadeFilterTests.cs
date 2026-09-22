// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class FadeFilterTests
{
    [Fact]
    public void No_fade_produces_no_filter()
    {
        Assert.Null(FadeFilter.BuildVideo(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        Assert.Null(FadeFilter.BuildAudio(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_fade_in_alone_starts_at_zero()
    {
        var filter = FadeFilter.BuildVideo(TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.Equal("fade=t=in:st=0:d=1", filter);
    }

    [Fact]
    public void A_fade_out_alone_starts_where_the_clip_has_that_much_left()
    {
        var filter = FadeFilter.BuildVideo(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        Assert.Equal("fade=t=out:st=4:d=1", filter);
    }

    [Fact]
    public void Both_fades_are_joined_with_a_comma()
    {
        var filter = FadeFilter.BuildVideo(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        Assert.Equal("fade=t=in:st=0:d=1,fade=t=out:st=4:d=1", filter);
    }

    [Fact]
    public void Audio_uses_the_afade_filter_name()
    {
        var filter = FadeFilter.BuildAudio(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        Assert.Equal("afade=t=in:st=0:d=1,afade=t=out:st=4:d=1", filter);
    }

    [Fact]
    public void Fades_that_would_overlap_are_clamped_to_fit_the_duration()
    {
        // 4 s de entrada + 4 s de salida no caben en un clip de 5 s: la salida se recorta
        // a lo que queda después de la entrada.
        var filter = FadeFilter.BuildVideo(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));

        Assert.Equal("fade=t=in:st=0:d=4,fade=t=out:st=4:d=1", filter);
    }

    [Fact]
    public void A_negative_fade_is_treated_as_no_fade_instead_of_throwing()
    {
        Assert.Null(FadeFilter.BuildVideo(TimeSpan.FromSeconds(-1), TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Fractional_seconds_use_an_invariant_decimal_point()
    {
        var filter = FadeFilter.BuildVideo(TimeSpan.FromSeconds(1.5), TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.Equal("fade=t=in:st=0:d=1.5", filter);
    }
}
