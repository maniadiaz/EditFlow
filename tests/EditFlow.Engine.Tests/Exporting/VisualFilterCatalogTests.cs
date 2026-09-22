// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class VisualFilterCatalogTests
{
    [Fact]
    public void No_filter_produces_no_fragment()
    {
        Assert.Null(VisualFilterCatalog.Build(VisualFilterKind.None));
    }

    [Theory]
    [InlineData(VisualFilterKind.BlackAndWhite, "hue=s=0")]
    [InlineData(VisualFilterKind.Sepia, "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131:0")]
    [InlineData(VisualFilterKind.Vintage, "eq=saturation=0.75:contrast=1.05,colorbalance=rs=.10:bs=-.10,vignette=PI/5")]
    [InlineData(VisualFilterKind.Vignette, "vignette=PI/4")]
    [InlineData(VisualFilterKind.Warm, "colorbalance=rs=.15:bs=-.15")]
    [InlineData(VisualFilterKind.Cool, "colorbalance=rs=-.15:bs=.15")]
    public void Each_preset_produces_its_exact_fragment(VisualFilterKind kind, string expected)
    {
        Assert.Equal(expected, VisualFilterCatalog.Build(kind));
    }
}
