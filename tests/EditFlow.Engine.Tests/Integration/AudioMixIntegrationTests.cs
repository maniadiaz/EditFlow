// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Exporta con FFmpeg de verdad y mide el resultado: lo que suena, no lo que se generó.
/// </summary>
/// <remarks>
/// Comprobar la cadena del grafo dice que se escribió lo esperado, no que FFmpeg lo
/// entienda ni que el sonido salga como se pretendía. Aquí se mide el volumen del
/// archivo final.
/// </remarks>
[Trait("Category", "Integration")]
public partial class AudioMixIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public AudioMixIntegrationTests(ITestOutputHelper output) => _output = output;

    [GeneratedRegex(@"mean_volume:\s*(-?[0-9.]+|-inf)\s*dB")]
    private static partial Regex MeanVolumePattern();

    private static async Task<MediaInfo> MakeMediaAsync(
        FFmpegTools tools, DirectoryInfo directory, string name, double seconds, bool video, double? toneHz)
    {
        var path = Path.Combine(directory.FullName, name);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        if (video)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=30:duration={duration}"]);
        }

        if (toneHz is { } hz)
        {
            arguments.AddRange(["-f", "lavfi", "-i",
                $"sine=frequency={hz.ToString(CultureInfo.InvariantCulture)}:duration={duration}"]);
        }

        if (video)
        {
            arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        }

        if (toneHz is not null)
        {
            // Siempre estéreo. Con una fuente mono, pasarla a estéreo ya baja 3 dB por sí sola
            // (la ley de panorama reparte la potencia entre los dos altavoces), y eso
            // enmascararía lo que estos tests quieren medir: el efecto de la mezcla.
            arguments.AddRange(video
                ? ["-ac", "2", "-c:a", "aac", "-shortest"]
                : ["-ac", "2", "-c:a", "pcm_s16le"]);
        }

        arguments.Add(path);

        var result = await ProcessRunner.RunAsync(tools.FFmpegPath, arguments, CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);

        return new MediaInfo(path, TimeSpan.FromSeconds(seconds), 320, 180, 30, "h264", toneHz is not null);
    }

    private static async Task<double> MeanVolumeAsync(FFmpegTools tools, string path)
    {
        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            ["-hide_banner", "-i", path, "-vn", "-af", "volumedetect", "-f", "null", "-"],
            CancellationToken.None);

        var match = MeanVolumePattern().Match(result.StandardError);
        Assert.True(match.Success, "volumedetect no informó del volumen:\n" + result.StandardError);

        return match.Groups[1].Value == "-inf"
            ? double.NegativeInfinity
            : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static ExportSettings Settings(string output) => new()
    {
        OutputPath = output,
        Resolution = VideoResolution.P480,
        EncoderName = "libx264",
        Speed = EncodingSpeed.Fastest,
        FrameRate = 30,
    };

    private async Task<ExportResult> ExportAsync(FFmpegTools tools, EditSequence sequence, string output)
    {
        var result = await new ExportJob(tools).RunAsync(
            sequence, Settings(output), progress: null, CancellationToken.None);

        _output.WriteLine($"exportación: {(result.Succeeded ? "correcta" : result.ErrorMessage)}");
        if (!result.Succeeded)
        {
            _output.WriteLine(result.Command);
        }

        return result;
    }

    [Fact]
    public async Task Music_placed_later_is_mixed_and_extends_the_export()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-mix-");

        try
        {
            var video = await MakeMediaAsync(tools, workspace, "video.mp4", 4, video: true, toneHz: 200);
            var music = await MakeMediaAsync(tools, workspace, "musica.wav", 6, video: false, toneHz: 1000);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(video));

            var clip = new AudioClip(music, TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1))
            {
                GainDb = -6,
                FadeIn = TimeSpan.FromSeconds(1),
                FadeOut = TimeSpan.FromSeconds(1),
            };
            sequence.AddAudioTrack("Música").TryAdd(clip);

            var output = Path.Combine(workspace.FullName, "mezcla.mp4");
            var result = await ExportAsync(tools, sequence, output);
            Assert.True(result.Succeeded, result.ErrorMessage);

            var probed = await new FFprobeService(tools).ProbeAsync(output, CancellationToken.None);
            _output.WriteLine($"duración: {probed.Duration.TotalSeconds:0.##} s, audio: {probed.HasAudio}");

            // La música empieza en 1 s y dura 6: el archivo debe llegar a los 7 segundos,
            // aunque el video solo tenga 4. La imagen se extiende con negro.
            Assert.True(probed.HasAudio);
            Assert.InRange(probed.Duration.TotalSeconds, 6.7, 7.4);

            var mean = await MeanVolumeAsync(tools, output);
            _output.WriteLine($"volumen medio: {mean:0.#} dB");
            Assert.True(mean > -50, $"el archivo suena casi mudo ({mean:0.#} dB): la mezcla no funcionó");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Detached_audio_is_not_played_twice()
    {
        // El audio del video se separa a una pista, y ese clip de audio se silencia. Si el
        // clip de video siguiera aportando su propio sonido, el archivo no sería mudo.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-detach-");

        try
        {
            var video = await MakeMediaAsync(tools, workspace, "v.mp4", 3, video: true, toneHz: 440);

            var sequence = new EditSequence();
            var clip = new Clip(video);
            sequence.Video.Append(clip);
            var detach = new DetachAudioCommand(sequence, clip);
            detach.Execute();
            Assert.NotNull(detach.Result);

            var control = Path.Combine(workspace.FullName, "con-audio.mp4");
            Assert.True((await ExportAsync(tools, sequence, control)).Succeeded);
            var withAudio = await MeanVolumeAsync(tools, control);

            detach.Result.IsMuted = true;
            var silenced = Path.Combine(workspace.FullName, "silenciado.mp4");
            Assert.True((await ExportAsync(tools, sequence, silenced)).Succeeded);
            var muted = await MeanVolumeAsync(tools, silenced);

            _output.WriteLine($"con la pista sonando: {withAudio:0.#} dB   pista silenciada: {muted:0.#} dB");

            Assert.True(withAudio > -50, "el audio separado debería oírse desde su pista");
            Assert.True(muted < -80, $"silenciada la pista debería quedar mudo, pero da {muted:0.#} dB");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixing_a_track_does_not_lower_the_level_of_the_others()
    {
        // Por defecto amix divide cada entrada entre el número de entradas: mezclar una
        // pista con silencio bajaría 6 dB. Con normalize=0 el nivel debe quedar igual.
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-level-");

        try
        {
            var video = await MakeMediaAsync(tools, workspace, "v.mp4", 4, video: true, toneHz: null);
            var music = await MakeMediaAsync(tools, workspace, "tono.wav", 4, video: false, toneHz: 1000);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(video));
            sequence.AddAudioTrack().TryAdd(
                new AudioClip(music, TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.Zero));

            var output = Path.Combine(workspace.FullName, "nivel.mp4");
            Assert.True((await ExportAsync(tools, sequence, output)).Succeeded);

            var original = await MeanVolumeAsync(tools, music.Path);
            var mixed = await MeanVolumeAsync(tools, output);
            _output.WriteLine($"tono original: {original:0.#} dB   tras mezclar: {mixed:0.#} dB   diferencia: {original - mixed:0.#} dB");

            // La compresión AAC mueve el valor una fracción de dB. Una normalización
            // activa lo habría bajado 6 dB, así que 1,5 dB de margen distingue bien.
            Assert.InRange(original - mixed, -1.5, 1.5);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Solo_exports_only_the_soloed_track()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-solo-");

        try
        {
            var video = await MakeMediaAsync(tools, workspace, "v.mp4", 3, video: true, toneHz: null);
            var loud = await MakeMediaAsync(tools, workspace, "fuerte.wav", 3, video: false, toneHz: 1000);

            var sequence = new EditSequence();
            sequence.Video.Append(new Clip(video));

            // Una pista con sonido y otra en solo pero sin nada: al activar el solo, la
            // primera calla aunque no esté silenciada, y el resultado debe ser mudo.
            sequence.AddAudioTrack("Suena").TryAdd(
                new AudioClip(loud, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.Zero));
            sequence.AddAudioTrack("Solo vacía").IsSolo = true;

            var output = Path.Combine(workspace.FullName, "solo.mp4");
            Assert.True((await ExportAsync(tools, sequence, output)).Succeeded);

            var mean = await MeanVolumeAsync(tools, output);
            _output.WriteLine($"volumen con el solo en una pista vacía: {mean:0.#} dB");

            Assert.True(mean < -80, $"la pista con sonido debía callar con el solo activo, pero da {mean:0.#} dB");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
