// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Immutable;
using System.Globalization;
using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Un punto de una curva de tonos: qué nivel de entrada se convierte en qué nivel de salida.</summary>
/// <param name="In">Nivel de entrada, de 0 (negro) a 1 (blanco).</param>
/// <param name="Out">Nivel de salida, de 0 a 1.</param>
public sealed record CurvePoint(double In, double Out)
{
    /// <summary>Copia con ambos valores dentro de 0 a 1.</summary>
    public CurvePoint Clamped() => new(
        double.IsFinite(In) ? Math.Clamp(In, 0, 1) : 0,
        double.IsFinite(Out) ? Math.Clamp(Out, 0, 1) : 0);
}

/// <summary>
/// Una curva de tonos: cómo se reasignan los niveles de un canal.
/// </summary>
/// <remarks>
/// Sin puntos, o con solo los dos extremos en su sitio, no cambia nada. Es el control de color
/// más directo que existe —lo que se dibuja es literalmente lo que le pasa a la imagen— y el que
/// permite cosas que ningún deslizador alcanza, como levantar los negros para un aspecto de cine
/// sin tocar las luces.
/// </remarks>
public sealed record ToneCurve
{
    private readonly ImmutableArray<CurvePoint> _points;

    private ToneCurve(ImmutableArray<CurvePoint> points) => _points = points;

    /// <summary>La curva que no cambia nada.</summary>
    public static ToneCurve Identity { get; } = new([]);

    /// <summary>Puntos, ordenados por nivel de entrada.</summary>
    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>Indica si deja el canal exactamente como estaba.</summary>
    public bool IsIdentity => _points.Length == 0 || _points.All(p => Math.Abs(p.In - p.Out) < 0.002);

    /// <summary>Crea una curva a partir de sus puntos, ordenados y acotados.</summary>
    public static ToneCurve FromPoints(IEnumerable<CurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var ordered = points
            .Select(p => p.Clamped())
            .GroupBy(p => Math.Round(p.In, 3))
            .Select(g => g.Last())
            .OrderBy(p => p.In)
            .ToImmutableArray();

        return new ToneCurve(ordered);
    }

    /// <summary>Copia con un punto añadido o movido.</summary>
    public ToneCurve With(double input, double output) =>
        FromPoints(_points.Where(p => Math.Abs(p.In - input) > 0.02).Append(new CurvePoint(input, output)));

    /// <summary>Copia sin el punto que haya cerca de un nivel de entrada.</summary>
    public ToneCurve Without(double input) =>
        FromPoints(_points.Where(p => Math.Abs(p.In - input) > 0.02));

    /// <summary>La curva escrita como la espera FFmpeg: pares <c>entrada/salida</c> separados por espacios.</summary>
    public string ToFilterValue() => string.Join(
        ' ',
        _points.Select(p => string.Create(
            CultureInfo.InvariantCulture, $"{p.In:0.###}/{p.Out:0.###}")));
}

/// <summary>
/// Una rueda de color: hacia dónde se empuja el color de un tramo de la imagen.
/// </summary>
/// <param name="Red">De -1 (quitar rojo, o sea añadir cian) a 1 (añadir rojo).</param>
/// <param name="Green">De -1 (añadir magenta) a 1 (añadir verde).</param>
/// <param name="Blue">De -1 (añadir amarillo) a 1 (añadir azul).</param>
public sealed record ColorWheel(double Red = 0, double Green = 0, double Blue = 0)
{
    /// <summary>Sin empujar nada.</summary>
    public static ColorWheel Neutral { get; } = new();

    /// <summary>Indica si no mueve el color.</summary>
    public bool IsNeutral =>
        Math.Abs(Red) < 0.002 && Math.Abs(Green) < 0.002 && Math.Abs(Blue) < 0.002;

    /// <summary>Copia con los tres valores dentro de -1 a 1.</summary>
    public ColorWheel Clamped() => new(Clamp(Red), Clamp(Green), Clamp(Blue));

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
}

/// <summary>Familias de color sobre las que se puede actuar por separado.</summary>
/// <remarks>
/// Son las mismas que entiende el filtro <c>selectivecolor</c> de FFmpeg. Permiten lo que en un
/// editor grande se llama «HSL secundario»: cambiar solo los rojos de una camiseta sin tocar el
/// resto de la imagen.
/// </remarks>
public enum ColorFamily
{
    /// <summary>Ninguna: el ajuste selectivo está apagado.</summary>
    None,

    /// <summary>Rojos.</summary>
    Reds,

    /// <summary>Amarillos.</summary>
    Yellows,

    /// <summary>Verdes.</summary>
    Greens,

    /// <summary>Cianes.</summary>
    Cyans,

    /// <summary>Azules.</summary>
    Blues,

    /// <summary>Magentas.</summary>
    Magentas,
}

