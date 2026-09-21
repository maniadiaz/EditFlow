// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Engine.Proxies;

namespace EditFlow.Engine.Tests.Proxies;

public class ProxyPolicyTests
{
    private static MediaInfo Media(int width, int height, string codec = "h264") =>
        new("v.mp4", TimeSpan.FromSeconds(10), width, height, 30, codec, true);

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    public void Anything_above_720p_gets_a_proxy(int width, int height) =>
        Assert.True(ProxyPolicy.NeedsProxy(Media(width, height)));

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(854, 480)]
    [InlineData(640, 360)]
    public void Small_h264_does_not(int width, int height) =>
        Assert.False(ProxyPolicy.NeedsProxy(Media(width, height)));

    [Fact]
    public void A_vertical_1080p_phone_clip_counts_by_pixels_not_orientation() =>
        Assert.True(ProxyPolicy.NeedsProxy(Media(1080, 1920)));

    [Theory]
    [InlineData("hevc")]
    [InlineData("av1")]
    [InlineData("prores")]
    public void Heavy_codecs_get_one_even_at_720p(string codec) =>
        Assert.True(ProxyPolicy.NeedsProxy(Media(1280, 720, codec)));

    [Fact]
    public void Heavy_codecs_at_very_low_resolution_do_not() =>
        Assert.False(ProxyPolicy.NeedsProxy(Media(640, 360, "hevc")));

    [Fact]
    public void Audio_only_media_never_does() =>
        Assert.False(ProxyPolicy.NeedsProxy(new MediaInfo("a.wav", TimeSpan.FromSeconds(5), 0, 0, 0, string.Empty, true)));
}

public sealed class ProxyCacheTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-cache-");
    private readonly ProxyCache _cache;

    public ProxyCacheTests() => _cache = new ProxyCache(Path.Combine(_workspace.FullName, "cache"));

    public void Dispose() => _workspace.DeleteWithRetry();

    private string Source(string name, string content = "x")
    {
        var path = Path.Combine(_workspace.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Proxy(string name, int bytes, DateTime lastWrite)
    {
        Directory.CreateDirectory(_cache.Directory);
        var path = Path.Combine(_cache.Directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWrite);
        return path;
    }

    [Fact]
    public void The_same_file_always_maps_to_the_same_proxy()
    {
        var source = Source("a.mp4");
        Assert.Equal(_cache.PathFor(source), _cache.PathFor(source));
    }

    [Fact]
    public void Different_files_map_to_different_proxies()
    {
        Assert.NotEqual(_cache.PathFor(Source("a.mp4")), _cache.PathFor(Source("b.mp4")));
    }

    [Fact]
    public void Changing_the_original_invalidates_its_proxy()
    {
        // Una copia vieja de un video que se ha vuelto a exportar mostraría imagen que ya no existe.
        var source = Source("a.mp4", "uno");
        var before = _cache.PathFor(source);

        File.WriteAllText(source, "contenido distinto y más largo");

        Assert.NotEqual(before, _cache.PathFor(source));
    }

    [Fact]
    public void A_missing_original_has_no_proxy_path()
    {
        Assert.Null(_cache.PathFor(Path.Combine(_workspace.FullName, "no-existe.mp4")));
    }

    [Fact]
    public void The_proxy_name_does_not_reveal_the_original_path()
    {
        var name = Path.GetFileName(_cache.PathFor(Source("mis-vacaciones-privadas.mp4"))!);
        Assert.DoesNotContain("vacaciones", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGet_finds_only_complete_proxies()
    {
        var source = Source("a.mp4");
        Assert.False(_cache.TryGet(source, out _));

        Directory.CreateDirectory(_cache.Directory);
        File.WriteAllBytes(_cache.PathFor(source)!, []);
        Assert.False(_cache.TryGet(source, out _), "un archivo vacío no es una copia");

        File.WriteAllBytes(_cache.PathFor(source)!, [1, 2, 3]);
        Assert.True(_cache.TryGet(source, out var path));
        Assert.Equal(_cache.PathFor(source), path);
    }

    [Fact]
    public void Trimming_removes_the_least_recently_used_first()
    {
        var old = Proxy("old.mp4", 1000, DateTime.UtcNow.AddDays(-3));
        var mid = Proxy("mid.mp4", 1000, DateTime.UtcNow.AddDays(-2));
        var fresh = Proxy("fresh.mp4", 1000, DateTime.UtcNow.AddDays(-1));

        var freed = _cache.TrimTo(1500);

        Assert.Equal(2000, freed);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(mid));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Trimming_under_the_limit_removes_nothing()
    {
        var path = Proxy("a.mp4", 1000, DateTime.UtcNow);

        Assert.Equal(0, _cache.TrimTo(5000));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Stale_partial_files_are_always_removed()
    {
        var stale = Proxy("x.mp4.partial", 10, DateTime.UtcNow.AddHours(-3));
        var running = Proxy("y.mp4.partial", 10, DateTime.UtcNow);

        _cache.TrimTo(long.MaxValue);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(running), "una generación en curso no debe borrarse");
    }

    [Fact]
    public void Trimming_a_cache_that_does_not_exist_yet_is_harmless()
    {
        Assert.Equal(0, _cache.TrimTo(0));
        Assert.Equal(0, _cache.SizeBytes());
    }
}

public class ProxyArgumentTests
{
    [Fact]
    public void The_proxy_is_480p_without_audio_and_keeps_timestamps()
    {
        var args = ProxyGenerator.BuildArguments("in.mp4", "out.mp4.partial");
        var line = string.Join(' ', args);

        Assert.Contains("scale=-2:480", line, StringComparison.Ordinal);
        Assert.Contains("-an", args);
        Assert.Contains("-fps_mode passthrough", line, StringComparison.Ordinal);
        Assert.Equal("out.mp4.partial", args[^1]);
    }

    [Fact]
    public void The_proxy_is_built_for_fast_seeking()
    {
        var line = string.Join(' ', ProxyGenerator.BuildArguments("in.mp4", "o.mp4"));

        // Sin fotogramas B y con un fotograma clave cada 12: buscar no obliga a decodificar
        // hacia delante mucho antes de poder mostrar imagen.
        Assert.Contains("-g 12", line, StringComparison.Ordinal);
        Assert.Contains("-bf 0", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_spaces_stays_a_single_argument()
    {
        var args = ProxyGenerator.BuildArguments("mi video.mp4", "sal ida.mp4");

        Assert.Contains("mi video.mp4", args);
    }
}
