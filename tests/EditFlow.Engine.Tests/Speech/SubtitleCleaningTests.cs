// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Speech;

namespace EditFlow.Engine.Tests.Speech;

public class SubtitleCleaningTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    [Fact]
    public void A_phrase_repeated_four_times_becomes_one()
    {
        Assert.Equal("It's fine.", SubtitleParser.CollapseRepetitions("It's fine. It's fine. It's fine. It's fine."));
        Assert.Equal("It's okay, it's fine.", SubtitleParser.CollapseRepetitions("It's okay, it's fine, it's fine, it's fine, it's fine."));
    }

    [Fact]
    public void Two_repetitions_are_left_alone_because_a_person_can_say_that()
    {
        Assert.Equal("It's fine. It's fine.", SubtitleParser.CollapseRepetitions("It's fine. It's fine."));
    }

    [Fact]
    public void Sung_lyrics_are_kept_without_the_music_notes_and_note_only_lines_are_dropped()
    {
        var segments = SubtitleParser.Clean(
        [
            new SpeechSegment(S(1), S(3), "♪ L'autre nuve réelle ♪"),
            new SpeechSegment(S(4), S(6), "♪"),
            new SpeechSegment(S(7), S(9), "[¶¶¶]"),
        ]);

        var one = Assert.Single(segments);
        Assert.Equal("L'autre nuve réelle", one.Text);
    }

    [Fact]
    public void The_count_of_detected_fragments_includes_the_ones_thrown_away()
    {
        const string srt = """
            1
            00:00:00,000 --> 00:00:10,000
             [MUSIC]

            2
            00:00:30,000 --> 00:00:33,400
             (dramatic music)

            3
            00:00:33,400 --> 00:00:34,240
             It's okay.
            """;

        var segments = SubtitleParser.ParseSrt(srt, out var detected);

        Assert.Equal(3, detected);
        Assert.Equal("It's okay.", Assert.Single(segments).Text);
    }

    [Fact]
    public void A_file_with_only_music_leaves_nothing()
    {
        const string srt = """
            1
            00:00:00,000 --> 00:00:10,000
             [MUSIC]

            2
            00:01:30,000 --> 00:01:40,000
             (soft music)
            """;

        var segments = SubtitleParser.ParseSrt(srt, out var detected);

        Assert.Empty(segments);
        Assert.Equal(2, detected);
    }

    [Theory]
    [InlineData("<i> Où tout commence, rien ne finit </i>", "Où tout commence, rien ne finit")]
    [InlineData("<b>Hola</b> <u>mundo</u>", "Hola mundo")]
    [InlineData("{\\an8}Arriba", "Arriba")]
    [InlineData("<font color=\"#ff0000\">rojo</font>", "rojo")]
    [InlineData("2 < 3 y 4 > 1", "2 < 3 y 4 > 1")]
    public void Formatting_tags_are_not_shown_as_text(string input, string expected)
    {
        var segments = SubtitleParser.Clean([new SpeechSegment(S(1), S(3), input)]);

        Assert.Equal(expected, Assert.Single(segments).Text);
    }

    [Fact]
    public void A_cue_that_is_only_tags_is_dropped()
    {
        Assert.Empty(SubtitleParser.Clean([new SpeechSegment(S(1), S(3), "<i> </i>")]));
    }
}
