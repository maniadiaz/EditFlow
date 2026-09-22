// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Encuadre del clip de video seleccionado: zoom, posición y rotación, con tiradores directamente
// sobre el preview (el propio VideoSurface) y este panel como respaldo para el ajuste fino.
public partial class MainWindow
{
    private void WireFrame()
    {
        Video.FrameTransformChanged += (_, transform) =>
        {
            if (Timeline.SetSelectedTransform(transform))
            {
                SetStatus("Encuadre ajustado.");
            }

            RefreshInspector();
        };

        FrameResetButton.Click += (_, _) =>
        {
            if (Timeline.SetSelectedTransform(ClipTransform.None))
            {
                SetStatus("Encuadre restablecido.");
            }

            RefreshInspector();
        };
    }

    /// <summary>Muestra en el panel el encuadre del clip seleccionado, o explica qué seleccionar.</summary>
    private void RefreshFrameInspector()
    {
        InspectorTitle.Text = "Encuadre";

        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            Video.FrameClip = null;
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no tiene imagen que encuadrar."
                : "Selecciona un clip de video en la timeline para recortarlo, acercarlo o girarlo " +
                  "arrastrando los tiradores sobre el preview.";
            return;
        }

        InspectorTarget.Text = System.IO.Path.GetFileName(clip.Source.Path);

        InspectorNothing.IsVisible = false;
        FrameControls.IsVisible = true;
        FrameControls.IsEnabled = Timeline.SelectionIsEditable;

        Video.FrameClip = Timeline.SelectionIsEditable ? clip : null;
        Video.FrameTransform = clip.Transform;

        ShowFrameReadouts(clip.Transform);
        FrameResetButton.IsEnabled = !clip.Transform.IsNone;
    }

    private void ShowFrameReadouts(ClipTransform transform)
    {
        FrameScaleReadout.Text = transform.Scale.ToString("0%", CultureInfo.InvariantCulture);
        FrameRotationReadout.Text = transform.Rotation.ToString("0", CultureInfo.InvariantCulture) + "°";
    }
}
