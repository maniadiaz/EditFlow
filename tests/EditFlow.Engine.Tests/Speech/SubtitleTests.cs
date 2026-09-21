// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Speech;

namespace EditFlow.Engine.Tests.Speech;

public class SubtitleParserTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    [Fact]
    public void A_plain_srt_is_read_with_its_times_and_text()
    {
        const string srt = """
            1
            00:00:00,000 --> 00:00:02,280
             Hello everyone. This is a test of the

            2
            00:01:02,500 --> 01:00:04,000
             automatic subtitles.
            """;

        var segments = SubtitleParser.ParseSrt(srt);

        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.Zero, segments[0].Start);
        Assert.Equal(S(2.28), segments[0].End);
        Assert.Equal("Hello everyone. This is a test of the", segments[0].Text);
        Assert.Equal(S(62.5), segments[1].Start);
        Assert.Equal(TimeSpan.FromHours(1) + S(4), segments[1].End);
    }

    [Fact]
    public void A_multi_line_cue_is_joined_into_one_line()
    {
        var segments = SubtitleParser.ParseSrt("1\n00:00:01,000 --> 00:00:03,000\nfirst line\nsecond line\n");

        Assert.Equal("first line second line", Assert.Single(segments).Text);
    }

    [Fact]
    public void Whisper_repetitions_are_merged_and_zero_length_pieces_dropped()
    {
        // Salida real de Whisper con un silencio en medio.
        const string srt = """
            1
            00:00:05,120 --> 00:00:07,120
             This is a test of the automatic SATA Itels.

            2
            00:00:07,120 --> 00:00:07,120
             This is a test of the automatic SATA Itels.

            3
            00:00:07,120 --> 00:00:07,200
             This is a test of the automatic SATA Itels.
            """;

        var segments = SubtitleParser.ParseSrt(srt);

        var one = Assert.Single(segments);
        Assert.Equal(S(5.12), one.Start);
        Assert.Equal(S(7.2), one.End);
    }

    [Theory]
    [InlineData("[MUSIC]")]
    [InlineData("(aplausos)")]
    [InlineData("♪ música ♪")]
    [InlineData("   ")]
    public void Sounds_that_are_not_speech_are_left_out(string text)
    {
        var segments = SubtitleParser.Clean([new SpeechSegment(S(1), S(3), text)]);

        Assert.Empty(segments);
    }

    [Fact]
    public void Overlaps_are_trimmed_so_the_cues_fit_in_one_layer()
    {
        var segments = SubtitleParser.Clean(
        [
            new SpeechSegment(S(0), S(5), "uno"),
            new SpeechSegment(S(3), S(6), "dos"),
        ]);

        Assert.Equal(2, segments.Count);
        Assert.Equal(S(3), segments[0].End);
        Assert.Equal(S(3), segments[1].Start);
    }

    [Fact]
    public void A_cue_shorter_than_readable_is_extended_when_there_is_room_and_dropped_when_not()
    {
        var extended = SubtitleParser.Clean([new SpeechSegment(S(1), S(1.05), "hola")]);
        Assert.Equal(SubtitleParser.MinimumDuration, extended[0].End - extended[0].Start);

        var dropped = SubtitleParser.Clean(
        [
            new SpeechSegment(S(1), S(1.05), "pegado"),
            new SpeechSegment(S(1.1), S(3), "siguiente"),
        ]);
        Assert.Equal("siguiente", Assert.Single(dropped).Text);
    }

    [Fact]
    public void The_whisper_arguments_avoid_repetitions_and_keep_lines_short()
    {
        var line = string.Join(' ', SubtitleGenerator.BuildArguments("m.bin", "a.wav", "out", "es", 4));

        Assert.Contains("-m m.bin", line, StringComparison.Ordinal);
        Assert.Contains("-l es", line, StringComparison.Ordinal);
        Assert.Contains("-osrt", line, StringComparison.Ordinal);
        Assert.Contains("-mc 0", line, StringComparison.Ordinal);
        Assert.Contains("-ml 42 -sow", line, StringComparison.Ordinal);
    }
}

