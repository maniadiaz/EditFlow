// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using EditFlow.Engine.Speech;

namespace EditFlow.Engine.Tests.Speech;

public class SubtitleAlignmentTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    private static SpeechRegion R(double start, double end) => new(S(start), S(end));

    [Fact]
    public void The_speech_detector_output_is_read_in_centiseconds()
    {
        const string output = """
            load_backend: loaded CPU backend from C:\x\ggml-cpu-haswell.dll

            Detected 3 speech segments:
            Speech segment 0: start = 544.00, end = 630.00
            Speech segment 1: start = 1018.00, end = 1049.00
            Speech segment 2: start = 4688.00, end = 5085.50
            """;

        var regions = SubtitleParser.ParseSpeechRegions(output);

        Assert.Equal(3, regions.Count);
        Assert.Equal(S(5.44), regions[0].Start);
        Assert.Equal(S(6.3), regions[0].End);
        Assert.Equal(S(50.855), regions[2].End);
    }

    [Fact]
    public void Garbage_output_gives_no_regions()
    {
        Assert.Empty(SubtitleParser.ParseSpeechRegions("nada que ver"));
    }

    [Fact]
    public void A_cue_that_starts_at_zero_but_is_spoken_later_starts_when_the_voice_does()
    {
        // Lo que pasaba: la primera frase empezaba en 0:00 y la voz llegaba en el 5,4.
        var aligned = SubtitleParser.AlignToSpeech(
            [new SpeechSegment(S(0), S(8), "She's not the only one who grieves.")],
            [R(5.44, 7.5)]);

        var cue = Assert.Single(aligned);
        Assert.InRange(cue.Start.TotalSeconds, 5.2, 5.4);
        Assert.InRange(cue.End.TotalSeconds, 7.6, 8.0);
    }

    [Fact]
    public void Short_pauses_inside_a_sentence_do_not_split_the_voice()
    {
        var aligned = SubtitleParser.AlignToSpeech(
            [new SpeechSegment(S(0), S(14), "The pain of losing you, she made me inherit it.")],
            [R(10.18, 10.49), R(11.52, 14.37)]);

        var cue = Assert.Single(aligned);
        Assert.InRange(cue.Start.TotalSeconds, 10.0, 10.1);
        Assert.Equal(S(14), cue.End);                                   // no pasa de donde acaba Whisper
    }

    [Fact]
    public void A_cue_with_no_matching_speech_region_keeps_its_whisper_timing_instead_of_being_dropped()
    {
        // Lo que pasaba: una frase dicha en voz baja o muy corta («I'm tired.», «when it mattered.») que el
        // detector no pescaba se perdía del todo, y el video se quedaba con huecos sin subtítulo aunque sí
        // hubiera diálogo. Ahora, sin un tramo de voz encima, el subtítulo se deja con el tiempo que le dio
        // Whisper en vez de descartarlo.
        var quiet = new SpeechSegment(S(20), S(23), "when it mattered.");

        var aligned = SubtitleParser.AlignToSpeech(
        [
            new SpeechSegment(S(5), S(8), "real uno"),
            quiet,
            new SpeechSegment(S(30), S(33), "real dos"),
            new SpeechSegment(S(40), S(43), "real tres"),
        ],
        [R(5.2, 7.8), R(30.1, 32.9), R(40.2, 42.8)]);

        Assert.Equal(["real uno", "when it mattered.", "real dos", "real tres"], aligned.Select(a => a.Text).ToArray());
        Assert.Equal(quiet, aligned.Single(a => a.Text == "when it mattered."));
    }

    [Fact]
    public void No_cue_is_ever_dropped_even_with_very_sparse_detection()
    {
        var original = new[]
        {
            new SpeechSegment(S(5), S(8), "uno"),
            new SpeechSegment(S(20), S(23), "dos"),
            new SpeechSegment(S(30), S(33), "tres"),
        };

        // Solo un tramo de voz detectado de tres: los otros dos se dejan con su tiempo original.
        var aligned = SubtitleParser.AlignToSpeech(original, [R(5.2, 7.8)]);

        Assert.Equal(["uno", "dos", "tres"], aligned.Select(a => a.Text).ToArray());
        Assert.Equal(original[1], aligned[1]);
        Assert.Equal(original[2], aligned[2]);
    }

    [Fact]
    public void Without_regions_nothing_changes()
    {
        var original = new[] { new SpeechSegment(S(1), S(3), "hola") };

        Assert.Same(original, SubtitleParser.AlignToSpeech(original, []));
        Assert.Empty(SubtitleParser.AlignToSpeech([], [R(1, 2)]));
    }

    [Fact]
    public void The_aligned_cues_never_overlap_and_keep_a_readable_length()
    {
        var aligned = SubtitleParser.AlignToSpeech(
        [
            new SpeechSegment(S(0), S(6), "a"),
            new SpeechSegment(S(6), S(12), "b"),
        ],
        [R(1, 5.5), R(6.2, 11)]);

        Assert.Equal(2, aligned.Count);
        Assert.True(aligned[0].End <= aligned[1].Start);
        Assert.All(aligned, c => Assert.True(c.End - c.Start >= SubtitleParser.MinimumDuration));
    }
}

public class WhisperRuntimeTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("editflow-runtime-");

    public void Dispose()
    {
        try { _root.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_speech_detector_program_is_taken_from_the_zip_too()
    {
        var zipPath = Path.Combine(_root.FullName, "bin.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "Release/whisper-cli.exe", "Release/whisper-vad-speech-segments.exe", "Release/whisper.dll", "Release/ggml.dll", "Release/whisper-server.exe" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("x");
            }
        }

        WhisperSetup.ExtractRuntime(zipPath, Path.Combine(_root.FullName, "bin"));

        Assert.NotNull(WhisperSetup.LocateVadCli(_root.FullName));
        Assert.False(File.Exists(Path.Combine(_root.FullName, "bin", "whisper-server.exe")));
    }

    [Fact]
    public void The_runtime_counts_as_installed_only_with_the_program_the_detector_and_its_model()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root.FullName, "bin")).FullName;
        var models = Directory.CreateDirectory(Path.Combine(_root.FullName, "models")).FullName;

        Assert.False(WhisperSetup.IsRuntimeInstalled(_root.FullName));

        File.WriteAllText(Path.Combine(bin, "whisper-cli.exe"), "x");
        Assert.False(WhisperSetup.IsRuntimeInstalled(_root.FullName));   // instalación anterior, sin detector

        File.WriteAllText(Path.Combine(bin, "whisper-vad-speech-segments.exe"), "x");
        Assert.False(WhisperSetup.IsRuntimeInstalled(_root.FullName));

        File.WriteAllText(WhisperSetup.VadModelPath(_root.FullName), "x");
        Assert.True(WhisperSetup.IsRuntimeInstalled(_root.FullName));
        Assert.NotEmpty(Directory.GetFiles(models));
    }
}
