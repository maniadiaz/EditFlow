// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.PreviewCache;

namespace EditFlow.Engine.Tests.Exporting;

public class ChromaKeyTests
{
    private static MediaInfo Video(string path = "capa.mp4") =>
        new(path, TimeSpan.FromSeconds(10), 1920, 1080, 30, "h264", HasAudio: true);

    private static OverlayItem Overlay() =>
        OverlayItem.CreateVideo(Video(), TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

    // -------------------------------------------------------------- el valor

    [Fact]
    public void A_new_key_is_off_so_nothing_changes_by_accident()
    {
        Assert.False(ChromaKey.None.Enabled);
        Assert.Null(ChromaKeyFilter.Build(ChromaKey.None));
        Assert.Null(ChromaKeyFilter.Build(null));
    }

    [Fact]
    public void A_fresh_video_layer_does_not_cut_anything_out()
    {
        Assert.Equal(ChromaKey.None, Overlay().ChromaKey);
    }

    [Theory]
    [InlineData("#00B140", "#00B140")]
    [InlineData("00b140", "#00B140")]
    [InlineData("  #00b140  ", "#00B140")]
    [InlineData("verde", ChromaKey.DefaultColor)]
    [InlineData("#12345", ChromaKey.DefaultColor)]
    [InlineData(null, ChromaKey.DefaultColor)]
    public void A_color_is_normalized_and_a_bad_one_falls_back_to_green(string? typed, string expected)
    {
        Assert.Equal(expected, ChromaKey.Normalize(typed));
    }

    [Theory]
    [InlineData(5, ChromaKey.MaximumSimilarity)]
    [InlineData(-3, ChromaKey.MinimumSimilarity)]
    [InlineData(double.NaN, 0.2)]
    public void The_tolerance_stays_inside_its_range(double requested, double expected)
    {
        Assert.Equal(expected, new ChromaKey(true, Similarity: requested).Clamped().Similarity, 3);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(-1, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void The_edge_softening_stays_inside_its_range(double requested, double expected)
    {
        Assert.Equal(expected, new ChromaKey(true, Blend: requested).Clamped().Blend, 3);
    }

    [Theory]
    [InlineData("#00B140", "green")]
    [InlineData("#0047BB", "blue")]
    [InlineData("#FFFFFF", "green")]   // empate: se queda con el verde, que es el fondo habitual
    public void The_spill_channel_follows_whichever_of_the_two_dominates(string color, string expected)
    {
        Assert.Equal(expected, new ChromaKey(true, color).SpillChannel);
    }

    // ------------------------------------------------------------ el filtro

    [Fact]
    public void The_filter_writes_the_color_the_way_FFmpeg_reads_it()
    {
        var filter = ChromaKeyFilter.Build(new ChromaKey(true, "#00B140", 0.3, 0.1, Despill: false));

        // '#' abre un comentario en un guion de filtros: el color tiene que ir como 0xRRGGBB.
        Assert.Equal("format=yuva444p,chromakey=0x00B140:0.3:0.1,format=rgba", filter);
        Assert.DoesNotContain("#", filter!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cut_is_measured_in_YUV_because_that_is_what_the_filter_expects()
    {
        var filter = ChromaKeyFilter.Build(ChromaKey.Green)!;

        // Pasado en RGBA, 'chromakey' compara canales que no son los que cree y recorta de más:
        // medido con FFmpeg de verdad, un azul saturado salía con alfa 192 en vez de 255. Y en
        // 4:4:4 en vez de 4:2:0 para que el recorte se evalúe píxel a píxel, no por bloques.
        Assert.StartsWith("format=yuva444p,chromakey=", filter, StringComparison.Ordinal);

        // Y vuelve a RGBA, que es donde componen tanto el preview como el 'overlay' de la salida.
        Assert.Contains(",format=rgba", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void The_despill_goes_after_the_cut_because_it_works_on_what_is_left()
    {
        var filter = ChromaKeyFilter.Build(new ChromaKey(true, "#00B140", 0.3, 0.1))!;

        Assert.Equal("format=yuva444p,chromakey=0x00B140:0.3:0.1,format=rgba,despill=type=green", filter);
        Assert.True(filter.IndexOf("chromakey", StringComparison.Ordinal)
            < filter.IndexOf("despill", StringComparison.Ordinal));
    }

    [Fact]
    public void The_numbers_are_written_the_same_in_any_language()
    {
        // En una máquina en español, "0,3" haría que FFmpeg leyera 0 y descartara el decimal, y el
        // recorte saldría con tolerancia 0 sin avisar de nada.
        var filter = ChromaKeyFilter.Build(new ChromaKey(true, "#00B140", 0.3, 0.15, Despill: false))!;

        Assert.Equal("format=yuva444p,chromakey=0x00B140:0.3:0.15,format=rgba", filter);
    }

    [Fact]
    public void The_defaults_leave_room_between_the_background_and_the_subject()
    {
        // La tolerancia y el suavizado se suman: el recorte alcanza hasta 'similarity + blend'.
        // Con 0,25 y 0,08 —los primeros valores que puse— ese alcance llegaba a 0,33, y un azul
        // saturado, que mide 0,31 de distancia al verde, salía medio recortado.
        Assert.True(ChromaKey.Green.Similarity + ChromaKey.Green.Blend <= 0.27,
            "El alcance del recorte por defecto se come colores que no son el fondo.");
    }

    // ------------------------------------------------------------ el grafo

    private static EditSequence SequenceWithKeyedLayer(ChromaKey key)
    {
        var sequence = new EditSequence();
        sequence.Video.Append(new Clip(new MediaInfo(
            "base.mp4", TimeSpan.FromSeconds(10), 1920, 1080, 30, "h264", HasAudio: true)));

        var track = sequence.AddOverlayTrack("V2");
        var item = OverlayItem.CreateVideo(Video(), TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));
        item.ChromaKey = key;
        Assert.True(track.TryAdd(item));

        return sequence;
    }

    private static ExportSettings Settings() => new()
    {
        OutputPath = "out.mp4",
        Resolution = VideoResolution.P1080,
        EncoderName = "libx264",
        FrameRate = 30,
    };

    [Fact]
    public void The_cut_lands_before_the_layer_is_composited_over_the_main_track()
    {
        var graph = FilterGraphBuilder.Build(SequenceWithKeyedLayer(ChromaKey.Green), Settings()).FilterGraph;

        var key = graph.IndexOf("chromakey=", StringComparison.Ordinal);
        var overlay = graph.IndexOf("overlay=", StringComparison.Ordinal);

        Assert.True(key >= 0, "La capa recorta el fondo, así que el grafo debe recortarlo.");
        Assert.True(key < overlay,
            "Recortar después de componer llegaría tarde: ya no habría nada debajo que enseñar.");
    }

    [Fact]
    public void The_cut_replaces_the_plain_conversion_instead_of_duplicating_it()
    {
        var keyed = FilterGraphBuilder.Build(SequenceWithKeyedLayer(ChromaKey.Green), Settings()).FilterGraph;

        // El fragmento del recorte ya acaba en RGBA; encadenarle otra conversión sería repetir
        // trabajo sobre cada fotograma de la capa.
        Assert.Equal(1, keyed.Split("format=rgba").Length - 1);
    }

    [Fact]
    public void A_layer_without_the_cut_leaves_the_graph_exactly_as_it_was()
    {
        var graph = FilterGraphBuilder.Build(SequenceWithKeyedLayer(ChromaKey.None), Settings()).FilterGraph;

        Assert.DoesNotContain("chromakey", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("despill", graph, StringComparison.Ordinal);
    }

    // -------------------------------------------------------- la operación

    [Fact]
    public void Setting_and_undoing_the_cut_returns_the_layer_to_what_it_was()
    {
        var item = Overlay();
        var undo = new UndoHistory();

        undo.Do(new SetChromaKeyCommand(item, ChromaKey.Green));
        Assert.True(item.ChromaKey.Enabled);
        Assert.Equal(ChromaKey.DefaultColor, item.ChromaKey.Color);

        undo.Undo();
        Assert.Equal(ChromaKey.None, item.ChromaKey);

        undo.Redo();
        Assert.True(item.ChromaKey.Enabled);
    }

    [Fact]
    public void The_command_clamps_what_it_is_given()
    {
        var item = Overlay();
        new SetChromaKeyCommand(item, new ChromaKey(true, "no-es-un-color", 9, -2)).Execute();

        Assert.Equal(ChromaKey.DefaultColor, item.ChromaKey.Color);
        Assert.Equal(ChromaKey.MaximumSimilarity, item.ChromaKey.Similarity, 3);
        Assert.Equal(0, item.ChromaKey.Blend, 3);
    }

    [Fact]
    public void A_text_layer_cannot_be_keyed_because_there_is_nothing_to_key()
    {
        var text = OverlayItem.CreateText(new TextStyle(), TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.Throws<ArgumentException>(() => new SetChromaKeyCommand(text, ChromaKey.Green));
    }

    // ------------------------------------------------------------- trozeado

    [Fact]
    public void A_slice_of_a_keyed_layer_keeps_cutting_the_same_background()
    {
        var sequence = SequenceWithKeyedLayer(new ChromaKey(true, ChromaKey.BlueColor, 0.4, 0.2));

        // Un trozo del medio: no conserva ni el inicio ni el final del elemento.
        var slice = SequenceSlicer.Slice(sequence, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        var sliced = slice.OverlayTracks.Single().Items.Single();

        Assert.True(sliced.ChromaKey.Enabled);
        Assert.Equal(ChromaKey.BlueColor, sliced.ChromaKey.Color);
        Assert.Equal(0.4, sliced.ChromaKey.Similarity, 3);
        Assert.Equal(0.2, sliced.ChromaKey.Blend, 3);
    }

    // ------------------------------------------------- copia de preview

    [Fact]
    public void Turning_the_cut_on_invalidates_the_rendered_preview()
    {
        var off = SequenceWithKeyedLayer(ChromaKey.None);
        var on = SequenceWithKeyedLayer(ChromaKey.Green);
        var settings = PreviewCacheSettings.For(540, 30);

        Assert.NotEqual(HashOf(off, settings), HashOf(on, settings));
    }

    [Fact]
    public void Nudging_the_tolerance_invalidates_the_rendered_preview_too()
    {
        var coarse = SequenceWithKeyedLayer(new ChromaKey(true, ChromaKey.DefaultColor, 0.2));
        var fine = SequenceWithKeyedLayer(new ChromaKey(true, ChromaKey.DefaultColor, 0.4));
        var settings = PreviewCacheSettings.For(540, 30);

        Assert.NotEqual(HashOf(coarse, settings), HashOf(fine, settings));
    }

    private static string HashOf(EditSequence sequence, PreviewCacheSettings settings) =>
        SectionHasher.Compute(
            SequenceSlicer.Slice(sequence, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
            settings,
            _ => "sin-archivo");
}
