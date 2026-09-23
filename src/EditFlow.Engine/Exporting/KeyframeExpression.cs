// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte una lista de puntos de animación en la expresión de FFmpeg que la evalúa
/// fotograma a fotograma.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg no tiene keyframes: tiene opciones que aceptan una expresión y la vuelven a evaluar
/// en cada fotograma, con <c>t</c> como el instante actual. Una animación de tramos rectos se
/// escribe entonces como una cadena de condicionales anidados, uno por tramo.
/// </para>
/// <para>
/// El instante <c>t</c> que ve el filtro no es el de la timeline sino el de su propia entrada,
/// y los puntos se guardan contados desde el inicio del clip: por eso todas las expresiones
/// llevan un desfase. En la exportación vale cero —cada clip abre su archivo ya recortado— pero
/// en el preview en vivo el decodificador se abre a mitad del clip, y sin restar ese salto la
/// animación empezaría de cero cada vez que se mueve el cabezal.
/// </para>
/// </remarks>
public static class KeyframeExpression
{
    /// <summary>
    /// Expresión que da el valor de una propiedad, o <see langword="null"/> si no está animada.
    /// </summary>
    /// <param name="track">Puntos de la propiedad.</param>
    /// <param name="offset">
    /// Segundos que sumar al instante de cada punto, una vez aplicado <paramref name="timeScale"/>.
    /// Cero en la exportación de un clip; el instante de la timeline en la rama de una capa.
    /// </param>
    /// <param name="scale">Factor por el que multiplicar cada valor, para pasarlo a píxeles o a lo que pida el filtro.</param>
    /// <param name="bias">Valor que sumar después de escalar.</param>
    /// <param name="time">
    /// Nombre de la variable del instante actual. Casi todos los filtros la llaman <c>t</c>; en
    /// <c>geq</c> se llama <c>T</c>, y usar la minúscula ahí hace que FFmpeg rechace la expresión
    /// entera con un error que no menciona el tiempo por ninguna parte.
    /// </param>
    /// <param name="timeScale">
    /// Cuántos segundos del reloj del filtro dura un segundo del clip. Vale 1 salvo en el preview
    /// de un clip acelerado o ralentizado, donde el decodificador entrega el material a su
    /// velocidad original y es la aplicación quien lleva el reloj.
    /// </param>
    public static string? Build(
        KeyframeTrack? track,
        TimeSpan offset = default,
        double scale = 1,
        double bias = 0,
        string time = "t",
        double timeScale = 1)
    {
        if (track is null || !track.IsAnimated)
        {
            return null;
        }

        var points = track.Points;
        var builder = new StringBuilder();
        var open = 0;

        // Antes del primer punto el valor no se mueve: se queda en el del primero.
        builder.Append("if(lt(").Append(time).Append(',').Append(At(points[0].At, offset, timeScale)).Append("),")
               .Append(Number(Map(points[0].Value, scale, bias))).Append(',');
        open++;

        // Un condicional por tramo, anidados en el 'si no' del anterior: el primero cuyo instante
        // final queda por delante del actual es el que manda.
        for (var i = 1; i < points.Count; i++)
        {
            var from = points[i - 1];
            var to = points[i];
            var span = (to.At - from.At).TotalSeconds * timeScale;

            var a = Map(from.Value, scale, bias);
            var b = Map(to.Value, scale, bias);

            builder.Append("if(lt(").Append(time).Append(',').Append(At(to.At, offset, timeScale)).Append("),");
            open++;

            if (span <= 0)
            {
                // Dos puntos en el mismo instante son un corte seco, no una rampa.
                builder.Append(Number(b));
            }
            else
            {
                builder.Append(Number(a)).Append('+').Append(Number(b - a))
                       .Append("*(").Append(time).Append('-').Append(At(from.At, offset, timeScale))
                       .Append(")/").Append(Number(span));
            }

            builder.Append(',');
        }

        // Pasado el último punto el valor se queda ahí, igual que antes del primero.
        builder.Append(Number(Map(points[^1].Value, scale, bias)));
        builder.Append(')', open);

        return builder.ToString();
    }

    /// <summary>
    /// Expresión de una propiedad, o el número fijo que corresponda si no está animada.
    /// </summary>
    /// <remarks>
    /// Es lo que usan los filtros que aceptan indistintamente una cosa u otra: así un clip sin
    /// animar produce exactamente los mismos argumentos que antes de que existieran los
    /// keyframes, sin coste ni riesgo de que cambie la imagen.
    /// </remarks>
    public static string ValueOrExpression(
        KeyframeTrack? track,
        double staticValue,
        TimeSpan offset = default,
        double scale = 1,
        double bias = 0,
        string time = "t",
        double timeScale = 1) =>
        Build(track, offset, scale, bias, time, timeScale) ?? Number(Map(staticValue, scale, bias));

    /// <summary>Indica si alguna de las propiedades indicadas cambia a lo largo del clip.</summary>
    public static bool IsAnimated(Animation? animation, params AnimatedProperty[] properties) =>
        animation is not null && properties.Any(p => animation.Track(p).IsAnimated);

    /// <summary>Formatea un número igual en cualquier idioma del sistema.</summary>
    public static string Number(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static double Map(double value, double scale, double bias) => (value * scale) + bias;

    /// <summary>Instante de un punto, llevado al reloj que ve el filtro.</summary>
    private static string At(TimeSpan point, TimeSpan offset, double timeScale) =>
        Number((point.TotalSeconds * timeScale) + offset.TotalSeconds);
}