public class AddSubtitlesCommandTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    private static EditSequence Sequence()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo("C:/no-existe/a.mp4", S(20), 1920, 1080, 30, "h264", true)));
        return sequence;
    }

    [Fact]
    public void Subtitles_become_editable_texts_in_a_new_layer_at_the_bottom_centre()
    {
        var sequence = Sequence();
        var command = new AddSubtitlesCommand(sequence,
        [
            new SubtitleCue(S(1), S(3), "Hola"),
            new SubtitleCue(S(4), S(6), "¿Qué tal?"),
        ]);

        command.Execute();

        var layer = Assert.Single(sequence.OverlayTracks);
        Assert.Equal("Subtítulos", layer.Name);
        Assert.Equal(2, command.Added);
        Assert.Equal(["Hola", "¿Qué tal?"], layer.Items.Select(i => i.Text!.Content).ToArray());
        Assert.All(layer.Items, i =>
        {
            Assert.Equal(OverlayKind.Text, i.Kind);
            Assert.Equal(0.5, i.Transform.CenterX);
            Assert.True(i.Transform.CenterY > 0.8);
        });
        Assert.Equal(S(1), layer.Items[0].Start);
        Assert.Equal(S(2), layer.Items[0].Duration);
    }

    [Fact]
    public void All_the_subtitles_are_one_step_of_the_history()
    {
        var sequence = Sequence();
        var history = new UndoHistory();
        history.Do(new AddSubtitlesCommand(sequence, [new SubtitleCue(S(1), S(3), "a"), new SubtitleCue(S(4), S(6), "b")]));

        history.Undo();
        Assert.Empty(sequence.OverlayTracks);

        history.Redo();
        Assert.Equal(2, Assert.Single(sequence.OverlayTracks).Items.Count);
    }

    [Fact]
    public void Overlapping_cues_are_cut_and_empty_ones_ignored()
    {
        var sequence = Sequence();
        var command = new AddSubtitlesCommand(sequence,
        [
            new SubtitleCue(S(0), S(5), "uno"),
            new SubtitleCue(S(3), S(6), "dos"),
            new SubtitleCue(S(7), S(8), "  "),
        ]);

        command.Execute();

        var items = sequence.OverlayTracks[0].Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(S(3), items[0].Duration);      // recortado al empezar el siguiente
    }

    [Fact]
    public void Nothing_to_add_leaves_no_empty_layer()
    {
        var sequence = Sequence();
        var command = new AddSubtitlesCommand(sequence, []);

        command.Execute();

        Assert.Equal(0, command.Added);
        Assert.Empty(sequence.OverlayTracks);
    }
}

public class WhisperSetupTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("editflow-whisper-");
    private HttpListener? _listener;

    public void Dispose()
    {
        _listener?.Close();
        try { _root.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private Uri Serve(byte[] content)
    {
        var port = Random.Shared.Next(20_000, 40_000);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    context.Response.ContentLength64 = content.Length;
                    await context.Response.OutputStream.WriteAsync(content);
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // El servidor de la prueba se cerró.
            }
        });

        return new Uri($"http://localhost:{port}/model.bin");
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task A_model_is_installed_only_when_its_fingerprint_matches()
    {
        var data = new byte[200_000];
        Random.Shared.NextBytes(data);

        var reports = new List<double>();
        await WhisperSetup.InstallModelAsync(
            WhisperModel.Base, new Progress<double>(reports.Add), _root.FullName, Serve(data), Sha(data));

        Assert.True(WhisperSetup.HasModel(WhisperModel.Base, _root.FullName));
        Assert.Equal(data.Length, new FileInfo(WhisperSetup.ModelPath(WhisperModel.Base, _root.FullName)).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(WhisperSetup.ModelPath(WhisperModel.Base, _root.FullName))!, "*.part"));
    }

    [Fact]
    public async Task A_download_with_the_wrong_fingerprint_is_discarded()
    {
        var data = new byte[50_000];
        Random.Shared.NextBytes(data);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WhisperSetup.InstallModelAsync(
            WhisperModel.Small, null, _root.FullName, Serve(data), new string('0', 64)));

        Assert.False(WhisperSetup.HasModel(WhisperModel.Small, _root.FullName));
        var models = Path.Combine(_root.FullName, "models");
        Assert.True(!Directory.Exists(models) || Directory.GetFiles(models).Length == 0);
    }

    [Fact]
    public void Only_the_needed_files_are_taken_from_the_zip_and_without_folders()
    {
        var zipPath = Path.Combine(_root.FullName, "bin.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "Release/whisper-cli.exe", "Release/whisper.dll", "Release/ggml.dll", "Release/ggml-cpu-haswell.dll", "Release/main.exe", "Release/SDL2.dll", "../escape/ggml-evil.dll" })
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("contenido");
            }
        }

        var destination = Path.Combine(_root.FullName, "bin");
        WhisperSetup.ExtractRuntime(zipPath, destination);

        var files = Directory.GetFiles(destination).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(["ggml-cpu-haswell.dll", "ggml-evil.dll", "ggml.dll", "whisper-cli.exe", "whisper.dll"], files);
        Assert.False(File.Exists(Path.Combine(_root.FullName, "escape", "ggml-evil.dll")));   // nada fuera de la carpeta
        Assert.NotNull(WhisperSetup.LocateCli(_root.FullName));
    }

    [Fact]
    public void A_package_without_the_program_is_refused()
    {
        var zipPath = Path.Combine(_root.FullName, "bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Release/ggml.dll");
        }

        Assert.Throws<InvalidOperationException>(() => WhisperSetup.ExtractRuntime(zipPath, Path.Combine(_root.FullName, "bin")));
    }

    [Fact]
    public void The_catalog_has_fixed_sizes_and_fingerprints()
    {
        Assert.All(WhisperModel.All, m =>
        {
            Assert.Equal(64, m.Sha256.Length);
            Assert.True(m.Bytes > 100_000_000);
            Assert.Equal("https", m.Url.Scheme);
        });
    }
}
