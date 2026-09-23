// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.App.Controls;

/// <summary>
/// El editor de una curva de tonos: un cuadrado donde la diagonal es «no cambies nada» y cada
/// punto que se arrastra dobla la respuesta de la imagen.
/// </summary>
/// <remarks>
/// <para>
/// Es el control de color más directo que existe: lo que se dibuja es literalmente lo que le pasa
/// a la imagen. Subir la parte baja de la curva levanta los negros —el aspecto de cine— sin tocar
/// las luces, algo que ningún deslizador alcanza.
/// </para>
/// <para>
/// Un clic añade un punto, arrastrarlo lo mueve y el botón derecho lo quita. Los dos extremos no
/// se borran: sin ellos la curva dejaría de cubrir todo el rango.
/// </para>
/// </remarks>
public sealed class CurveEditor : Control
{
    private const double HitRadius = 0.06;

    private ToneCurve _curve = ToneCurve.Identity;
    private int _dragging = -1;

    /// <summary>Crea el editor.</summary>
    public CurveEditor()
    {
        Height = 150;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>Se avisa mientras se arrastra un punto.</summary>
    public event EventHandler<ToneCurve>? Changing;

    /// <summary>Se avisa al soltar: es el momento de guardar el cambio en el historial.</summary>
    public event EventHandler<ToneCurve>? Committed;

    /// <summary>Curva que se muestra.</summary>
    public ToneCurve Curve
    {
        get => _curve;
        set
        {
            _curve = value ?? ToneCurve.Identity;
            InvalidateVisual();
        }
    }

    /// <summary>Color con el que se dibuja el trazo; distingue el canal que se está editando.</summary>
    public IBrush Stroke { get; set; } = Brushes.White;

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = ToCurve(e.GetPosition(this));
        var hit = IndexNear(point.In);

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // Los extremos sostienen el rango completo: quitarlos dejaría la curva a medias.
            if (hit >= 0 && _curve.Points[hit].In > 0.01 && _curve.Points[hit].In < 0.99)
            {
                Curve = _curve.Without(_curve.Points[hit].In);
                Committed?.Invoke(this, _curve);
            }

            e.Handled = true;
            return;
        }

        // Una curva sin puntos empieza por la diagonal: los extremos tienen que existir para que
        // mover un punto del medio no arrastre consigo los negros y los blancos.
        if (_curve.Points.Count == 0)
        {
            _curve = ToneCurve.FromPoints([new CurvePoint(0, 0), new CurvePoint(1, 1)]);
            hit = -1;
        }

        Curve = hit >= 0 ? _curve : _curve.With(point.In, point.Out);
        _dragging = IndexNear(point.In);

        e.Pointer.Capture(this);
        Changing?.Invoke(this, _curve);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging < 0 || _dragging >= _curve.Points.Count)
        {
            return;
        }

        var moved = ToCurve(e.GetPosition(this));
        var anchored = _curve.Points[_dragging].In;

        // Un extremo solo sube y baja: si pudiera desplazarse a lo ancho, la curva dejaría de
        // cubrir todo el rango y FFmpeg extrapolaría por su cuenta.
        var input = anchored <= 0.01 || anchored >= 0.99 ? anchored : moved.In;

        Curve = _curve.Without(anchored).With(input, moved.Out);
        _dragging = IndexNear(input);
        Changing?.Invoke(this, _curve);
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragging >= 0)
        {
            _dragging = -1;
            e.Pointer.Capture(null);
            Committed?.Invoke(this, _curve);
        }
    }

    private int IndexNear(double input)
    {
        for (var i = 0; i < _curve.Points.Count; i++)
        {
            if (Math.Abs(_curve.Points[i].In - input) <= HitRadius)
            {
                return i;
            }
        }

        return -1;
    }

    private CurvePoint ToCurve(Point point) => new CurvePoint(
        Bounds.Width > 0 ? point.X / Bounds.Width : 0,

        // En pantalla la Y crece hacia abajo y en la curva hacia arriba.
        Bounds.Height > 0 ? 1 - (point.Y / Bounds.Height) : 0).Clamped();

    private Point ToScreen(CurvePoint point) =>
        new(point.In * Bounds.Width, (1 - point.Out) * Bounds.Height);

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        var line = this.TryFindResource("Line", out var l) && l is IBrush lb ? lb : Brushes.DimGray;
        var faint = this.TryFindResource("TextFaint", out var f) && f is IBrush fb ? fb : Brushes.Gray;
        var panel = this.TryFindResource("Panel", out var p) && p is IBrush pb ? pb : Brushes.Black;

        context.FillRectangle(panel, new Rect(Bounds.Size));

        // Rejilla en tercios y la diagonal de referencia.
        for (var i = 1; i < 4; i++)
        {
            var x = Bounds.Width * i / 4;
            var y = Bounds.Height * i / 4;
            context.DrawLine(new Pen(line, 0.5), new Point(x, 0), new Point(x, Bounds.Height));
            context.DrawLine(new Pen(line, 0.5), new Point(0, y), new Point(Bounds.Width, y));
        }

        context.DrawLine(
            new Pen(faint, 1, new DashStyle([3, 3], 0)),
            new Point(0, Bounds.Height),
            new Point(Bounds.Width, 0));

        context.DrawRectangle(null, new Pen(line, 1), new Rect(Bounds.Size));

        var points = _curve.Points.Count >= 2
            ? _curve.Points
            : [new CurvePoint(0, 0), new CurvePoint(1, 1)];

        // El trazo, recto entre puntos: es lo mismo que hace el filtro con 'curves' en su modo
        // por defecto, así que lo que se dibuja aquí es lo que se ve en la imagen.
        var pen = new Pen(Stroke, 1.8);
        for (var i = 1; i < points.Count; i++)
        {
            context.DrawLine(pen, ToScreen(points[i - 1]), ToScreen(points[i]));
        }

        foreach (var point in points)
        {
            context.DrawEllipse(Stroke, new Pen(Brushes.Black, 1), ToScreen(point), 4, 4);
        }
    }
}
