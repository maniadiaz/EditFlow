// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.App.Controls;

/// <summary>
/// Una rueda de color: un disco donde se arrastra un punto para empujar el color de un tramo de
/// la imagen —sombras, medios o luces— hacia un tono.
/// </summary>
/// <remarks>
/// <para>
/// El disco es el mapa que usa cualquier sala de etalonaje: el ángulo es el tono y la distancia
/// al centro, la fuerza. Se traduce a los tres canales que espera el modelo proyectando sobre
/// los ejes del rojo, el verde y el azul, separados 120 grados.
/// </para>
/// <para>
/// El fondo se dibuja como un abanico de sectores en vez de con un degradado cónico porque
/// Avalonia no trae uno; con 64 sectores el escalonado no se aprecia a este tamaño.
/// </para>
/// </remarks>
public sealed class ColorWheelPad : Control
{
    private static readonly double RedAngle = -90 * Math.PI / 180;
    private static readonly double GreenAngle = 30 * Math.PI / 180;
    private static readonly double BlueAngle = 150 * Math.PI / 180;

    private ColorWheel _value = ColorWheel.Neutral;
    private bool _dragging;

    /// <summary>Crea la rueda.</summary>
    public ColorWheelPad()
    {
        Width = 96;
        Height = 96;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Se avisa mientras se arrastra, con el valor nuevo.</summary>
    public event EventHandler<ColorWheel>? Changing;

    /// <summary>Se avisa al soltar: es el momento de guardar el cambio en el historial.</summary>
    public event EventHandler<ColorWheel>? Committed;

    /// <summary>Valor que muestra la rueda.</summary>
    public ColorWheel Value
    {
        get => _value;
        set
        {
            _value = (value ?? ColorWheel.Neutral).Clamped();
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this));
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging)
        {
            Apply(e.GetPosition(this));
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            Committed?.Invoke(this, _value);
        }
    }

    /// <summary>Devuelve la rueda al centro. Doble clic, como en cualquier editor.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // La rueda del ratón acerca o aleja el punto del centro sin cambiar el tono: es la forma
        // cómoda de ajustar la fuerza cuando ya se acertó con el color.
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        Value = new ColorWheel(_value.Red * factor, _value.Green * factor, _value.Blue * factor);
        Committed?.Invoke(this, _value);
        e.Handled = true;
    }

    private void Apply(Point point)
    {
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (radius <= 0)
        {
            return;
        }

        var dx = (point.X - (Bounds.Width / 2)) / radius;
        var dy = (point.Y - (Bounds.Height / 2)) / radius;

        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance > 1)
        {
            dx /= distance;
            dy /= distance;
        }

        // Se proyecta sobre los tres ejes del disco. El signo de Y va al revés porque en pantalla
        // crece hacia abajo.
        _value = new ColorWheel(
            (dx * Math.Cos(RedAngle)) + (-dy * Math.Sin(RedAngle)),
            (dx * Math.Cos(GreenAngle)) + (-dy * Math.Sin(GreenAngle)),
            (dx * Math.Cos(BlueAngle)) + (-dy * Math.Sin(BlueAngle))).Clamped();

        InvalidateVisual();
        Changing?.Invoke(this, _value);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (radius <= 1)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // El abanico de tonos.
        const int Sectors = 64;
        for (var i = 0; i < Sectors; i++)
        {
            var from = i * 2 * Math.PI / Sectors;
            var to = (i + 1) * 2 * Math.PI / Sectors;
            var hue = i * 360.0 / Sectors;

            var wedge = new StreamGeometry();
            using (var draw = wedge.Open())
            {
                draw.BeginFigure(centre, isFilled: true);
                draw.LineTo(new Point(centre.X + (radius * Math.Cos(from)), centre.Y - (radius * Math.Sin(from))));
                draw.LineTo(new Point(centre.X + (radius * Math.Cos(to)), centre.Y - (radius * Math.Sin(to))));
                draw.EndFigure(true);
            }

            context.DrawGeometry(new SolidColorBrush(FromHue(hue)), null, wedge);
        }

        // Un velo claro en el centro: la fuerza crece hacia fuera, y así se ve.
        context.DrawEllipse(
            new RadialGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(230, 128, 128, 128), 0),
                    new GradientStop(Color.FromArgb(0, 128, 128, 128), 1),
                ],
            },
            null,
            centre,
            radius,
            radius);

        var line = this.TryFindResource("Line", out var l) && l is IBrush lb ? lb : Brushes.DimGray;
        context.DrawEllipse(null, new Pen(line, 1), centre, radius, radius);

        // El punto: se deshace la proyección para saber dónde cae.
        var x = (_value.Red * Math.Cos(RedAngle))
            + (_value.Green * Math.Cos(GreenAngle))
            + (_value.Blue * Math.Cos(BlueAngle));
        var y = (_value.Red * Math.Sin(RedAngle))
            + (_value.Green * Math.Sin(GreenAngle))
            + (_value.Blue * Math.Sin(BlueAngle));

        // La proyección de tres ejes sobre dos dimensiones escala por 3/2: se deshace para que el
        // punto vuelva al sitio exacto del que se arrastró.
        var handle = new Point(centre.X + (x * radius * 2 / 3), centre.Y - (y * radius * 2 / 3));

        context.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), handle, 5, 5);
    }

    /// <summary>Color totalmente saturado de un tono, en grados.</summary>
    private static Color FromHue(double hue)
    {
        var sector = hue / 60;
        var fraction = sector - Math.Floor(sector);
        var rising = (byte)(fraction * 255);
        var falling = (byte)(255 - rising);

        var index = ((int)Math.Floor(sector) % 6 + 6) % 6;

        return index switch
        {
            0 => Color.FromRgb(255, rising, 0),
            1 => Color.FromRgb(falling, 255, 0),
            2 => Color.FromRgb(0, 255, rising),
            3 => Color.FromRgb(0, falling, 255),
            4 => Color.FromRgb(rising, 0, 255),
            _ => Color.FromRgb(255, 0, falling),
        };
    }
}
