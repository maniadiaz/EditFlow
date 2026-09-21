// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Probing;
using EditFlow.Engine.Proxies;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>Genera copias de edición con FFmpeg de verdad y comprueba lo que salió.</summary>
[Trait("Category", "Integration")]
public class ProxyIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ProxyIntegrationTests(ITestOutputHelper output) => _output = output;

    private static async Task<MediaInfo> MakeHdSourceAsync(FFmpegTools tools, DirectoryInfo directory, double seconds)
    {
        var path = Path.Combine(directory.FullName, "original-hd.mp4");
        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:rate=30:duration={seconds}",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", path,
            ],
            CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);

        return await new FFprobeService(tools).ProbeAsync(path, CancellationToken.None);
    }

    [Fact]
    public async Task The_proxy_is_480p_has_no_audio_and_lasts_as_long_as_the_original()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-proxy-");

        try
        {
            var source = await MakeHdSourceAsync(tools, workspace, 4);
            var output = Path.Combine(workspace.FullName, "cache", "p.mp4");
            var reports = new List<double>();

            await new ProxyGenerator(tools).GenerateAsync(
                source.Path, output, source.Duration, reports.Add, CancellationToken.None);

            Assert.True(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));

            var proxy = await new FFprobeService(tools).ProbeAsync(output, CancellationToken.None);
            _output.WriteLine(
                $"proxy {proxy.Width}x{proxy.Height}, {proxy.Duration.TotalSeconds:0.###} s " +
                $"(original {source.Duration.TotalSeconds:0.###} s), {new FileInfo(output).Length / 1024} KB, " +
                $"avisos de progreso: {reports.Count}");

            Assert.Equal(480, proxy.Height);
            Assert.False(proxy.HasAudio);

            // La duración debe coincidir: si la copia se desplazara, los cortes del preview
            // caerían en otro sitio que en la exportación.
            Assert.InRange(proxy.Duration.TotalSeconds, source.Duration.TotalSeconds - 0.15, source.Duration.TotalSeconds + 0.15);
            Assert.Equal(100, reports[^1]);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_manager_generates_in_the_background_and_then_resolves_to_the_proxy()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-proxy-mgr-");

        try
        {
            var source = await MakeHdSourceAsync(tools, workspace, 3);
            var cache = new ProxyCache(Path.Combine(workspace.FullName, "cache"));
            using var manager = new ProxyManager(tools, cache);

            var ready = new TaskCompletionSource<ProxyUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Updated += update =>
            {
                if (update.State is ProxyState.Ready or ProxyState.Failed)
                {
                    ready.TrySetResult(update);
                }
            };

            // Mientras no está lista, se usa el original.
            Assert.Equal(source.Path, manager.Resolve(source.Path));

            Assert.True(manager.Request(source));
            Assert.False(manager.Request(source), "pedirla dos veces no debe encolarla dos veces");

            var update = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(ProxyState.Ready, update.State);
            Assert.Equal(0, update.Pending);
            Assert.NotEqual(source.Path, manager.Resolve(source.Path));
            Assert.True(File.Exists(manager.Resolve(source.Path)));

            // Una vez lista, ya no hace falta pedirla.
            Assert.False(manager.Request(source));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_broken_file_fails_cleanly_and_leaves_no_proxy()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-proxy-bad-");

        try
        {
            var broken = Path.Combine(workspace.FullName, "roto.mp4");
            await File.WriteAllBytesAsync(broken, new byte[2048]);
            var output = Path.Combine(workspace.FullName, "p.mp4");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ProxyGenerator(tools).GenerateAsync(broken, output, TimeSpan.FromSeconds(1)));

            _output.WriteLine(error.Message);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
