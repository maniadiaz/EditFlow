// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Media;

namespace EditFlow.App.Controls;

/// <summary>
/// Capa transparente sobre la timeline donde se dibuja el cabezal y el cursor de referencia.
/// </summary>
/// <remarks>
/// El cabezal se mueve a cada fotograma de pantalla mientras se reproduce. Si se dibujara dentro
/// de la timeline, cada movimiento la repintaría entera (clips, miniaturas, forma de onda): medido,
/// eso costaba casi medio núcleo de CPU. En su propia capa solo se repinta una línea.
/// </remarks>
public sealed class PlayheadLayer : Control
{
    private TimelineControl? _source;

    /// <summary>La timeline sobre la que se dibuja.</summary>
    public TimelineControl? Source
    {
        get => _source;
        set
        {
            _source = value;
            if (value is not null)
            {
                value.Layer = this;
            }

            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context) =>
        _source?.DrawPlayheadLayer(context, Bounds.Height);
}