/// <summary>
/// Ajuste de una sola familia de color, sin tocar las demás.
/// </summary>
/// <param name="Family">Familia sobre la que actúa.</param>
/// <param name="CyanRed">De -1 (hacia el cian) a 1 (hacia el rojo).</param>
/// <param name="MagentaGreen">De -1 (hacia el magenta) a 1 (hacia el verde).</param>
/// <param name="YellowBlue">De -1 (hacia el amarillo) a 1 (hacia el azul).</param>
/// <param name="Lightness">De -1 (más oscuro) a 1 (más claro).</param>
public sealed record SelectiveColor(
    ColorFamily Family = ColorFamily.None,
    double CyanRed = 0,
    double MagentaGreen = 0,
    double YellowBlue = 0,
    double Lightness = 0)
{
    /// <summary>Sin ajuste selectivo.</summary>
    public static SelectiveColor None { get; } = new();

    /// <summary>Indica si no cambia nada.</summary>
    public bool IsNone => Family == ColorFamily.None
        || (Math.Abs(CyanRed) < 0.002 && Math.Abs(MagentaGreen) < 0.002
            && Math.Abs(YellowBlue) < 0.002 && Math.Abs(Lightness) < 0.002);

    /// <summary>Copia con los cuatro valores dentro de -1 a 1.</summary>
    public SelectiveColor Clamped() =>
        new(Family, Clamp(CyanRed), Clamp(MagentaGreen), Clamp(YellowBlue), Clamp(Lightness));

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
}

/// <summary>
/// Corrección de color avanzada de un clip: curvas, ruedas, color selectivo y LUT.
/// </summary>
/// <remarks>
/// <para>
/// Va aparte de <see cref="ColorAdjust"/>, que son los cuatro deslizadores de siempre, porque
/// resuelven cosas distintas: aquel es el ajuste rápido que cubre el 90 % de los casos, este es
/// el que hace falta cuando ese 90 % no basta. Se aplican los dos, en ese orden.
/// </para>
/// <para>
/// Todo es inmutable, como el resto de ajustes de un clip.
/// </para>
/// </remarks>
/// <param name="Master">Curva que se aplica a los tres canales por igual: contraste y brillo con forma.</param>
/// <param name="Red">Curva del canal rojo.</param>
/// <param name="Green">Curva del canal verde.</param>
/// <param name="Blue">Curva del canal azul.</param>
/// <param name="Shadows">Rueda de las sombras.</param>
/// <param name="Midtones">Rueda de los medios.</param>
/// <param name="Highlights">Rueda de las luces.</param>
/// <param name="Selective">Ajuste de una familia de color concreta.</param>
/// <param name="LutPath">Archivo <c>.cube</c> a aplicar, o <see langword="null"/>.</param>
public sealed record ColorGrade(
    ToneCurve? Master = null,
    ToneCurve? Red = null,
    ToneCurve? Green = null,
    ToneCurve? Blue = null,
    ColorWheel? Shadows = null,
    ColorWheel? Midtones = null,
    ColorWheel? Highlights = null,
    SelectiveColor? Selective = null,
    string? LutPath = null)
{
    /// <summary>Sin corrección.</summary>
    public static ColorGrade None { get; } = new();

    /// <summary>Curva maestra, nunca nula.</summary>
    public ToneCurve MasterCurve => Master ?? ToneCurve.Identity;

    /// <summary>Curva del rojo, nunca nula.</summary>
    public ToneCurve RedCurve => Red ?? ToneCurve.Identity;

    /// <summary>Curva del verde, nunca nula.</summary>
    public ToneCurve GreenCurve => Green ?? ToneCurve.Identity;

    /// <summary>Curva del azul, nunca nula.</summary>
    public ToneCurve BlueCurve => Blue ?? ToneCurve.Identity;

    /// <summary>Rueda de sombras, nunca nula.</summary>
    public ColorWheel ShadowWheel => Shadows ?? ColorWheel.Neutral;

    /// <summary>Rueda de medios, nunca nula.</summary>
    public ColorWheel MidtoneWheel => Midtones ?? ColorWheel.Neutral;

    /// <summary>Rueda de luces, nunca nula.</summary>
    public ColorWheel HighlightWheel => Highlights ?? ColorWheel.Neutral;

    /// <summary>Ajuste selectivo, nunca nulo.</summary>
    public SelectiveColor SelectiveAdjust => Selective ?? SelectiveColor.None;

    /// <summary>Indica si deja la imagen exactamente como está.</summary>
    public bool IsNone =>
        MasterCurve.IsIdentity && RedCurve.IsIdentity && GreenCurve.IsIdentity && BlueCurve.IsIdentity
        && ShadowWheel.IsNeutral && MidtoneWheel.IsNeutral && HighlightWheel.IsNeutral
        && SelectiveAdjust.IsNone
        && string.IsNullOrWhiteSpace(LutPath);

    /// <summary>Indica si hay alguna curva con forma.</summary>
    public bool HasCurves =>
        !MasterCurve.IsIdentity || !RedCurve.IsIdentity || !GreenCurve.IsIdentity || !BlueCurve.IsIdentity;

    /// <summary>Indica si alguna rueda empuja el color.</summary>
    public bool HasWheels => !ShadowWheel.IsNeutral || !MidtoneWheel.IsNeutral || !HighlightWheel.IsNeutral;
}

/// <summary>Cambia la corrección de color avanzada de un clip.</summary>
public sealed class SetColorGradeCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly ColorGrade _grade;
    private readonly ColorGrade _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="clip">Clip.</param>
    /// <param name="grade">Nueva corrección.</param>
    /// <param name="previous">
    /// La que había antes, si ya se fue mostrando mientras se arrastraba un control; sin ella se
    /// toma la actual.
    /// </param>
    public SetColorGradeCommand(Clip clip, ColorGrade grade, ColorGrade? previous = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(grade);

        _clip = clip;
        _grade = grade;
        _previous = previous ?? clip.Grade;
    }

    /// <inheritdoc/>
    public string Description => "Corregir el color";

    /// <inheritdoc/>
    public void Execute() => _clip.Grade = _grade;

    /// <inheritdoc/>
    public void Undo() => _clip.Grade = _previous;
}
