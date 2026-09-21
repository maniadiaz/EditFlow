// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class ExportPresetTests
{
    [Fact]
    public void Every_preset_has_a_distinct_name_and_a_description()
    {
        Assert.NotEmpty(ExportPreset.All);
        Assert.Equal(ExportPreset.All.Count, ExportPreset.All.Select(p => p.Name).Distinct().Count());
        Assert.All(ExportPreset.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.DoesNotContain(ExportPreset.All, p => p.Name == ExportPreset.CustomName);
    }

    [Fact]
    public void Every_preset_starts_from_a_known_resolution_and_a_valid_quality()
    {
        Assert.All(ExportPreset.All, p =>
        {
            Assert.Contains(p.Resolution, VideoResolution.Presets);
            Assert.InRange(p.Quality, 1, 100);
            Assert.Contains(p.AudioBitrateKbps, new[] { 128, 192, 256, 320 });
            Assert.Contains(p.FrameRate, new[] { 24.0, 25, 30, 50, 60 });
        });
    }

    [Fact]
    public void The_vertical_preset_swaps_width_and_height()
    {
        var vertical = ExportPreset.All.Single(p => p.Portrait);
        var portrait = vertical.Resolution.AsPortrait();

        Assert.Equal(1080, portrait.Width);
        Assert.Equal(1920, portrait.Height);
    }
}
