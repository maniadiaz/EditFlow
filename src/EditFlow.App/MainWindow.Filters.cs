// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.App;

// Filtros de color de un clic (blanco y negro, sepia, viñeta...) del clip de video seleccionado,
// con miniaturas reales de su propio fotograma, y el fundido de entrada/salida del mismo clip.
public partial class MainWindow
{
    private static readonly (VisualFilterKind Kind, string Label)[] FilterKinds =
    [
        (VisualFilterKind.None, "Ninguno"),
        (VisualFilterKind.BlackAndWhite, "Blanco y negro"),
        (VisualFilterKind.Sepia, "Sepia"),
        (VisualFilterKind.Vintage, "Vintage"),
        (VisualFilterKind.Vignette, "Viñeta"),
        (VisualFilterKind.Warm, "Cálido"),
        (VisualFilterKind.Cool, "Frío"),
    ];

    private static readonly string?[] FilterFragments =
        FilterKinds.Select(f => VisualFilterCatalog.Build(f.Kind)).ToArray();

    private System.Collections.Generic.List<PresetCard>? _filterCards;

    private void WireFilters()
    {
        _filterCards = BuildPresetGallery(
            FilterGallery, FilterKinds.Select(f => f.Label).ToArray(), i => ApplyFilter(FilterKinds[i].Kind));

        FilterSearchBox.TextChanged += (_, _) => FilterPresetGallery(_filterCards, FilterSearchBox.Text);

        ClipFadeInSlider.ValueChanged += (_, _) => ClipFadeInReadout.Text = FormatSeconds(ClipFadeInSlider.Value);
        ClipFadeOutSlider.ValueChanged += (_, _) => ClipFadeOutReadout.Text = FormatSeconds(ClipFadeOutSlider.Value);

        // Igual que el volumen y los fundidos de audio: se aplica al soltar, no en cada
        // movimiento, para no llenar el historial de pasos minúsculos.
        CommitOnRelease(ClipFadeInSlider, CommitClipFades);
        CommitOnRelease(ClipFadeOutSlider, CommitClipFades);

        FadeResetButton.Click += (_, _) =>
        {
            if (Timeline.SetSelectedClipFade(TimeSpan.Zero, TimeSpan.Zero))
            {
                SetStatus("Fundidos quitados.");
            }

            RefreshInspector();
            RefreshColorPreview();
        };
    }

    private void ApplyFilter(VisualFilterKind kind)
    {
        if (Timeline.SetSelectedFilter(kind))
        {
            SetStatus(kind == VisualFilterKind.None ? "Filtro quitado." : "Filtro aplicado.");
        }

        RefreshInspector();
        RefreshColorPreview();
    }

    private void CommitClipFades()
    {
        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            return;
        }

        var fadeIn = TimeSpan.FromSeconds(ClipFadeInSlider.Value);
        var fadeOut = TimeSpan.FromSeconds(ClipFadeOutSlider.Value);

        if (fadeIn == clip.FadeIn && fadeOut == clip.FadeOut)
        {
            return;
        }

        if (Timeline.SetSelectedClipFade(fadeIn, fadeOut))
        {
            SetStatus("Fundidos ajustados.");
        }

        RefreshInspector();
        RefreshColorPreview();
    }

    /// <summary>Muestra en el panel el filtro y el fundido del clip seleccionado, o explica qué seleccionar.</summary>
    private void RefreshFiltersInspector()
    {
        InspectorTitle.Text = "Filtros";

        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no tiene imagen a la que aplicar un filtro."
                : "Selecciona un clip de video en la timeline para aplicarle un filtro.";
            return;
        }

        InspectorTarget.Text = System.IO.Path.GetFileName(clip.Source.Path);

        _inspectorUpdating = true;
        try
        {
            InspectorNothing.IsVisible = false;
            FiltersControls.IsVisible = true;
            FiltersControls.IsEnabled = Timeline.SelectionIsEditable;

            var selected = Array.FindIndex(FilterKinds, f => f.Kind == clip.Filter);
            HighlightPresetCard(_filterCards!, selected);

            void RefreshThumbnails() => ApplyPresetThumbnails(_filterCards!, FilterFragments, clip, RefreshThumbnails);
            RefreshThumbnails();

            ClipFadeInSlider.Value = clip.FadeIn.TotalSeconds;
            ClipFadeOutSlider.Value = clip.FadeOut.TotalSeconds;
            ClipFadeInReadout.Text = FormatSeconds(clip.FadeIn.TotalSeconds);
            ClipFadeOutReadout.Text = FormatSeconds(clip.FadeOut.TotalSeconds);
            FadeResetButton.IsEnabled = clip.FadeIn != TimeSpan.Zero || clip.FadeOut != TimeSpan.Zero;
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }
}
