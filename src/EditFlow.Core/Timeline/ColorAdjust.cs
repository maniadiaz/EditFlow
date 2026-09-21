// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Ajuste de color de un clip: cuatro controles de -100 a 100, donde 0 deja la imagen como está.</summary>
/// <param name="Exposure">Exposición: aclara u oscurece toda la imagen, como abrir o cerrar el diafragma.</param>
/// <param name="Contrast">Contraste: separa más (positivo) o menos (negativo) las luces y las sombras.</param>
/// <param name="Saturation">Saturación: -100 deja la imagen en blanco y negro, 100 duplica la intensidad de los colores.</param>
/// <param name="Temperature">Temperatura: negativo enfría la imagen (azulada), positivo la calienta (anaranjada).</param>
/// <remarks>
/// Es un valor inmutable: cambiarlo crea otro. Los mismos números se usan en el preview y en la exportación,
/// que los convierten en filtros de FFmpeg en un único sitio para que coincidan.
/// </remarks>
public sealed record ColorAdjust(double Exposure = 0, double Contrast = 0, double Saturation = 0, double Temperature = 0)
{
    /// <summary>Rango de cada control.</summary>
    public const double Limit = 100;

    /// <summary>Sin ajuste.</summary>
    public static ColorAdjust None { get; } = new();

    /// <summary>Indica si deja la imagen exactamente como está.</summary>
    public bool IsNone => Math.Abs(Exposure) < 0.05 && Math.Abs(Contrast) < 0.05
        && Math.Abs(Saturation) < 0.05 && Math.Abs(Temperature) < 0.05;

    /// <summary>Ajusta cada control a su rango y descarta valores no numéricos.</summary>
    public ColorAdjust Clamped() => new(Clamp(Exposure), Clamp(Contrast), Clamp(Saturation), Clamp(Temperature));

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, -Limit, Limit) : 0;
}

/// <summary>Cambia el ajuste de color de un clip de video o de un video superpuesto.</summary>
public sealed class SetColorCommand : IUndoableCommand
{
    private readonly Clip? _clip;
    private readonly OverlayItem? _item;
    private readonly ColorAdjust _color;
    private ColorAdjust _previous = ColorAdjust.None;

    /// <summary>Crea la operación sobre un clip de la pista principal.</summary>
    /// <param name="clip">Clip.</param>
    /// <param name="color">Nuevo ajuste.</param>
    /// <param name="previous">
    /// Ajuste que había antes, si ya se fue mostrando mientras se arrastraba un deslizador; sin él se
    /// toma el actual, que es lo correcto cuando el cambio no se ha aplicado todavía.
    /// </param>
    public SetColorCommand(Clip clip, ColorAdjust color, ColorAdjust? previous = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(color);
        _clip = clip;
        _color = color.Clamped();
        _previous = previous ?? clip.Color;
    }

    /// <summary>Crea la operación sobre un video superpuesto.</summary>
    public SetColorCommand(OverlayItem item, ColorAdjust color, ColorAdjust? previous = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(color);
        _item = item;
        _color = color.Clamped();
        _previous = previous ?? item.Color;
    }

    /// <inheritdoc/>
    public string Description => "Ajustar color";

    /// <inheritdoc/>
    public void Execute() => Set(_color);

    /// <inheritdoc/>
    public void Undo() => Set(_previous);

    private void Set(ColorAdjust color)
    {
        if (_clip is not null)
        {
            _clip.Color = color;
        }
        else if (_item is not null)
        {
            _item.Color = color;
        }
    }
}
