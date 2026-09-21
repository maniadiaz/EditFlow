// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Filmstrips;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Filmstrips;

public class FilmstripArgumentTests
{
    [Fact]
    public void One_frame_is_taken_every_interval_and_scaled_to_the_strip_height()
    {
        var line = string.Join(' ', FilmstripCache.BuildArguments("in.mp4", "out/%05d.jpg"));

        Assert.Contains("fps=1/2", line, StringComparison.Ordinal);
        Assert.Contains("scale=-2:54", line, StringComparison.Ordinal);
        Assert.Contains("-an", line, StringComparison.Ordinal);
        Assert.EndsWith("out/%05d.jpg", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_work_is_limited_to_two_threads()
    {
        var args = FilmstripCache.BuildArguments("in.mp4", "o.jpg").ToList();

        Assert.Equal("2", args[args.IndexOf("-threads") + 1]);
    }
}

[Trait("Category", "Integration")]
public class FilmstripIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public FilmstripIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Frames_are_generated_in_the_background_and_reused_by_the_next_session()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-strip-");

        try
        {
            var video = await Integration.AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "v.mp4", 7, video: true, toneHz: null);
            var directory = Path.Combine(workspace.FullName, "strips");

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var cache = new FilmstripCache(tools, directory))
            {
                cache.Updated += _ =>
                {
                    // Cuando existe el último fotograma esperado, el trabajo terminó.
                    if (cache.FrameAt(video.Path, TimeSpan.FromSeconds(6)) is { } last && File.Exists(last))
                    {
                        done.TrySetResult();
                    }
                };

                Assert.Null(cache.FrameAt(video.Path, TimeSpan.Zero));   // aún no hay nada
                cache.Request(video.Path);
                cache.Request(video.Path);                               // no se encola dos veces

                await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

                var first = cache.FrameAt(video.Path, TimeSpan.Zero);
                Assert.NotNull(first);
                Assert.True(File.Exists(first));
                Assert.EndsWith("00001.jpg", first, StringComparison.Ordinal);

                // 7 s a un fotograma cada 2: instantes 0, 2, 4 y 6.
                var at5 = cache.FrameAt(video.Path, TimeSpan.FromSeconds(5));
                Assert.EndsWith("00004.jpg", at5!, StringComparison.Ordinal);   // 5 / 2 = 2,5 → casilla 3 (índice 3)
            }

            // Otra sesión: la carpeta completa se reutiliza al momento, sin lanzar FFmpeg.
            using var second = new FilmstripCache(tools, directory);
            second.Request(video.Path);

            Assert.NotNull(second.FrameAt(video.Path, TimeSpan.FromSeconds(4)));
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Asking_beyond_the_end_uses_the_last_frame_instead_of_a_missing_one()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-strip-end-");

        try
        {
            var video = await Integration.AudioMixIntegrationTests.MakeMediaAsync(
                tools, workspace, "v.mp4", 5, video: true, toneHz: null);

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cache = new FilmstripCache(tools, Path.Combine(workspace.FullName, "strips"));
            cache.Updated += _ =>
            {
                if (cache.FrameAt(video.Path, TimeSpan.FromSeconds(4)) is { } p && File.Exists(p))
                {
                    done.TrySetResult();
                }
            };

            cache.Request(video.Path);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var beyond = cache.FrameAt(video.Path, TimeSpan.FromSeconds(500));

            Assert.NotNull(beyond);
            Assert.True(File.Exists(beyond), "la casilla pedida debe existir");
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Old_folders_are_trimmed_and_recent_ones_kept()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("editflow-strip-trim-");

        try
        {
            var old = Directory.CreateDirectory(Path.Combine(directory.FullName, "viejo"));
            var fresh = Directory.CreateDirectory(Path.Combine(directory.FullName, "reciente"));
            old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-60);

            using var cache = new FilmstripCache(tools, directory.FullName);

            Assert.Equal(1, cache.TrimUnusedFor(TimeSpan.FromDays(30)));
            Assert.False(old.Exists && Directory.Exists(old.FullName));
            Assert.True(Directory.Exists(fresh.FullName));
            await Task.CompletedTask;
        }
        finally
        {
            directory.DeleteWithRetry();
        }
    }
}
