// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Exporting;

public class SegmentedExportUnitTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ExportSettings Settings(string path = "C:/videos/salida.mp4", string encoder = "libx264", TimeSpan? segment = null) => new()
    {
        OutputPath = path,
        Resolution = VideoResolution.P1080,
        EncoderName = encoder,
        SegmentDuration = segment,
    };

    // ------------------------------------------------------------------ cuántas partes

    [Theory]
    [InlineData(180, 90, 2)]     // 3 min en trozos de 1:30
    [InlineData(360, 120, 3)]
    [InlineData(240, 90, 3)]     // sobra un trozo de 1 min
    [InlineData(60, 90, 1)]      // más corto que una parte
    [InlineData(90, 90, 1)]
    [InlineData(90.02, 90, 1)]   // un redondeo no crea una parte de una centésima
    [InlineData(91, 90, 2)]
    public void The_number_of_parts_is_the_division_rounded_up(double total, double segment, int expected) =>
        Assert.Equal(expected, ExportSettings.SegmentCount(S(total), S(segment)));

    [Fact]
    public void Without_a_meaningful_duration_it_is_a_single_part()
    {
        Assert.Equal(1, ExportSettings.SegmentCount(TimeSpan.Zero, S(60)));
        Assert.Equal(1, ExportSettings.SegmentCount(S(60), TimeSpan.Zero));
    }

    // ---------------------------------------------------------------------- nombres

    [Fact]
    public void Parts_are_numbered_from_one_with_three_digits_next_to_the_chosen_name()
    {
        var settings = Settings(segment: S(60));

        Assert.EndsWith("salida_%03d.mp4", settings.SegmentPattern, StringComparison.Ordinal);
        Assert.EndsWith("salida_001.mp4", settings.SegmentPath(1), StringComparison.Ordinal);
        Assert.EndsWith("salida_012.mp4", settings.SegmentPath(12), StringComparison.Ordinal);
        Assert.Equal(settings.SegmentPath(1), settings.PrimaryOutputPath);
    }

    [Fact]
    public void Without_segments_the_primary_output_is_the_chosen_file()
    {
        var settings = Settings();

        Assert.False(settings.IsSegmented);
        Assert.Equal(settings.OutputPath, settings.PrimaryOutputPath);
    }

    [Theory]
    [InlineData("a.mp4", ExportContainer.Mp4)]
    [InlineData("a.MKV", ExportContainer.Mkv)]
    [InlineData("a.mov", ExportContainer.Mov)]
    [InlineData("a", ExportContainer.Mp4)]
    public void The_container_follows_the_extension(string path, ExportContainer expected) =>
        Assert.Equal(expected, Settings(path).Container);

    // ------------------------------------------------------------------- argumentos

    [Fact]
    public void A_segmented_export_forces_a_keyframe_at_every_cut_and_writes_numbered_files()
    {
        var args = FFmpegArgumentBuilder.BuildOutputArguments(Settings(segment: S(90)));
        var line = string.Join(' ', args);

        Assert.Contains("-force_key_frames expr:gte(t,n_forced*90)", line, StringComparison.Ordinal);
        Assert.Contains("-f segment", line, StringComparison.Ordinal);
        Assert.Contains("-segment_time 90", line, StringComparison.Ordinal);
        Assert.Contains("-segment_format mp4", line, StringComparison.Ordinal);
        Assert.Contains("-reset_timestamps 1", line, StringComparison.Ordinal);
        Assert.Contains("-segment_start_number 1", line, StringComparison.Ordinal);
        Assert.EndsWith("salida_%03d.mp4", args[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void NVENC_and_x265_need_forced_IDR_for_the_cuts_to_land_where_asked()
    {
        var nvenc = string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings(encoder: "h264_nvenc", segment: S(60))));
        var cpu = string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings(segment: S(60))));

        var x265 = string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings(encoder: "libx265", segment: S(60))));

        Assert.Contains("-forced-idr 1", nvenc, StringComparison.Ordinal);
        Assert.Contains("-forced-idr 1", x265, StringComparison.Ordinal);
        Assert.DoesNotContain("-forced-idr", cpu, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.mkv", "matroska")]
    [InlineData("a.mov", "mov")]
    [InlineData("a.mp4", "mp4")]
    public void The_segment_format_matches_the_container(string path, string format)
    {
        var line = string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings(path, segment: S(60))));

        Assert.Contains($"-segment_format {format}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Faststart_applies_to_mp4_and_mov_but_not_to_matroska()
    {
        Assert.Contains("+faststart", string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings("a.mp4"))), StringComparison.Ordinal);
        Assert.Contains("+faststart", string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings("a.mov"))), StringComparison.Ordinal);
        Assert.DoesNotContain("+faststart", string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings("a.mkv"))), StringComparison.Ordinal);

        Assert.Contains("movflags=+faststart",
            string.Join(' ', FFmpegArgumentBuilder.BuildOutputArguments(Settings("a.mp4", segment: S(60)))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_shorter_than_one_second_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            FFmpegArgumentBuilder.BuildOutputArguments(Settings(segment: S(0.5))));
    }

    [Fact]
    public void Without_audio_the_output_has_no_audio_track_and_the_graph_still_closes()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo("v.mp4", S(10), 1920, 1080, 30, "h264", true)));

        var settings = Settings() with { IncludeAudio = false };
        using var command = ExportCommandBuilder.Build(sequence, settings);
        var line = string.Join(' ', command.Arguments);

        Assert.Contains("-an", command.Arguments);
        Assert.DoesNotContain("-c:a", command.Arguments);
        Assert.DoesNotContain("[aout]", command.Arguments.Where(a => a.StartsWith("[", StringComparison.Ordinal)));
        Assert.Contains("[aout]anullsink", command.FilterGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("-map [aout]", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ estimación

    [Fact]
    public void With_a_fixed_bitrate_the_size_is_the_bitrate_times_the_duration()
    {
        var settings = Settings() with
        {
            RateControl = RateControlMode.VariableBitrate,
            VideoBitrateKbps = 8000,
            AudioBitrateKbps = 192,
        };

        var bytes = ExportEstimate.SizeBytes(settings, VideoCodec.H264, TimeSpan.FromMinutes(1));

        Assert.Equal((8000 + 192) * 1000L / 8 * 60, bytes);
    }

    [Fact]
    public void Removing_the_audio_shrinks_the_estimate()
    {
        var withAudio = Settings() with { RateControl = RateControlMode.VariableBitrate, VideoBitrateKbps = 8000 };
        var without = withAudio with { IncludeAudio = false };

        Assert.True(
            ExportEstimate.SizeBytes(without, VideoCodec.H264, TimeSpan.FromMinutes(1)) <
            ExportEstimate.SizeBytes(withAudio, VideoCodec.H264, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Higher_quality_and_higher_frame_rate_cost_more_in_constant_quality()
    {
        var reference = Settings() with { Quality = 65, FrameRate = 30 };

        Assert.True(ExportEstimate.VideoKbps(reference with { Quality = 90 }, VideoCodec.H264) >
                    ExportEstimate.VideoKbps(reference, VideoCodec.H264));
        Assert.True(ExportEstimate.VideoKbps(reference with { Quality = 40 }, VideoCodec.H264) <
                    ExportEstimate.VideoKbps(reference, VideoCodec.H264));
        Assert.True(ExportEstimate.VideoKbps(reference with { FrameRate = 60 }, VideoCodec.H264) >
                    ExportEstimate.VideoKbps(reference, VideoCodec.H264));
    }

    [Fact]
    public void A_smaller_codec_gets_a_smaller_estimate()
    {
        var settings = Settings();

        Assert.True(ExportEstimate.VideoKbps(settings, VideoCodec.Hevc) < ExportEstimate.VideoKbps(settings, VideoCodec.H264));
    }
}

[Trait("Category", "Integration")]
public class SegmentedExportIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public SegmentedExportIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<MediaInfo> MakeVideoAsync(FFmpegTools tools, DirectoryInfo directory, int seconds)
    {
        var path = Path.Combine(directory.FullName, "fuente.mp4");
        var result = await ProcessRunner.RunAsync(tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate=30:duration={seconds}",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
            ],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
        return await new FFprobeService(tools).ProbeAsync(path, CancellationToken.None);
    }

    [Theory]
    [InlineData("libx264", "mp4")]
    [InlineData("libx264", "mkv")]
    [InlineData("libx265", "mp4")]
    [InlineData("h264_nvenc", "mp4")]
    public async Task A_video_is_split_into_the_expected_number_of_parts_with_the_expected_lengths(string encoder, string extension)
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var detected = await new EncoderDetector(tools).DetectAsync(CancellationToken.None);
        if (!detected.Any(e => e.Name == encoder && e.IsAvailable))
        {
            _output.WriteLine($"{encoder} no está disponible en este equipo: se omite.");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-segments-");

        try
        {
            var source = await MakeVideoAsync(tools, workspace, seconds: 11);
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(source));

            var settings = new ExportSettings
            {
                OutputPath = Path.Combine(workspace.FullName, $"salida.{extension}"),
                Resolution = VideoResolution.P480,
                EncoderName = encoder,
                Speed = EncodingSpeed.Fastest,
                FrameRate = 30,
                SegmentDuration = TimeSpan.FromSeconds(4),
            };

            var result = await new ExportJob(tools).RunAsync(sequence, settings, progress: null, CancellationToken.None);
            _output.WriteLine(result.Succeeded ? "exportación correcta" : result.ErrorMessage + "\n" + result.Command);
            Assert.True(result.Succeeded, result.ErrorMessage);

            // 11 s en trozos de 4: tres partes, de 4, 4 y 3 segundos.
            Assert.NotNull(result.Files);
            Assert.Equal(3, result.Files.Count);
            Assert.Equal(3, ExportSettings.SegmentCount(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(4)));
            Assert.False(File.Exists(settings.OutputPath), "no debe quedar además un archivo entero con el nombre base");

            var lengths = new List<double>();
            foreach (var file in result.Files)
            {
                var probed = await new FFprobeService(tools).ProbeAsync(file, CancellationToken.None);
                Assert.True(probed.HasAudio, $"{Path.GetFileName(file)} debe llevar audio");
                lengths.Add(probed.Duration.TotalSeconds);
            }

            _output.WriteLine($"{encoder}/{extension}: partes de " + string.Join(" s, ", lengths.Select(l => l.ToString("0.00"))) + " s");

            Assert.InRange(lengths[0], 3.85, 4.2);
            Assert.InRange(lengths[1], 3.85, 4.2);
            Assert.InRange(lengths[2], 2.8, 3.25);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task An_export_without_audio_has_no_audio_stream()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-noaudio-");

        try
        {
            var source = await MakeVideoAsync(tools, workspace, seconds: 3);
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(source));

            var output = Path.Combine(workspace.FullName, "mudo.mp4");
            var result = await new ExportJob(tools).RunAsync(
                sequence,
                new ExportSettings
                {
                    OutputPath = output,
                    Resolution = VideoResolution.P480,
                    EncoderName = "libx264",
                    Speed = EncodingSpeed.Fastest,
                    IncludeAudio = false,
                },
                progress: null,
                CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.False((await new FFprobeService(tools).ProbeAsync(output, CancellationToken.None)).HasAudio);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Cancelling_a_split_export_removes_the_parts_it_wrote_but_not_older_files()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-segcancel-");

        try
        {
            var source = await MakeVideoAsync(tools, workspace, seconds: 20);
            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(source));

            var settings = new ExportSettings
            {
                OutputPath = Path.Combine(workspace.FullName, "salida.mp4"),
                Resolution = VideoResolution.P2160,
                EncoderName = "libx264",
                Speed = EncodingSpeed.Slowest,
                SegmentDuration = TimeSpan.FromSeconds(2),
            };

            // Una exportación anterior con otro nombre en la misma carpeta: no debe tocarse.
            var previous = Path.Combine(workspace.FullName, "otra_001.mp4");
            await File.WriteAllTextAsync(previous, "anterior");

            using var cancellation = new CancellationTokenSource();
            var job = new ExportJob(tools);

            var exporting = job.RunAsync(
                sequence,
                settings,
                new Progress<ExportProgress>(p => { if (p.Processed > TimeSpan.FromSeconds(3)) cancellation.Cancel(); }),
                cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporting);

            Assert.False(File.Exists(settings.SegmentPath(1)));
            Assert.True(File.Exists(previous));
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}
