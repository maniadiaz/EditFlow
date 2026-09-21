// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Probing;

namespace EditFlow.Engine.Tests.Probing;

/// <summary>
/// Verifica el análisis de la salida de ffprobe contra JSON capturado de archivos reales.
/// </summary>
public class FFprobeServiceTests
{
    private static string Fixture(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string NtscWithAudio => Fixture("ffprobe-ntsc-with-audio.json");
    private static string RotatedNoAudio => Fixture("ffprobe-rotated-no-audio.json");

    [Fact]
    public void Reads_the_video_stream_and_not_the_audio_one()
    {
        // En el formato clave=valor de ffprobe, 'codec_name' llega antes que
        // 'codec_type', así que un parser secuencial acaba atribuyendo al video el
        // códec del audio: "aac" en lugar de "h264". El JSON agrupa cada flujo en su
        // propio objeto y elimina la ambigüedad.
        var info = FFprobeService.Parse(NtscWithAudio, "ntsc.mp4");

        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
    }

    [Fact]
    public void Detects_the_presence_of_audio()
    {
        Assert.True(FFprobeService.Parse(NtscWithAudio, "ntsc.mp4").HasAudio);
        Assert.False(FFprobeService.Parse(RotatedNoAudio, "rotated.mp4").HasAudio);
    }

    [Fact]
    public void Frame_rate_keeps_its_fractional_precision()
    {
        // NTSC son 30000/1001 = 29,97 fps. Redondearlo a 30 introduce una deriva de
        // audio de aproximadamente un segundo cada media hora de metraje.
        var info = FFprobeService.Parse(NtscWithAudio, "ntsc.mp4");

        Assert.Equal(29.97, info.FrameRate, precision: 2);
        Assert.NotEqual(30, info.FrameRate);
    }

    [Fact]
    public void Reads_the_duration_from_the_container()
    {
        var info = FFprobeService.Parse(NtscWithAudio, "ntsc.mp4");

        Assert.Equal(2, info.Duration.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Reads_rotation_metadata()
    {
        var info = FFprobeService.Parse(RotatedNoAudio, "rotated.mp4");

        Assert.Equal(90, info.Rotation);
    }

    [Fact]
    public void Rotation_turns_stored_dimensions_into_display_dimensions()
    {
        // El archivo se almacena en 640x360 con una marca de 90 grados: así graba un
        // móvil en vertical. Sin corregirlo, se trataría como apaisado.
        var info = FFprobeService.Parse(RotatedNoAudio, "rotated.mp4");

        Assert.Equal(640, info.Width);
        Assert.Equal(360, info.Height);
        Assert.Equal(360, info.DisplayWidth);
        Assert.Equal(640, info.DisplayHeight);
        Assert.True(info.IsPortrait);
    }

    [Fact]
    public void Legacy_rotate_tags_are_still_understood()
    {
        // Las grabaciones antiguas llevan la rotación en un tag en lugar de en
        // side_data_list. Ambas formas conviven en el parque de archivos reales.
        const string json =
            """
            {
              "streams": [
                {
                  "codec_type": "video",
                  "codec_name": "h264",
                  "width": 1920,
                  "height": 1080,
                  "r_frame_rate": "30/1",
                  "tags": { "rotate": "270" }
                }
              ],
              "format": { "duration": "5.000000" }
            }
            """;

        var info = FFprobeService.Parse(json, "legacy.mp4");

        Assert.Equal(270, info.Rotation);
        Assert.True(info.IsPortrait);
    }

    [Fact]
    public void Negative_rotation_is_normalised()
    {
        const string json =
            """
            {
              "streams": [
                {
                  "codec_type": "video", "codec_name": "h264",
                  "width": 1920, "height": 1080, "r_frame_rate": "30/1",
                  "side_data_list": [ { "rotation": -90 } ]
                }
              ],
              "format": { "duration": "1.0" }
            }
            """;

        var info = FFprobeService.Parse(json, "negative.mp4");

        Assert.Equal(270, info.Rotation);
    }

    [Fact]
    public void An_embedded_cover_image_is_not_mistaken_for_the_video()
    {
        // Muchos archivos llevan una carátula incrustada, que se declara como flujo de
        // video. Tomarla por el video real daría unas dimensiones equivocadas.
        const string json =
            """
            {
              "streams": [
                {
                  "codec_type": "video", "codec_name": "mjpeg",
                  "width": 600, "height": 600, "r_frame_rate": "90000/1",
                  "disposition": { "attached_pic": 1 }
                },
                {
                  "codec_type": "video", "codec_name": "h264",
                  "width": 1920, "height": 1080, "r_frame_rate": "25/1",
                  "disposition": { "attached_pic": 0 }
                }
              ],
              "format": { "duration": "12.0" }
            }
            """;

        var info = FFprobeService.Parse(json, "withcover.mp4");

        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(1920, info.Width);
    }

    [Fact]
    public void A_file_without_video_is_reported_clearly()
    {
        const string json =
            """
            { "streams": [ { "codec_type": "audio", "codec_name": "mp3" } ],
              "format": { "duration": "180.0" } }
            """;

        var error = Assert.Throws<InvalidOperationException>(
            () => FFprobeService.Parse(json, "song.mp3"));

        Assert.Contains("song.mp3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_duration_does_not_throw()
    {
        const string json =
            """
            { "streams": [ { "codec_type": "video", "codec_name": "h264",
                             "width": 640, "height": 480, "r_frame_rate": "30/1" } ],
              "format": {} }
            """;

        var info = FFprobeService.Parse(json, "noduration.mp4");

        Assert.Equal(TimeSpan.Zero, info.Duration);
    }

    [Fact]
    public void A_degenerate_frame_rate_does_not_divide_by_zero()
    {
        // Los flujos de audio declaran "0/0"; un video mal formado puede hacerlo también.
        const string json =
            """
            { "streams": [ { "codec_type": "video", "codec_name": "h264",
                             "width": 640, "height": 480, "r_frame_rate": "0/0" } ],
              "format": { "duration": "1.0" } }
            """;

        var info = FFprobeService.Parse(json, "broken.mp4");

        Assert.Equal(0, info.FrameRate);
    }
}

public class FFprobeAudioOnlyTests
{
    private const string Mp3 =
        """
        { "streams": [ { "codec_type": "audio", "codec_name": "mp3" } ],
          "format": { "duration": "183.5" } }
        """;

    [Fact]
    public void An_audio_file_is_accepted_when_audio_is_allowed()
    {
        var info = FFprobeService.Parse(Mp3, "cancion.mp3", allowAudioOnly: true);

        Assert.True(info.HasAudio);
        Assert.Equal(183.5, info.Duration.TotalSeconds, precision: 2);
        Assert.Equal("none", info.VideoCodec);
    }

    [Fact]
    public void An_audio_file_has_no_picture_to_measure()
    {
        // Dimensiones y cadencia a cero es lo que distingue un audio de un video.
        var info = FFprobeService.Parse(Mp3, "cancion.mp3", allowAudioOnly: true);

        Assert.Equal(0, info.Width);
        Assert.Equal(0, info.Height);
        Assert.Equal(0, info.FrameRate);
    }

    [Fact]
    public void Importing_a_video_still_refuses_an_audio_only_file()
    {
        // Aceptar un mp3 al importar video lo dejaría entrar en la pista de video como un
        // clip sin nada que mostrar. Por eso el modo de audio es explícito y no el defecto.
        Assert.Throws<InvalidOperationException>(() => FFprobeService.Parse(Mp3, "cancion.mp3"));
    }

    [Fact]
    public void A_video_file_is_read_the_same_way_in_both_modes()
    {
        const string video =
            """
            { "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "30/1" },
                { "codec_type": "audio", "codec_name": "aac" } ],
              "format": { "duration": "12.0" } }
            """;

        var strict = FFprobeService.Parse(video, "v.mp4");
        var lenient = FFprobeService.Parse(video, "v.mp4", allowAudioOnly: true);

        Assert.Equal(strict, lenient);
    }

    [Fact]
    public void A_file_with_neither_video_nor_audio_is_still_refused()
    {
        const string neither = """{ "streams": [ { "codec_type": "subtitle" } ], "format": { "duration": "5" } }""";

        Assert.Throws<InvalidOperationException>(
            () => FFprobeService.Parse(neither, "x.srt", allowAudioOnly: true));
    }

    [Fact]
    public void An_audio_file_with_no_declared_duration_does_not_throw()
    {
        const string noLength = """{ "streams": [ { "codec_type": "audio", "codec_name": "aac" } ], "format": {} }""";

        var info = FFprobeService.Parse(noLength, "x.aac", allowAudioOnly: true);

        Assert.Equal(TimeSpan.Zero, info.Duration);
    }
}
