// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Execution;
using EditFlow.Engine.Waveforms;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Waveforms;

public class PeakAccumulatorTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    [Fact]
    public void Each_peak_is_the_maximum_magnitude_of_its_samples()
    {
        var accumulator = new PeakAccumulator(samplesPerPeak: 4);

        accumulator.Add(Pcm(100, -200, 50, 10, 0, 0, 0, 0));

        var peaks = accumulator.Finish();

        Assert.Equal(2, peaks.Length);
        Assert.Equal((200 * 255 + 16383) / 32768, peaks[0]);   // el negativo cuenta por su magnitud
        Assert.Equal(0, peaks[1]);
    }

    [Fact]
    public void Full_scale_maps_to_255_and_silence_to_0()
    {
        var accumulator = new PeakAccumulator(2);
        accumulator.Add(Pcm(short.MaxValue, 0, 0, 0));

        Assert.Equal([255, 0], accumulator.Finish());
    }

    [Fact]
    public void The_most_negative_sample_does_not_overflow()
    {
        var accumulator = new PeakAccumulator(1);
        accumulator.Add(Pcm(short.MinValue));

        Assert.Equal([255], accumulator.Finish());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public void The_result_does_not_depend_on_how_the_stream_is_chunked(int chunkSize)
    {
        // Un trozo puede cortar una muestra por la mitad: un solo byte mal alineado
        // desplazaría toda la onda siguiente.
        short[] samples = [1000, -3000, 20000, 5, -32768, 12, 0, 7000, -900, 300, 15000, -15000];
        var data = Pcm(samples);

        var whole = new PeakAccumulator(3);
        whole.Add(data);

        var chunked = new PeakAccumulator(3);
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            chunked.Add(data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset)));
        }

        Assert.Equal(whole.Finish(), chunked.Finish());
    }

    [Fact]
    public void An_incomplete_last_bucket_is_still_reported()
    {
        var accumulator = new PeakAccumulator(4);
        accumulator.Add(Pcm(16384, 0));

        var peaks = accumulator.Finish();

        Assert.Single(peaks);
        Assert.InRange(peaks[0], 126, 129);
    }

    [Fact]
    public void An_empty_stream_gives_no_peaks() =>
        Assert.Empty(new PeakAccumulator(80).Finish());
}

public class WaveformPeaksTests
{
    [Fact]
    public void The_maximum_over_the_interval_is_returned_not_the_average()
    {
        var peaks = new byte[300];
        peaks[150] = 200;   // un golpe aislado en el segundo 1,5

        Assert.Equal(200, WaveformPeaks.MaxIn(peaks, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Equal(0, WaveformPeaks.MaxIn(peaks, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void An_interval_narrower_than_one_peak_still_reads_one()
    {
        var peaks = new byte[] { 10, 90, 30 };

        Assert.Equal(90, WaveformPeaks.MaxIn(peaks, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Intervals_past_the_end_are_clamped_instead_of_failing()
    {
        var peaks = new byte[] { 10, 20, 30 };

        Assert.Equal(30, WaveformPeaks.MaxIn(peaks, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9)));
        Assert.Equal(10, WaveformPeaks.MaxIn(peaks, TimeSpan.FromSeconds(-3), TimeSpan.Zero));
    }

    [Fact]
    public void Without_peaks_the_answer_is_silence() =>
        Assert.Equal(0, WaveformPeaks.MaxIn([], TimeSpan.Zero, TimeSpan.FromSeconds(1)));
}

public class WaveformArgumentTests
{
    [Fact]
    public void The_extractor_asks_for_mono_low_rate_pcm_on_stdout()
    {
        var line = string.Join(' ', WaveformExtractor.BuildArguments("in.wav"));

        Assert.Contains("-ac 1", line, StringComparison.Ordinal);
        Assert.Contains("-ar 8000", line, StringComparison.Ordinal);
        Assert.Contains("-f s16le", line, StringComparison.Ordinal);
        Assert.EndsWith("pipe:1", line, StringComparison.Ordinal);
    }
}

[Trait("Category", "Integration")]
public class WaveformIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public WaveformIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_tone_followed_by_silence_shows_up_as_loud_then_quiet_peaks()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-wave-");

        try
        {
            var path = Path.Combine(workspace.FullName, "tono.wav");

            // Un segundo de tono y otro de silencio.
            var made = await ProcessRunner.RunAsync(
                tools.FFmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                    "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1",
                    "-filter_complex", "[0:a][1:a]concat=n=2:v=0:a=1",
                    path,
                ],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StandardError);

            var peaks = await new WaveformExtractor(tools).ExtractAsync(path);
            _output.WriteLine($"picos: {peaks.Length}");

            Assert.InRange(peaks.Length, 195, 205);
            Assert.True(WaveformPeaks.MaxIn(peaks, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.8)) > 20);   // el seno de lavfi va a 0,125 de amplitud: unos 32 de 255
            Assert.True(WaveformPeaks.MaxIn(peaks, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.9)) < 5);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task The_cache_computes_once_reuses_it_and_survives_a_new_session()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-wavecache-");

        try
        {
            var media = await Integration.AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "a.wav", 2, video: false, toneHz: 500);
            var directory = Path.Combine(workspace.FullName, "cache");

            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var cache = new WaveformCache(tools, directory))
            {
                cache.Ready += path => ready.TrySetResult(path);

                Assert.Null(cache.TryGet(media.Path));
                cache.Request(media.Path);
                cache.Request(media.Path);   // pedirla dos veces no la calcula dos veces

                Assert.Equal(media.Path, await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.NotNull(cache.TryGet(media.Path));
            }

            // Otra sesión: sale del disco, sin volver a calcular.
            using var second = new WaveformCache(tools, directory);
            var fromDisk = second.TryGet(media.Path);

            Assert.NotNull(fromDisk);
            Assert.InRange(fromDisk.Length, 195, 205);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task A_file_that_is_not_audio_fails_cleanly()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-wave-bad-");

        try
        {
            var path = Path.Combine(workspace.FullName, "roto.wav");
            await File.WriteAllBytesAsync(path, new byte[2048]);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new WaveformExtractor(tools).ExtractAsync(path));
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }
}
