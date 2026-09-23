// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>
/// Recorte por color (pantalla verde) de un video superpuesto: vuelve transparente todo lo que se
/// parezca a un color, de modo que se vea lo que hay debajo.
/// </summary>
/// <param name="Enabled">Si el recorte está activo. Apagado, el video se ve entero.</param>
/// <param name="Color">Color que se elimina, en <c>#RRGGBB</c>.</param>
/// <param name="Similarity">
/// Cuánto se aparta un color del elegido y aun así se borra, de 0,01 a 1. Subirlo recorta más fondo;
/// pasarse empieza a comerse al sujeto.
/// </param>
/// <param name="Blend">
/// Suavizado del borde, de 0 a 1. En 0 cada píxel está dentro o fuera y el contorno queda dentado;
/// subirlo deja un borde semitransparente que se integra mejor.
/// </param>
/// <param name="Despill">
/// Si se quita el tinte que el fondo derrama sobre el sujeto. Es lo que borra el halo verdoso en el
/// pelo y los hombros; no toca la transparencia, solo el color.
/// </param>
/// <remarks>
/// <para>
/// Es un valor inmutable, como <see cref="ColorAdjust"/>: cambiarlo crea otro. Los mismos números
/// alimentan el preview y la exportación, convertidos a filtros de FFmpeg en un único sitio.
/// </para>
/// <para>
/// Solo tiene sentido en un video de una capa: recortar el fondo de un clip de la pista principal
/// no dejaría ver nada debajo, porque debajo no hay nada.
/// </para>
/// </remarks>
public sealed record ChromaKey(
    bool Enabled = false,
    string Color = ChromaKey.DefaultColor,
    double Similarity = 0.2,
    double Blend = 0.05,
    bool Despill = true)
{
    /// <summary>Verde de croma estándar, el de casi cualquier fondo comprado para esto.</summary>
    public const string DefaultColor = "#00B140";

    /// <summary>Azul de croma estándar, el que se usa cuando el sujeto lleva algo verde.</summary>
    public const string BlueColor = "#0047BB";

    /// <summary>Valor mínimo de <see cref="Similarity"/>.</summary>
    public const double MinimumSimilarity = 0.01;

    /// <summary>Valor máximo de <see cref="Similarity"/>.</summary>
    public const double MaximumSimilarity = 1;

    /// <summary>Sin recorte: el video se ve entero.</summary>
    public static ChromaKey None { get; } = new();

    /// <summary>Recorte listo para un fondo verde.</summary>
    public static ChromaKey Green { get; } = new(true);

    /// <summary>Recorte listo para un fondo azul.</summary>
    public static ChromaKey Blue { get; } = new(true, BlueColor);

    /// <summary>Ajusta cada valor a su rango y normaliza el color.</summary>
    public ChromaKey Clamped() => new(
        Enabled,
        Normalize(Color),
        double.IsFinite(Similarity) ? Math.Clamp(Similarity, MinimumSimilarity, MaximumSimilarity) : 0.2,
        double.IsFinite(Blend) ? Math.Clamp(Blend, 0, 1) : 0,
        Despill);

    /// <summary>
    /// Qué canal domina el color elegido: <c>"green"</c> o <c>"blue"</c>.
    /// </summary>
    /// <remarks>
    /// El filtro que quita el derrame de color solo conoce esos dos fondos, que son los dos que se
    /// usan de verdad. Con cualquier otro color se elige el más cercano de los dos.
    /// </remarks>
    public string SpillChannel => TryParse(Color, out _, out var green, out var blue) && blue > green
        ? "blue"
        : "green";

    /// <summary>Deja un color en <c>#RRGGBB</c> en mayúsculas, o devuelve el verde por defecto si no se entiende.</summary>
    public static string Normalize(string? value)
    {
        if (!TryParse(value, out var red, out var green, out var blue))
        {
            return DefaultColor;
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{red:X2}{green:X2}{blue:X2}");
    }

    /// <summary>Lee un color en <c>#RRGGBB</c> o <c>RRGGBB</c>.</summary>
    public static bool TryParse(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;

        var text = value?.Trim().TrimStart('#');
        if (text is not { Length: 6 })
        {
            return false;
        }

        if (!byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
            || !byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
            || !byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue))
        {
            return false;
        }

        return true;
    }
}

/// <summary>Cambia el recorte por color de un video superpuesto.</summary>
public sealed class SetChromaKeyCommand : IUndoableCommand
{
    private readonly OverlayItem _item;
    private readonly ChromaKey _key;
    private readonly ChromaKey _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="item">Video superpuesto.</param>
    /// <param name="key">Nuevo recorte.</param>
    /// <param name="previous">
    /// Recorte que había antes, si ya se fue mostrando mientras se arrastraba un deslizador; sin él
    /// se toma el actual, que es lo correcto cuando el cambio no se ha aplicado todavía.
    /// </param>
    public SetChromaKeyCommand(OverlayItem item, ChromaKey key, ChromaKey? previous = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(key);

        if (item.Kind != OverlayKind.Video)
        {
            throw new ArgumentException("El recorte por color solo se aplica a un video de una capa.", nameof(item));
        }

        _item = item;
        _key = key.Clamped();
        _previous = previous ?? item.ChromaKey;
    }

    /// <inheritdoc/>
    public string Description => _key.Enabled ? "Recortar el fondo" : "Quitar el recorte del fondo";

    /// <inheritdoc/>
    public void Execute() => _item.ChromaKey = _key;

    /// <inheritdoc/>
    public void Undo() => _item.ChromaKey = _previous;
}
