// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Filtros de color de un clic (blanco y negro, sepia, viñeta...) del clip de video
// seleccionado: una galería de presets, sin deslizadores que ajustar.
public partial class MainWindow
{
    private (VisualFilterKind Kind, Avalonia.Controls.Button Button)[] FilterButtons => [
        (VisualFilterKind.None, FilterNoneButton),
        (VisualFilterKind.BlackAndWhite, FilterBlackAndWhiteButton),
        (VisualFilterKind.Sepia, FilterSepiaButton),
        (VisualFilterKind.Vintage, FilterVintageButton),
        (VisualFilterKind.Vignette, FilterVignetteButton),
        (VisualFilterKind.Warm, FilterWarmButton),
        (VisualFilterKind.Cool, FilterCoolButton),
    ];

    private void WireFilters()
    {
        foreach (var (kind, button) in FilterButtons)
        {
            button.Click += (_, _) => ApplyFilter(kind);
        }
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

    /// <summary>Muestra en el panel el filtro del clip seleccionado, o explica qué seleccionar.</summary>
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

        InspectorNothing.IsVisible = false;
        FiltersControls.IsVisible = true;
        FiltersControls.IsEnabled = Timeline.SelectionIsEditable;

        foreach (var (kind, button) in FilterButtons)
        {
            button.Classes.Set("selected", clip.Filter == kind);
        }
    }
}
