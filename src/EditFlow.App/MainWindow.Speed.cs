// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Velocidad de reproducción del clip de video seleccionado: más rápido acorta lo que ocupa en
// la timeline, más lento lo alarga; el audio se estira con 'atempo' para no cambiar de tono.
public partial class MainWindow
{
    // Valor que había al empezar a arrastrar el deslizador. Igual que en color y transición:
    // mientras se arrastra el cambio se ve, pero entra en el historial de una sola vez al soltar.
    private double? _speedBaseline;

    private void WireSpeed()
    {
        SpeedSlider.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!_inspectorUpdating)
            {
                _speedBaseline ??= Timeline.SelectedClip?.Speed;
            }
        }, RoutingStrategies.Tunnel);

        SpeedSlider.ValueChanged += (_, _) => OnSpeedSliderChanged();
        SpeedSlider.AddHandler(PointerReleasedEvent, (_, _) => CommitSpeed(), RoutingStrategies.Tunnel);
        SpeedSlider.PointerCaptureLost += (_, _) => CommitSpeed();

        Speed025Button.Click += (_, _) => ApplyPresetSpeed(0.25);
        Speed05Button.Click += (_, _) => ApplyPresetSpeed(0.5);
        Speed1Button.Click += (_, _) => ApplyPresetSpeed(1);
        Speed2Button.Click += (_, _) => ApplyPresetSpeed(2);
        Speed4Button.Click += (_, _) => ApplyPresetSpeed(4);

        SpeedResetButton.Click += (_, _) => ApplyPresetSpeed(1);
    }

    // El deslizador vive en escala logarítmica en base 2: el mismo tramo de arrastre cubre
    // "la mitad" que "el doble", en vez de amontonar todo lo útil (0,25×–4×) en una esquina
    // del rango 0,1–16 si fuera lineal.
    private static double SpeedFromSlider(double value) => Math.Clamp(
        Math.Pow(2, value), EditFlow.Core.Timeline.Clip.MinimumSpeed, EditFlow.Core.Timeline.Clip.MaximumSpeed);

    private static double SliderFromSpeed(double speed) => Math.Log2(speed);

    private void ApplyPresetSpeed(double speed)
    {
        _speedBaseline = null;
        if (Timeline.SetSelectedSpeed(speed))
        {
            SetStatus("Velocidad ajustada.");
        }

        RefreshInspector();
    }

    private void OnSpeedSliderChanged()
    {
        if (_inspectorUpdating || Timeline.SelectedClip is not { } clip)
        {
            return;
        }

        var speed = SpeedFromSlider(SpeedSlider.Value);
        ShowSpeedReadouts(speed, clip);

        if (_speedBaseline is null)
        {
            // Sin arrastre (teclado, rueda): cada cambio es un paso del historial.
            Timeline.SetSelectedSpeed(speed);
            return;
        }

        // Arrastrando: el modelo lleva el valor provisional para que la timeline lo muestre
        // (el clip cambia de ancho al cambiar de velocidad), sin historial todavía.
        Timeline.SetSelectedSpeedProvisional(speed);
    }

    private void CommitSpeed()
    {
        if (_speedBaseline is not { } baseline)
        {
            return;
        }

        _speedBaseline = null;

        var final = SpeedFromSlider(SpeedSlider.Value);
        if (Timeline.SetSelectedSpeed(final, baseline))
        {
            SetStatus("Velocidad ajustada.");
        }
    }

    // 'Clip' a secas es ambiguo aquí: Avalonia.Visual (del que Window hereda) ya tiene una
    // propiedad de instancia con ese nombre, así que hace falta el nombre completo.
    private void ShowSpeedReadouts(double speed, EditFlow.Core.Timeline.Clip clip)
    {
        SpeedReadout.Text = FormatSpeed(speed);

        // La duración se recalcula con la velocidad provisional, no con la ya aplicada: el
        // usuario ve cuánto va a ocupar el clip antes de soltar el deslizador.
        var duration = TimeSpan.FromTicks((long)Math.Round(clip.SourceDuration.Ticks / speed));
        SpeedDurationReadout.Text = $"Dura {Controls.TimelineControl.FormatClock(duration)}";
    }

    private static string FormatSpeed(double speed) =>
        speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    /// <summary>Muestra en el panel la velocidad del clip seleccionado, o explica qué seleccionar.</summary>
    private void RefreshSpeedInspector()
    {
        InspectorTitle.Text = "Velocidad";

        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no tiene velocidad: es tiempo en negro, no material que reproducir."
                : "Selecciona un clip de video en la timeline para cambiar su velocidad.";
            return;
        }

        InspectorTarget.Text = System.IO.Path.GetFileName(clip.Source.Path);

        _inspectorUpdating = true;
        try
        {
            InspectorNothing.IsVisible = false;
            SpeedControls.IsVisible = true;
            SpeedControls.IsEnabled = Timeline.SelectionIsEditable;

            SpeedSlider.Value = SliderFromSpeed(clip.Speed);
            ShowSpeedReadouts(clip.Speed, clip);
            SpeedResetButton.IsEnabled = !clip.Speed.Equals(1.0);
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }
}
