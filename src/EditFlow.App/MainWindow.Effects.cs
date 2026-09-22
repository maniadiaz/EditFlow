// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.App;

// Efectos de estilo de un clic (VHS, aberración cromática, grano, desenfoque, vaporwave...) del
// clip de video seleccionado, con miniaturas reales de su propio fotograma.
public partial class MainWindow
{
    private static readonly (VisualEffectKind Kind, string Label)[] EffectKinds =
    [
        (VisualEffectKind.None, "Ninguno"),
        (VisualEffectKind.Vhs, "VHS"),
        (VisualEffectKind.ChromaticAberration, "Aberración cromática"),
        (VisualEffectKind.FilmGrain, "Grano de película"),
        (VisualEffectKind.Blur, "Desenfocado"),
        (VisualEffectKind.Vaporwave, "Vaporwave"),
    ];

    private static readonly string?[] EffectFragments =
        EffectKinds.Select(f => VisualEffectCatalog.Build(f.Kind)).ToArray();

    private System.Collections.Generic.List<PresetCard>? _effectCards;

    private void WireEffects()
    {
        _effectCards = BuildPresetGallery(
            EffectGallery, EffectKinds.Select(f => f.Label).ToArray(), i => ApplyEffect(EffectKinds[i].Kind));

        EffectSearchBox.TextChanged += (_, _) => FilterPresetGallery(_effectCards, EffectSearchBox.Text);
    }

    private void ApplyEffect(VisualEffectKind kind)
    {
        if (Timeline.SetSelectedEffect(kind))
        {
            SetStatus(kind == VisualEffectKind.None ? "Efecto quitado." : "Efecto aplicado.");
        }

        RefreshInspector();
        RefreshColorPreview();
    }

    /// <summary>Muestra en el panel el efecto del clip seleccionado, o explica qué seleccionar.</summary>
    private void RefreshEffectsInspector()
    {
        InspectorTitle.Text = "Efectos";

        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no tiene imagen a la que aplicar un efecto."
                : "Selecciona un clip de video en la timeline para aplicarle un efecto.";
            return;
        }

        InspectorTarget.Text = System.IO.Path.GetFileName(clip.Source.Path);

        InspectorNothing.IsVisible = false;
        EffectsControls.IsVisible = true;
        EffectsControls.IsEnabled = Timeline.SelectionIsEditable;

        var selected = Array.FindIndex(EffectKinds, f => f.Kind == clip.Effect);
        HighlightPresetCard(_effectCards!, selected);

        void RefreshThumbnails() => ApplyPresetThumbnails(_effectCards!, EffectFragments, clip, RefreshThumbnails);
        RefreshThumbnails();
    }
}
