// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Projects;
using EditFlow.Engine.Playback;

namespace EditFlow.Engine.Tests.Playback;

public class PlaybackResolutionTests
{
    [Theory]
    [InlineData(1, "Completa")]
    [InlineData(2, "1/2")]
    [InlineData(4, "1/4")]
    [InlineData(8, "1/8")]
    [InlineData(16, "1/16")]
    public void Labels_read_like_premiere(int divisor, string label) =>
        Assert.Equal(label, PlaybackResolution.Label(divisor));

    [Theory]
    [InlineData(2, 1080)]    // 4K a 1/2 = 1920x1080
    [InlineData(4, 540)]     // 4K a 1/4 = 960x540
    [InlineData(8, 270)]
    [InlineData(16, 134)]
    public void A_4K_video_is_decoded_at_the_chosen_fraction(int divisor, int expectedHeight)
    {
        // El preview ocupa 1080 píxeles: la fracción nunca sube de ahí.
        var height = PlaybackResolution.DecodeHeight(divisor, sourceHeight: 2160, fullHeight: 1080);

        Assert.Equal(expectedHeight, height);
        Assert.Equal(0, height % 2);
    }

    [Fact]
    public void The_widths_match_the_documented_examples()
    {
        Assert.Equal(1920, PlaybackResolution.WidthFor(1080));
        Assert.Equal(960, PlaybackResolution.WidthFor(540));
    }

    [Fact]
    public void Full_resolution_is_whatever_the_preview_needs()
    {
        Assert.Equal(720, PlaybackResolution.DecodeHeight(1, 2160, 720));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Lowering_the_resolution_never_costs_more_than_full(int divisor)
    {
        foreach (var source in new[] { 480, 720, 1080, 1440, 2160, 4320 })
        {
            foreach (var full in new[] { 360, 480, 720, 1080 })
            {
                Assert.True(PlaybackResolution.DecodeHeight(divisor, source, full)
                            <= PlaybackResolution.DecodeHeight(1, source, full));
            }
        }
    }

    [Fact]
    public void The_image_never_gets_smaller_than_the_minimum_readable_size()
    {
        Assert.True(PlaybackResolution.DecodeHeight(16, 480, 1080) >= PlaybackResolution.MinimumHeight);
    }

    [Fact]
    public void An_unknown_divisor_means_full_resolution()
    {
        Assert.Equal(1, PlaybackResolution.Normalize(3));
        Assert.Equal(1, PlaybackResolution.Normalize(0));
        Assert.Equal(8, PlaybackResolution.Normalize(8));
    }
}

public class UserSettingsStoreTests
{
    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var directory = Directory.CreateTempSubdirectory("editflow-settings-");
        try
        {
            var store = new UserSettingsStore(Path.Combine(directory.FullName, "sub", "settings.json"));
            store.Save(new UserSettings(PlaybackDivisor: 4));

            Assert.Equal(4, store.Load().PlaybackDivisor);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_or_corrupt_file_gives_the_defaults()
    {
        var directory = Directory.CreateTempSubdirectory("editflow-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var store = new UserSettingsStore(path);

            Assert.Equal(1, store.Load().PlaybackDivisor);

            File.WriteAllText(path, "{ esto no es json");
            Assert.Equal(1, store.Load().PlaybackDivisor);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_nonsensical_divisor_falls_back_to_full_resolution()
    {
        var directory = Directory.CreateTempSubdirectory("editflow-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, "{ \"PlaybackDivisor\": 7 }");

            Assert.Equal(1, new UserSettingsStore(path).Load().PlaybackDivisor);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
