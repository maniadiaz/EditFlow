// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Fundidos a negro y a silencio del clip de video seleccionado: entrada y salida, aplicados
// juntos a la imagen y a su propio audio.
public partial class MainWindow
{
    private void WireEffects()
    {
        ClipFadeInSlider.ValueChanged += (_, _) => ClipFadeInReadout.Text = FormatSeconds(ClipFadeInSlider.Value);
        ClipFadeOutSlider.ValueChanged += (_, _) => ClipFadeOutReadout.Text = FormatSeconds(ClipFadeOutSlider.Value);

        // Igual que el volumen y los fundidos de audio: se aplica al soltar, no en cada
        // movimiento, para no llenar el historial de pasos minúsculos.
        CommitOnRelease(ClipFadeInSlider, CommitClipFades);
        CommitOnRelease(ClipFadeOutSlider, CommitClipFades);

        EffectsResetButton.Click += (_, _) =>
        {
            if (Timeline.SetSelectedClipFade(TimeSpan.Zero, TimeSpan.Zero))
            {
                SetStatus("Fundidos quitados.");
            }

            RefreshInspector();
            RefreshColorPreview();
        };
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

    /// <summary>Muestra en el panel los fundidos del clip seleccionado, o explica qué seleccionar.</summary>
    private void RefreshEffectsInspector()
    {
        InspectorTitle.Text = "Efectos";

        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no tiene imagen ni audio que fundir."
                : "Selecciona un clip de video en la timeline para fundirlo a negro al principio o al final.";
            return;
        }

        InspectorTarget.Text = System.IO.Path.GetFileName(clip.Source.Path);

        _inspectorUpdating = true;
        try
        {
            InspectorNothing.IsVisible = false;
            EffectsControls.IsVisible = true;
            EffectsControls.IsEnabled = Timeline.SelectionIsEditable;

            ClipFadeInSlider.Value = clip.FadeIn.TotalSeconds;
            ClipFadeOutSlider.Value = clip.FadeOut.TotalSeconds;
            ClipFadeInReadout.Text = FormatSeconds(clip.FadeIn.TotalSeconds);
            ClipFadeOutReadout.Text = FormatSeconds(clip.FadeOut.TotalSeconds);
            EffectsResetButton.IsEnabled = clip.FadeIn != TimeSpan.Zero || clip.FadeOut != TimeSpan.Zero;
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }
}
