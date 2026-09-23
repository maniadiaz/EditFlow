// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EditFlow.App.Controls;

/// <summary>
/// Un histograma RGB del fotograma que se está viendo: cuántos píxeles hay de cada nivel en cada
/// canal.
/// </summary>
/// <remarks>
/// <para>
/// Es el instrumento que dice lo que el ojo no puede: si los negros están aplastados contra el
/// borde izquierdo se perdió detalle en las sombras, y si las luces se amontonan en el derecho
/// se quemaron. Corregir mirando solo la pantalla depende de cómo esté calibrada; el histograma no.
/// </para>
/// <para>
/// Se calcula en la aplicación a partir del fotograma que el preview ya tiene en memoria, no
/// pidiéndole a FFmpeg el filtro <c>histogram</c>: el fotograma está ahí, recorrerlo cuesta unas
/// décimas de milisegundo y así el instrumento sigue al cabezal sin abrir otro proceso.
/// </para>
/// </remarks>
public sealed class HistogramScope : Control
{
    private const int Levels = 256;

    private readonly int[] _red = new int[Levels];
    private readonly int[] _green = new int[Levels];
    private readonly int[] _blue = new int[Levels];

    private int _peak;

    /// <summary>Crea el instrumento.</summary>
    public HistogramScope() => Height = 90;

    /// <summary>Indica si ya se ha medido algún fotograma.</summary>
    public bool HasData => _peak > 0;

    /// <summary>Vacía el histograma.</summary>
    public void Clear()
    {
        Array.Clear(_red);
        Array.Clear(_green);
        Array.Clear(_blue);
        _peak = 0;
        InvalidateVisual();
    }

    /// <summary>
    /// Mide un fotograma en BGRA, tal como lo entrega el decodificador.
    /// </summary>
    /// <param name="pixels">Píxeles, cuatro bytes por punto.</param>
    /// <param name="stride">Bytes por fila.</param>
    /// <param name="width">Ancho en píxeles.</param>
    /// <param name="height">Alto en píxeles.</param>
    /// <remarks>
    /// <para>
    /// No se recorren todos los píxeles: en un fotograma de 960×540 hay medio millón, y para la
    /// forma de un histograma sobra con una muestra regular. Con el salto que se elige aquí se
    /// miden unos 20.000, que es más que suficiente y cuesta una fracción de milisegundo.
    /// </para>
    /// <para>
    /// Se puede llamar desde el hilo de decodificación: aquí solo se cuentan píxeles. El repintado
    /// va aparte, con <see cref="Refresh"/>, porque ese sí tiene que ocurrir en el de interfaz.
    /// Medir en el hilo de interfaz obligaría además a copiar el fotograma, que se recicla en
    /// cuanto la llamada vuelve.
    /// </para>
    /// </remarks>
    public void Measure(ReadOnlySpan<byte> pixels, int stride, int width, int height)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return;
        }

        Array.Clear(_red);
        Array.Clear(_green);
        Array.Clear(_blue);

        var step = Math.Max(1, (int)Math.Sqrt(width * (long)height / 20000.0));

        for (var y = 0; y < height; y += step)
        {
            var row = y * stride;

            for (var x = 0; x < width; x += step)
            {
                var i = row + (x * 4);
                if (i + 2 >= pixels.Length)
                {
                    break;
                }

                // BGRA: el azul va primero.
                _blue[pixels[i]]++;
                _green[pixels[i + 1]]++;
                _red[pixels[i + 2]]++;
            }
        }

        // El pico marca la altura del dibujo. Se ignoran los dos extremos para calcularlo: un
        // plano sobre fondo negro tiene tantísimos píxeles en el nivel 0 que aplastaría el resto
        // del histograma contra el suelo y no se vería nada.
        _peak = 1;
        for (var level = 2; level < Levels - 2; level++)
        {
            _peak = Math.Max(_peak, Math.Max(_red[level], Math.Max(_green[level], _blue[level])));
        }

    }

    /// <summary>Redibuja con lo último que se midió. Va en el hilo de interfaz.</summary>
    public void Refresh() => InvalidateVisual();

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        var panel = this.TryFindResource("Panel", out var p) && p is IBrush pb ? pb : Brushes.Black;
        var line = this.TryFindResource("Line", out var l) && l is IBrush lb ? lb : Brushes.DimGray;

        context.FillRectangle(panel, new Rect(Bounds.Size));

        // Cuartos, para situar sombras, medios y luces de un vistazo.
        for (var i = 1; i < 4; i++)
        {
            var x = Bounds.Width * i / 4;
            context.DrawLine(new Pen(line, 0.5), new Point(x, 0), new Point(x, Bounds.Height));
        }

        if (_peak > 0)
        {
            // Los tres canales se suman a la vista con mezcla aditiva: donde coinciden, blanco.
            Draw(context, _red, Color.FromArgb(150, 255, 70, 70));
            Draw(context, _green, Color.FromArgb(150, 70, 255, 70));
            Draw(context, _blue, Color.FromArgb(150, 90, 130, 255));
        }

        context.DrawRectangle(null, new Pen(line, 1), new Rect(Bounds.Size));
    }

    private void Draw(DrawingContext context, int[] counts, Color color)
    {
        var geometry = new StreamGeometry();

        using (var draw = geometry.Open())
        {
            draw.BeginFigure(new Point(0, Bounds.Height), isFilled: true);

            for (var level = 0; level < Levels; level++)
            {
                var x = Bounds.Width * level / (Levels - 1.0);
                var value = Math.Min(1.0, counts[level] / (double)_peak);
                draw.LineTo(new Point(x, Bounds.Height * (1 - value)));
            }

            draw.LineTo(new Point(Bounds.Width, Bounds.Height));
            draw.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }
}
