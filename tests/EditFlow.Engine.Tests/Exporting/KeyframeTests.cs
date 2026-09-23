// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.PreviewCache;

namespace EditFlow.Engine.Tests.Exporting;

public class KeyframeTests
{
    private static MediaInfo Media(string path = "a.mp4", double seconds = 10) =>
        new(path, TimeSpan.FromSeconds(seconds), 1920, 1080, 30, "h264", HasAudio: true);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static KeyframeTrack Track(params (double At, double Value)[] points)
    {
        var track = KeyframeTrack.Empty;
        foreach (var (at, value) in points)
        {
            track = track.With(S(at), value);
        }

        return track;
    }

    // ------------------------------------------------------- interpolación

    [Fact]
    public void An_empty_track_falls_back_to_the_fixed_value()
    {
        Assert.Equal(1.5, KeyframeTrack.Empty.ValueAt(S(3), 1.5));
        Assert.True(KeyframeTrack.Empty.IsEmpty);
        Assert.False(KeyframeTrack.Empty.IsAnimated);
    }

    [Fact]
    public void One_point_fixes_a_value_but_does_not_animate()
    {
        var track = Track((2, 4));

        Assert.False(track.IsAnimated);
        Assert.Equal(4, track.ValueAt(S(0), 99));
        Assert.Equal(4, track.ValueAt(S(9), 99));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 15)]
    [InlineData(2, 20)]
    [InlineData(4, 20)]      // pasado el último, se queda quieto
    [InlineData(-1, 10)]     // antes del primero, también
    public void Between_two_points_the_value_moves_in_a_straight_line(double at, double expected)
    {
        Assert.Equal(expected, Track((0, 10), (2, 20)).ValueAt(S(at), 0), 3);
    }

    [Fact]
    public void Putting_a_point_where_there_is_one_replaces_it_instead_of_piling_up()
    {
        var track = Track((1, 5)).With(S(1), 8);

        Assert.Single(track.Points);
        Assert.Equal(8, track.Points[0].Value);
    }

    [Fact]
    public void Two_points_a_frame_apart_count_as_the_same_instant()
    {
        // A 60 por segundo un fotograma dura 16,7 ms. Sin esta tolerancia, mover el cabezal un
        // fotograma y corregir el valor dejaría dos puntos donde se quería uno.
        var track = KeyframeTrack.Empty.With(TimeSpan.FromMilliseconds(500), 1);
        track = track.With(TimeSpan.FromMilliseconds(504), 2);

        Assert.Single(track.Points);
        Assert.Equal(2, track.Points[0].Value);
    }

    [Fact]
    public void The_points_stay_sorted_however_they_are_added()
    {
        var track = Track((3, 30), (1, 10), (2, 20));

        Assert.Equal([S(1), S(2), S(3)], track.Points.Select(p => p.At));
    }

    // ------------------------------------------------------------- troceado

    [Fact]
    public void A_section_keeps_the_value_the_animation_had_at_each_edge()
    {
        // De 0 a 4 segundos el valor va de 0 a 40. El trozo de 1 a 3 debe empezar en 10 y
        // terminar en 30: si se tiraran los puntos de fuera, arrancaría en 0 y se vería un salto.
        var section = Track((0, 0), (4, 40)).Section(S(1), S(3));

        Assert.Equal(0, section.ValueAt(S(0), -1) - 10, 3);
        Assert.Equal(30, section.ValueAt(S(2), -1), 3);
        Assert.Equal(20, section.ValueAt(S(1), -1), 3);
    }

    [Fact]
    public void A_section_where_nothing_changes_stops_being_an_animation()
    {
        // Un tramo posterior al último punto tiene el mismo valor de principio a fin: dejarlo
        // como animación haría emitir una expresión que no hace nada.
        var section = Track((0, 5), (1, 9)).Section(S(4), S(6));

        Assert.False(section.IsAnimated);
        Assert.Equal(9, section.ValueAt(S(1), -1), 3);
    }

    [Fact]
    public void Splitting_a_clip_gives_each_half_its_own_stretch_of_the_animation()
    {
        var clip = new Clip(Media(seconds: 10));
        var animatable = (IAnimatable)clip;
        clip.Animation = Animation.None.With(AnimatedProperty.Scale, Track((0, 1), (10, 3)));

        var second = clip.SplitAt(S(5));

        Assert.NotNull(second);

        // La primera mitad va de 1 a 2; la segunda arranca en 2 y llega a 3, contando desde cero.
        Assert.Equal(1, clip.Animation.Track(AnimatedProperty.Scale).ValueAt(S(0), -1), 3);
        Assert.Equal(2, clip.Animation.Track(AnimatedProperty.Scale).ValueAt(S(5), -1), 3);
        Assert.Equal(2, second.Animation.Track(AnimatedProperty.Scale).ValueAt(S(0), -1), 3);
        Assert.Equal(3, second.Animation.Track(AnimatedProperty.Scale).ValueAt(S(5), -1), 3);
        Assert.True(animatable.Supports(AnimatedProperty.Scale));
    }

    // ------------------------------------------------------ las operaciones

    [Fact]
    public void The_first_point_drags_along_the_value_the_clip_already_had()
    {
        // Animar desde la mitad no debe hacer que la primera mitad salte de golpe al valor nuevo.
        var clip = new Clip(Media()) { Transform = new ClipTransform(1.5, 0, 0, 0) };
        var history = new UndoHistory();

        history.Do(new SetKeyframeCommand(clip, AnimatedProperty.Scale, S(4), 3));

        var track = clip.Animation.Track(AnimatedProperty.Scale);
        Assert.Equal(2, track.Points.Count);
        Assert.Equal(1.5, track.ValueAt(TimeSpan.Zero, -1), 3);
        Assert.Equal(3, track.ValueAt(S(4), -1), 3);

        history.Undo();
        Assert.True(clip.Animation.IsNone);
    }

    [Fact]
    public void Removing_the_point_that_leaves_only_one_behind_clears_the_animation()
    {
        var clip = new Clip(Media());
        new SetKeyframeCommand(clip, AnimatedProperty.Scale, TimeSpan.Zero, 1).Execute();
        new SetKeyframeCommand(clip, AnimatedProperty.Scale, S(4), 2).Execute();

        new RemoveKeyframeCommand(clip, AnimatedProperty.Scale, S(4)).Execute();

        // Queda un punto suelto, que ya no anima nada: se deja la propiedad quieta en vez de
        // conservar un resto invisible que el panel tendría que explicar.
        Assert.True(clip.Animation.IsNone);
    }

    [Fact]
    public void A_clip_refuses_to_animate_what_belongs_to_a_layer_or_to_the_audio()
    {
        var clip = new Clip(Media());

        Assert.Throws<ArgumentException>(
            () => new SetKeyframeCommand(clip, AnimatedProperty.Opacity, TimeSpan.Zero, 0.5));
        Assert.Throws<ArgumentException>(
            () => new SetKeyframeCommand(clip, AnimatedProperty.Volume, TimeSpan.Zero, -6));
    }

    // ------------------------------------------------------- la expresión

    [Fact]
    public void A_track_that_does_not_move_produces_no_expression()
    {
        Assert.Null(KeyframeExpression.Build(KeyframeTrack.Empty));
        Assert.Null(KeyframeExpression.Build(Track((1, 5))));
        Assert.Equal("7", KeyframeExpression.ValueOrExpression(KeyframeTrack.Empty, 7));
    }

    [Fact]
    public void The_expression_holds_the_first_and_last_values_outside_the_animated_stretch()
    {
        var expression = KeyframeExpression.Build(Track((1, 10), (3, 20)))!;

        Assert.StartsWith("if(lt(t,1),10,", expression, StringComparison.Ordinal);
        Assert.EndsWith("20))", expression, StringComparison.Ordinal);
        Assert.Equal(
            expression.Count(c => c == '('),
            expression.Count(c => c == ')'));
    }

    [Fact]
    public void The_offset_moves_the_expression_to_the_clock_the_filter_sees()
    {
        // La rama de una capa se compone contra el reloj de la pista principal: un punto en el
        // segundo 1 del elemento cae en el segundo 6 de la timeline si aparece en el 5.
        var expression = KeyframeExpression.Build(Track((1, 10), (3, 20)), offset: S(5))!;

        Assert.StartsWith("if(lt(t,6),10,", expression, StringComparison.Ordinal);
        Assert.Contains("lt(t,8)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void The_time_scale_lets_the_preview_open_the_decoder_halfway_through()
    {
        // El preview abre el archivo por el segundo 2 del clip: el punto que estaba en el 3 del
        // clip tiene que caer en el segundo 1 del reloj del decodificador.
        var expression = KeyframeExpression.Build(
            Track((3, 10), (5, 20)), offset: S(-2), timeScale: 1)!;

        Assert.StartsWith("if(lt(t,1),10,", expression, StringComparison.Ordinal);
        Assert.Contains("lt(t,3)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void The_geq_filter_gets_its_own_name_for_the_time()
    {
        // 'geq' llama T al instante actual; con la minúscula rechaza la expresión entera con un
        // error que no menciona el tiempo por ninguna parte.
        var expression = KeyframeExpression.Build(Track((0, 0), (1, 1)), time: "T")!;

        Assert.Contains("lt(T,", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("lt(t,", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void The_numbers_are_written_the_same_in_any_language()
    {
        var expression = KeyframeExpression.Build(Track((0.5, 1.25), (1.5, 2.75)))!;

        Assert.Contains("0.5", expression, StringComparison.Ordinal);
        Assert.Contains("1.25", expression, StringComparison.Ordinal);
        Assert.DoesNotContain(",5", expression, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- el grafo

    private static ExportSettings Settings() => new()
    {
        OutputPath = "out.mp4",
        Resolution = VideoResolution.P1080,
        EncoderName = "libx264",
        FrameRate = 30,
    };

    [Fact]
    public void A_montage_without_keyframes_produces_the_very_same_graph_as_before()
    {
        // Es la promesa que sostiene todo el bloque: añadir animaciones no puede cambiar ni un
        // carácter de lo que se exporta cuando nadie las usa.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()) { Transform = new ClipTransform(2, 0.1, 0, 0) });

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        Assert.DoesNotContain("eval=frame", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("if(lt(", graph, StringComparison.Ordinal);
        Assert.Contains("scale=3840:2160,crop=1920:1080:", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_animated_zoom_asks_FFmpeg_to_look_again_at_every_frame()
    {
        var clip = new Clip(Media());
        clip.Animation = Animation.None.With(AnimatedProperty.Scale, Track((0, 1), (4, 2)));

        var sequence = new EditSequence();
        sequence.Video.Append(clip);

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        Assert.Contains("eval=frame", graph, StringComparison.Ordinal);

        // A 4:2:0 un ancho impar no es representable, y con la escala cambiando a cada fotograma
        // el redondeo tiene que ir dentro de la expresión.
        Assert.Contains("2*floor(", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_animated_layer_moves_against_the_clock_of_the_main_track()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()));

        var track = sequence.AddOverlayTrack("V2");
        var item = OverlayItem.CreateText(new TextStyle(), S(3), S(4));
        item.Animation = Animation.None.With(AnimatedProperty.OffsetX, Track((0, 0.1), (4, 0.9)));
        Assert.True(track.TryAdd(item));

        var assets = new Dictionary<Guid, string> { [item.Id] = "titulo.png" };
        var graph = FilterGraphBuilder.Build(sequence, Settings(), assets).FilterGraph;

        // El elemento aparece en el segundo 3, así que su primer punto cae ahí y el último en el 7.
        Assert.Contains("overlay=x='main_w*(if(lt(t,3),0.1,", graph, StringComparison.Ordinal);
        Assert.Contains("lt(t,7)", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void An_animated_opacity_multiplies_the_alpha_that_was_already_there()
    {
        // Así se compone con el recorte del fondo y con el fundido en vez de pisarlos.
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()));

        var track = sequence.AddOverlayTrack("V2");
        var item = OverlayItem.CreateText(new TextStyle(), TimeSpan.Zero, S(4));
        item.Animation = Animation.None.With(AnimatedProperty.Opacity, Track((0, 0), (4, 1)));
        Assert.True(track.TryAdd(item));

        var assets = new Dictionary<Guid, string> { [item.Id] = "titulo.png" };
        var graph = FilterGraphBuilder.Build(sequence, Settings(), assets).FilterGraph;

        Assert.Contains("geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("colorchannelmixer", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Automated_volume_is_converted_from_decibels_inside_the_expression()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media()));

        var audioTrack = sequence.AddAudioTrack("A1");
        var audio = new AudioClip(Media("musica.wav"), TimeSpan.Zero, S(6), TimeSpan.Zero);
        audio.Animation = Animation.None.With(AnimatedProperty.Volume, Track((0, 0), (3, -20)));
        Assert.True(audioTrack.TryAdd(audio));

        var graph = FilterGraphBuilder.Build(sequence, Settings()).FilterGraph;

        // El filtro multiplica por un factor lineal y los puntos se guardan en dB.
        Assert.Contains("volume='pow(10,(", graph, StringComparison.Ordinal);
        Assert.Contains(")/20)':eval=frame", graph, StringComparison.Ordinal);
    }

    // --------------------------------------------------- copia de preview

    [Fact]
    public void Moving_a_point_invalidates_the_rendered_preview()
    {
        var settings = PreviewCacheSettings.For(540, 30);

        Assert.NotEqual(
            HashOf(Track((0, 1), (4, 2)), settings),
            HashOf(Track((0, 1), (4, 3)), settings));
    }

    [Fact]
    public void Two_animated_clips_of_the_same_file_are_no_longer_treated_as_one()
    {
        // Sin animar, dos fragmentos seguidos del mismo archivo cuentan como uno para la huella.
        // Con animación no pueden: cada uno recorre sus puntos desde su propio inicio.
        var settings = PreviewCacheSettings.For(540, 30);
        var media = Media(seconds: 20);

        var plain = new EditSequence();
        plain.Video.Append(new Clip(media, TimeSpan.Zero, S(5)));
        plain.Video.Append(new Clip(media, S(5), S(10)));

        var animated = new EditSequence();
        var first = new Clip(media, TimeSpan.Zero, S(5));
        var second = new Clip(media, S(5), S(10));
        first.Animation = Animation.None.With(AnimatedProperty.Scale, Track((0, 1), (5, 2)));
        second.Animation = first.Animation;
        animated.Video.Append(first);
        animated.Video.Append(second);

        Assert.NotEqual(Hash(plain, settings), Hash(animated, settings));
    }

    private static string HashOf(KeyframeTrack track, PreviewCacheSettings settings)
    {
        var sequence = new EditSequence();
        var clip = new Clip(Media());
        clip.Animation = Animation.None.With(AnimatedProperty.Scale, track);
        sequence.Video.Append(clip);

        return Hash(sequence, settings);
    }

    private static string Hash(EditSequence sequence, PreviewCacheSettings settings) =>
        SectionHasher.Compute(
            SequenceSlicer.Slice(sequence, TimeSpan.Zero, S(10)), settings, _ => "sin-archivo");
}
