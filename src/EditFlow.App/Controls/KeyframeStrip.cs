// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Shapes = Avalonia.Controls.Shapes;
using EditFlow.Core.Timeline;

namespace EditFlow.App.Controls;

/// <summary>
/// Los controles de animación de una propiedad: el rombo que pone o quita un punto en el cabezal,
/// una regla con los puntos que ya hay y los botones para saltar entre ellos.
/// </summary>
/// <remarks>
/// <para>
/// Es la misma pieza para las siete propiedades animables —zoom, posición, giro, ancho, opacidad
/// y volumen—, que viven en cuatro paneles distintos. Escribirla una vez evita que cada panel
/// acabe con su propia versión ligeramente distinta.
/// </para>
/// <para>
/// El rombo es el gesto que usa cualquier editor: vacío no hay animación, hueco la hay pero no
/// justo aquí, y relleno hay un punto en el cabezal. Pulsarlo alterna entre poner y quitar.
/// </para>
/// </remarks>
public sealed class KeyframeStrip : UserControl
{
    private readonly Button _toggle;
    private readonly Shapes.Path _diamond;
    private readonly TextBlock _summary;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _clear;
    private readonly PointRuler _ruler;

    private KeyframeTrack _track = KeyframeTrack.Empty;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _position;

    /// <summary>Crea la fila de controles.</summary>
    public KeyframeStrip()
    {
        _diamond = new Shapes.Path
        {
            // Un rombo de 10×10 centrado en el origen.
            Data = Geometry.Parse("M 5,0 L 10,5 L 5,10 L 0,5 Z"),
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.2,
        };

        _toggle = new Button
        {
            Content = _diamond,
            Padding = new Thickness(6, 4),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _toggle.Classes.Add("quiet");
        ToolTip.SetTip(_toggle, "Poner o quitar un punto de animación en el cabezal");
        _toggle.Click += (_, _) => Toggled?.Invoke(this, EventArgs.Empty);

        _previous = SmallButton("‹", "Ir al punto anterior");
        _previous.Click += (_, _) => JumpTo(before: true);

        _next = SmallButton("›", "Ir al punto siguiente");
        _next.Click += (_, _) => JumpTo(before: false);

        _clear = SmallButton("×", "Quitar toda la animación de esta propiedad");
        _clear.Click += (_, _) => Cleared?.Invoke(this, EventArgs.Empty);

        _summary = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        _ruler = new PointRuler { Height = 10, Margin = new Thickness(0, 2, 0, 0) };
        _ruler.Picked += (_, at) => Sought?.Invoke(this, at);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(_toggle);
        row.Children.Add(_previous);
        row.Children.Add(_next);
        row.Children.Add(_clear);
        row.Children.Add(_summary);

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(row);
        stack.Children.Add(_ruler);

        Content = stack;
    }

    /// <summary>Se pide poner o quitar un punto en la posición del cabezal.</summary>
    public event EventHandler? Toggled;

    /// <summary>Se pide quitar toda la animación de la propiedad.</summary>
    public event EventHandler? Cleared;

    /// <summary>Se pide mover el cabezal a un instante del clip.</summary>
    public event EventHandler<TimeSpan>? Sought;

    /// <summary>Pone al día lo que se muestra.</summary>
    /// <param name="track">Puntos de la propiedad.</param>
    /// <param name="duration">Cuánto dura el clip, que es el ancho de la regla.</param>
    /// <param name="position">Instante del cabezal dentro del clip.</param>
    public void Show(KeyframeTrack track, TimeSpan duration, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(track);

        _track = track;
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
        _position = position;

        var here = track.HasPointAt(position);
        var animated = !track.IsEmpty;

        _diamond.Fill = here ? Brush("Accent") : Brushes.Transparent;
        _diamond.Stroke = animated ? Brush("Accent") : Brush("TextMuted");

        _summary.Text = track.Points.Count switch
        {
            0 => "sin animar",
            1 => "1 punto",
            var n => $"{n} puntos",
        };

        _summary.Foreground = animated ? Brush("TextMuted") : Brush("TextFaint");

        _previous.IsEnabled = animated;
        _next.IsEnabled = animated;
        _clear.IsEnabled = animated;
        _ruler.IsVisible = animated;

        _ruler.Show(track, _duration, position);
    }

    private void JumpTo(bool before)
    {
        // Se busca con un poco de margen para que, estando justo encima de un punto, «siguiente»
        // no vuelva a caer en el mismo.
        var margin = KeyframeTrack.SameInstant;

        var target = before
            ? _track.Points.LastOrDefault(p => p.At < _position - margin)
            : _track.Points.FirstOrDefault(p => p.At > _position + margin);

        if (target is not null)
        {
            Sought?.Invoke(this, target.At);
        }
    }

    private Button SmallButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = glyph,
            FontSize = 13,
            Padding = new Thickness(7, 1),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.Classes.Add("quiet");
        ToolTip.SetTip(button, tip);
        return button;
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    /// <summary>La regla que dibuja dónde caen los puntos dentro del clip.</summary>
    private sealed class PointRuler : Control
    {
        private KeyframeTrack _points = KeyframeTrack.Empty;
        private TimeSpan _span = TimeSpan.FromSeconds(1);
        private TimeSpan _at;

        internal PointRuler() => Cursor = new Cursor(StandardCursorType.Hand);

        internal event EventHandler<TimeSpan>? Picked;

        internal void Show(KeyframeTrack track, TimeSpan duration, TimeSpan position)
        {
            _points = track;
            _span = duration;
            _at = position;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (Bounds.Width <= 0)
            {
                return;
            }

            // Se salta al punto más cercano al clic, no al instante exacto: la regla es estrecha
            // y acertar al píxel sería pedir demasiado.
            var fraction = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);
            var wanted = _span * fraction;

            var nearest = _points.Points
                .OrderBy(p => Math.Abs((p.At - wanted).TotalSeconds))
                .FirstOrDefault();

            if (nearest is not null)
            {
                Picked?.Invoke(this, nearest.At);
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var width = Bounds.Width;
            if (width <= 0 || _span <= TimeSpan.Zero)
            {
                return;
            }

            var middle = Bounds.Height / 2;
            var line = this.TryFindResource("Line", out var l) && l is IBrush lb ? lb : Brushes.DimGray;
            var accent = this.TryFindResource("Accent", out var a) && a is IBrush ab ? ab : Brushes.DodgerBlue;
            var text = this.TryFindResource("Text", out var t) && t is IBrush tb ? tb : Brushes.White;

            context.DrawLine(new Pen(line, 1), new Point(0, middle), new Point(width, middle));

            // El cabezal, para ver de un vistazo entre qué dos puntos se está.
            var headX = width * Math.Clamp(_at / _span, 0, 1);
            context.DrawLine(new Pen(text, 1), new Point(headX, 0), new Point(headX, Bounds.Height));

            foreach (var point in _points.Points)
            {
                var x = width * Math.Clamp(point.At / _span, 0, 1);
                var diamond = new StreamGeometry();

                using (var draw = diamond.Open())
                {
                    draw.BeginFigure(new Point(x, middle - 4), isFilled: true);
                    draw.LineTo(new Point(x + 4, middle));
                    draw.LineTo(new Point(x, middle + 4));
                    draw.LineTo(new Point(x - 4, middle));
                    draw.EndFigure(true);
                }

                context.DrawGeometry(accent, null, diamond);
            }
        }
    }
}
