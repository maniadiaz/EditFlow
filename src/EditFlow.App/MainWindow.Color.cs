// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Ajuste de color del clip seleccionado: exposición, contraste, saturación y temperatura.
public partial class MainWindow
{
    // Valor que había al empezar a arrastrar un deslizador. Mientras se arrastra, el cambio se ve en el
    // preview pero no entra en el historial: al soltar entra uno solo, que deshace todo el arrastre.
    private ColorAdjust? _colorBaseline;

    private readonly DispatcherTimer _colorPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };

    private Slider[] ColorSliders => [ExposureSlider, ContrastSlider, SaturationSlider, TemperatureSlider];

    private void WireColor()
    {
        _colorPreviewTimer.Tick += (_, _) =>
        {
            _colorPreviewTimer.Stop();
            RefreshColorPreview();
        };

        foreach (var slider in ColorSliders)
        {
            slider.AddHandler(PointerPressedEvent, (_, _) =>
            {
                if (!_inspectorUpdating)
                {
                    _colorBaseline ??= CurrentTargetColor();
                }
            }, RoutingStrategies.Tunnel);

            slider.ValueChanged += (_, _) => OnColorSliderChanged();
            slider.AddHandler(PointerReleasedEvent, (_, _) => CommitColor(), RoutingStrategies.Tunnel);
            slider.PointerCaptureLost += (_, _) => CommitColor();
        }

        ColorResetButton.Click += (_, _) =>
        {
            _colorBaseline = null;
            Timeline.SetSelectedColor(ColorAdjust.None, null);
            RefreshInspector();
        };
    }

    /// <summary>Ajuste del elemento que se está editando: un clip de video o un video superpuesto.</summary>
    private ColorAdjust? CurrentTargetColor() =>
        Timeline.SelectedClip is { IsGap: false } clip ? clip.Color
        : Timeline.SelectedOverlay is { Kind: OverlayKind.Video } item ? item.Color
        : null;

    private ColorAdjust ColorFromSliders() => new(
        ExposureSlider.Value, ContrastSlider.Value, SaturationSlider.Value, TemperatureSlider.Value);

    private void OnColorSliderChanged()
    {
        if (_inspectorUpdating)
        {
            return;
        }

        var color = ColorFromSliders();
        ShowColorReadouts(color);

        if (CurrentTargetColor() is null)
        {
            return;
        }

        if (_colorBaseline is null)
        {
            // Sin arrastre (teclado, rueda): cada cambio es un paso del historial.
            Timeline.SetSelectedColor(color, null);
            return;
        }

        // Arrastrando: el modelo lleva el valor provisional para que el preview lo muestre, sin historial.
        Timeline.SetSelectedColorProvisional(color);
        _colorPreviewTimer.Stop();
        _colorPreviewTimer.Start();
    }

    private void CommitColor()
    {
        if (_colorBaseline is not { } baseline)
        {
            return;
        }

        _colorBaseline = null;
        _colorPreviewTimer.Stop();

        var final = ColorFromSliders();
        if (Timeline.SetSelectedColor(final, baseline))
        {
            SetStatus("Color ajustado.");
        }
    }

    /// <summary>Vuelve a mostrar el fotograma actual con el color provisional.</summary>
    private void RefreshColorPreview()
    {
        if (_video is null)
        {
            return;
        }

        // El clip cargado se vuelve a abrir con los filtros nuevos; un video superpuesto pide su fotograma otra vez.
        _playingClip = null;
        _videoOverlayAsked.Clear();
        ShowFrameAt(Timeline.Playhead);
        UpdatePreviewOverlays();
    }

    private void ShowColorReadouts(ColorAdjust color)
    {
        ExposureReadout.Text = FormatSigned(color.Exposure);
        ContrastReadout.Text = FormatSigned(color.Contrast);
        SaturationReadout.Text = FormatSigned(color.Saturation);
        TemperatureReadout.Text = FormatSigned(color.Temperature);
    }

    private static string FormatSigned(double value) =>
        Math.Round(value).ToString("+0;-0;0", CultureInfo.InvariantCulture);

    /// <summary>Muestra en el panel el color del elemento seleccionado, o explica qué seleccionar.</summary>
    private void RefreshColorInspector()
    {
        InspectorTitle.Text = "Color";

        var color = CurrentTargetColor();
        if (color is null)
        {
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no tiene imagen. Selecciona un clip de video para ajustar su color."
                : "Selecciona un clip de video (o un video en una capa) en la timeline para ajustar su color.";
            return;
        }

        InspectorTarget.Text = Timeline.SelectedClip is { } clip
            ? System.IO.Path.GetFileName(clip.Source.Path)
            : System.IO.Path.GetFileName(Timeline.SelectedOverlay?.Media?.Path);

        _inspectorUpdating = true;
        try
        {
            InspectorNothing.IsVisible = false;
            ColorControls.IsVisible = true;
            ColorControls.IsEnabled = Timeline.SelectionIsEditable || Timeline.SelectedOverlayTrack is { IsLocked: false };

            ExposureSlider.Value = color.Exposure;
            ContrastSlider.Value = color.Contrast;
            SaturationSlider.Value = color.Saturation;
            TemperatureSlider.Value = color.Temperature;
            ShowColorReadouts(color);
        }
        finally
        {
            _inspectorUpdating = false;
        }

        // La corrección avanzada es solo de los clips de la pista principal: un video en una capa
        // tiene los cuatro deslizadores rápidos, pero no ruedas ni curvas.
        AdvancedColorToggle.IsVisible = Timeline.SelectedClip is { IsGap: false };
        if (!AdvancedColorToggle.IsVisible)
        {
            AdvancedColorPanel.IsVisible = false;
        }
        else
        {
            RefreshGradePanel();
        }
    }
}
