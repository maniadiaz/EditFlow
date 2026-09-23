// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class ColorGradeTests
{
    private static MediaInfo Media() =>
        new("a.mp4", TimeSpan.FromSeconds(10), 1920, 1080, 30, "h264", HasAudio: true);

    // ------------------------------------------------------------ el valor

    [Fact]
    public void A_clip_starts_with_nothing_corrected()
    {
        Assert.True(ColorGrade.None.IsNone);
        Assert.Null(ColorGradeFilter.Build(ColorGrade.None));
        Assert.Null(ColorGradeFilter.Build(null));
        Assert.True(new Clip(Media()).Grade.IsNone);
    }

    [Fact]
    public void A_curve_whose_points_sit_on_the_diagonal_changes_nothing()
    {
        var flat = ToneCurve.FromPoints([new CurvePoint(0, 0), new CurvePoint(0.5, 0.5), new CurvePoint(1, 1)]);

        Assert.True(flat.IsIdentity);
        Assert.True(new ColorGrade(Master: flat).IsNone);
    }

    [Fact]
    public void Curve_points_are_sorted_clamped_and_deduplicated()
    {
        var curve = ToneCurve.FromPoints(
        [
            new CurvePoint(1, 2),        // fuera de rango por arriba
            new CurvePoint(0.5, 0.7),
            new CurvePoint(-1, 0.1),     // fuera de rango por abajo
            new CurvePoint(0.5, 0.8),    // repite entrada: gana el último
        ]);

        Assert.Equal(3, curve.Points.Count);
        Assert.Equal([0, 0.5, 1], curve.Points.Select(p => p.In));
        Assert.Equal(0.8, curve.Points[1].Out, 3);
        Assert.Equal(1, curve.Points[2].Out, 3);
    }

    [Fact]
    public void A_curve_is_written_the_way_FFmpeg_reads_it()
    {
        var curve = ToneCurve.FromPoints([new CurvePoint(0, 0.1), new CurvePoint(1, 0.9)]);

        // En una máquina en español, "0,1" haría que FFmpeg leyera un punto en 0 y otro en 1.
        Assert.Equal("0/0.1 1/0.9", curve.ToFilterValue());
    }

    // ------------------------------------------------------------ el filtro

    [Fact]
    public void The_shadow_wheel_lifts_the_black_point_of_the_channel_it_pushes()
    {
        var filter = ColorGradeFilter.Build(new ColorGrade(Shadows: new ColorWheel(Blue: 1)))!;

        Assert.Contains("colorlevels=", filter, StringComparison.Ordinal);
        Assert.Contains("bomin=0.25", filter, StringComparison.Ordinal);
        Assert.Contains("romin=0", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void The_highlight_wheel_lowers_the_white_point_of_the_other_channels()
    {
        // El filtro solo admite de 0 a 1: añadir azul en las luces se expresa quitando rojo y
        // verde, que es exactamente el mismo viraje.
        var filter = ColorGradeFilter.Build(new ColorGrade(Highlights: new ColorWheel(Blue: 1)))!;

        Assert.Contains("bomax=1", filter, StringComparison.Ordinal);
        Assert.Contains("romax=0.65", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void The_midtone_wheel_bends_the_gamma_of_each_channel()
    {
        var filter = ColorGradeFilter.Build(new ColorGrade(Midtones: new ColorWheel(Blue: 1)))!;

        Assert.Contains("eq=gamma_r=", filter, StringComparison.Ordinal);

        // Centrada en la media: el azul sube y los otros dos bajan en la misma medida.
        Assert.Contains("gamma_b=1.4", filter, StringComparison.Ordinal);
        Assert.Contains("gamma_r=0.8", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Pushing_the_three_channels_together_is_not_a_colour_change()
    {
        // Una rueda de color vira el tono; aclarar u oscurecer es trabajo de la exposición.
        var filter = ColorGradeFilter.Build(
            new ColorGrade(Shadows: new ColorWheel(0.5, 0.5, 0.5), Midtones: new ColorWheel(0.5, 0.5, 0.5)))!;

        Assert.Contains("romin=0:gomin=0:bomin=0", filter, StringComparison.Ordinal);
        Assert.Contains("gamma_r=1:gamma_g=1:gamma_b=1", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_curves_that_have_a_shape_are_written()
    {
        var grade = new ColorGrade(
            Master: ToneCurve.FromPoints([new CurvePoint(0, 0.1), new CurvePoint(1, 1)]),
            Red: ToneCurve.Identity);

        var filter = ColorGradeFilter.Build(grade)!;

        Assert.Contains("curves=all='0/0.1 1/1'", filter, StringComparison.Ordinal);

        // Un canal vacío en 'curves' lo dejaría plano, no sin tocar.
        Assert.DoesNotContain("r=", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void The_selective_sliders_are_flipped_because_the_filter_speaks_in_ink()
    {
        // Subir el cian en el filtro equivale a quitar rojo; el deslizador va del cian al rojo,
        // que es como lo entiende quien lo mueve.
        var grade = new ColorGrade(Selective: new SelectiveColor(ColorFamily.Reds, CyanRed: 0.4));
        var filter = ColorGradeFilter.Build(grade)!;

        Assert.Contains("selectivecolor=reds='-0.4 0 0 0'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void The_order_is_the_one_a_grading_suite_follows()
    {
        var grade = new ColorGrade(
            Master: ToneCurve.FromPoints([new CurvePoint(0, 0.1), new CurvePoint(1, 1)]),
            Shadows: new ColorWheel(Red: 0.2),
            Selective: new SelectiveColor(ColorFamily.Blues, YellowBlue: 0.3),
            LutPath: @"C:\luts\look.cube");

        var filter = ColorGradeFilter.Build(grade)!;

        var lut = filter.IndexOf("lut3d", StringComparison.Ordinal);
        var wheels = filter.IndexOf("colorlevels", StringComparison.Ordinal);
        var curves = filter.IndexOf("curves", StringComparison.Ordinal);
        var selective = filter.IndexOf("selectivecolor", StringComparison.Ordinal);

        Assert.True(lut < wheels && wheels < curves && curves < selective,
            $"El orden cambia la imagen, y salió mal: {filter}");
    }

    // ----------------------------------------------------- rutas de LUT

    [Theory]
    [InlineData(@"C:\luts\look.cube", "'C\\:/luts/look.cube'")]
    [InlineData(@"D:\mis luts\el look.cube", "'D\\:/mis luts/el look.cube'")]
    [InlineData("/opt/editflow/luts/look.cube", "'/opt/editflow/luts/look.cube'")]
    public void A_path_is_escaped_so_the_filter_parser_does_not_eat_it(string path, string expected)
    {
        // La letra de unidad lleva dos puntos, que es justo lo que separa las opciones de un
        // filtro: sin escapar, FFmpeg cree que 'C' es una opción y responde «No option name near».
        Assert.Equal(expected, FilterPath.Quote(path));
    }

    [Fact]
    public void An_apostrophe_needs_three_backslashes_because_FFmpeg_unescapes_twice()
    {
        // Con una sola barra el apóstrofo desaparece del nombre; con dos, el resto del grafo se
        // traga dentro de la ruta. Comprobado contra el FFmpeg empaquetado.
        Assert.Equal(@"'C\:/luts/el look d'\\\''Ana.cube'", FilterPath.Quote(@"C:\luts\el look d'Ana.cube"));
    }

    [Fact]
    public void An_empty_path_is_rejected_instead_of_producing_a_broken_filter()
    {
        Assert.Throws<ArgumentException>(() => FilterPath.Quote("  "));
    }

    // -------------------------------------------------------- la operación

    [Fact]
    public void Correcting_and_undoing_returns_the_clip_to_what_it_was()
    {
        var clip = new Clip(Media());
        var history = new UndoHistory();
        var grade = new ColorGrade(Midtones: new ColorWheel(Red: 0.5));

        history.Do(new SetColorGradeCommand(clip, grade));
        Assert.False(clip.Grade.IsNone);

        history.Undo();
        Assert.True(clip.Grade.IsNone);

        history.Redo();
        Assert.Equal(0.5, clip.Grade.MidtoneWheel.Red, 3);
    }

    [Fact]
    public void The_correction_survives_cloning_and_splitting_a_clip()
    {
        var clip = new Clip(Media()) { Grade = new ColorGrade(Highlights: new ColorWheel(Blue: -0.4)) };

        Assert.Equal(-0.4, clip.Clone().Grade.HighlightWheel.Blue, 3);

        var second = clip.SplitAt(TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        Assert.Equal(-0.4, second.Grade.HighlightWheel.Blue, 3);
        Assert.Equal(-0.4, clip.Grade.HighlightWheel.Blue, 3);
    }

    // ------------------------------------------------------------- el grafo

    [Fact]
    public void The_advanced_correction_goes_after_the_quick_one()
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(Media())
        {
            Color = new ColorAdjust(Saturation: 30),
            Grade = new ColorGrade(Shadows: new ColorWheel(Red: 0.2)),
        });

        var graph = FilterGraphBuilder.Build(sequence, new ExportSettings
        {
            OutputPath = "out.mp4",
            Resolution = VideoResolution.P1080,
            EncoderName = "libx264",
            FrameRate = 30,
        }).FilterGraph;

        var quick = graph.IndexOf("eq=", StringComparison.Ordinal);
        var advanced = graph.IndexOf("colorlevels", StringComparison.Ordinal);

        Assert.True(quick >= 0 && quick < advanced,
            "El ajuste rápido deja la imagen en su punto de partida; la corrección avanzada le da la forma final.");
    }
}
