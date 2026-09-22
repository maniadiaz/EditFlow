// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>
/// Encuadre de un clip de video: zoom, posición y rotación sobre su propio fotograma.
/// </summary>
/// <param name="Scale">
/// Cuánto se acerca. 1 muestra el fotograma completo; 2 muestra la mitad (recortada del centro,
/// salvo que <see cref="OffsetX"/>/<see cref="OffsetY"/> lo desplacen), ampliada a como estaba.
/// </param>
/// <param name="OffsetX">Desplazamiento horizontal del recorte, como fracción del fotograma.</param>
/// <param name="OffsetY">Desplazamiento vertical del recorte, como fracción del fotograma.</param>
/// <param name="Rotation">Giro en grados, sentido horario.</param>
/// <remarks>
/// Es un encuadre fijo para todo el clip, no una animación: mismo espíritu que el ajuste de
/// color o la velocidad, un valor que se aplica igual del primer al último fotograma. Rotar sin
/// haber acercado lo suficiente deja ver las esquinas del fotograma original —negras, como
/// pasaría en cualquier editor—; el propio deslizador de zoom es quien lo corrige, no un cálculo
/// automático que adivine cuánto hace falta.
/// </remarks>
public sealed record ClipTransform(double Scale, double OffsetX, double OffsetY, double Rotation)
{
    /// <summary>Encuadre normal: el fotograma completo, sin girar.</summary>
    public static ClipTransform None { get; } = new(1, 0, 0, 0);

    /// <summary>Sin acercar.</summary>
    public static double MinimumScale => 1;

    /// <summary>Tope superior: más allá, el recorte deja de aportar nada útil.</summary>
    public static double MaximumScale => 5;

    /// <summary>Indica si no cambia nada respecto a mostrar el fotograma tal cual.</summary>
    public bool IsNone =>
        Scale <= MinimumScale + 0.0001 &&
        Math.Abs(OffsetX) < 0.0001 &&
        Math.Abs(OffsetY) < 0.0001 &&
        Math.Abs(Rotation) < 0.05;

    /// <summary>Acota a los rangos admitidos, incluido el desplazamiento máximo que permite la escala actual.</summary>
    public ClipTransform Clamped()
    {
        var scale = Math.Clamp(Scale, MinimumScale, MaximumScale);

        // Con esta escala, el recorte no puede salirse del fotograma: como mucho se desplaza la
        // mitad de lo que el zoom dejó de sobra a cada lado.
        var maxOffset = (scale - 1) / 2;
        var offsetX = Math.Clamp(OffsetX, -maxOffset, maxOffset);
        var offsetY = Math.Clamp(OffsetY, -maxOffset, maxOffset);

        // Normalizado a (-180, 180]: un giro de 370° debe verse y guardarse como 10°.
        var rotation = (((Rotation + 180) % 360) + 360) % 360 - 180;

        return new ClipTransform(scale, offsetX, offsetY, rotation);
    }
}

/// <summary>Cambia el encuadre (zoom, posición, rotación) de un clip de la pista principal.</summary>
public sealed class SetTransformCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly ClipTransform _transform;
    private readonly ClipTransform _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="clip">Clip cuyo encuadre se cambia.</param>
    /// <param name="transform">Nuevo encuadre; usa <see cref="ClipTransform.None"/> para restablecerlo.</param>
    /// <param name="previous">
    /// Encuadre que había antes, si ya se fue mostrando mientras se arrastraba un tirador; sin él
    /// se toma el actual, que es lo correcto cuando el cambio no se ha aplicado todavía.
    /// </param>
    public SetTransformCommand(Clip clip, ClipTransform transform, ClipTransform? previous = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(transform);
        _clip = clip;
        _transform = transform.Clamped();
        _previous = previous ?? clip.Transform;
    }

    /// <inheritdoc/>
    public string Description => "Cambiar encuadre";

    /// <inheritdoc/>
    public void Execute() => _clip.Transform = _transform;

    /// <inheritdoc/>
    public void Undo() => _clip.Transform = _previous;
}
