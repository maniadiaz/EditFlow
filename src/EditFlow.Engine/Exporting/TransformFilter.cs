// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Convierte un encuadre (zoom, posición, rotación) en los filtros de FFmpeg que lo aplican.
/// </summary>
/// <remarks>
/// <para>
/// Único sitio donde el encuadre se traduce a filtros, usado por igual en el preview en vivo y
/// en la exportación. Trabaja sobre las dimensiones del propio fotograma del clip (o de la
/// resolución a la que se decodifica en el preview, que es proporcional): el resultado tiene el
/// mismo tamaño que la entrada, así que encaja sin más en el resto de la rama —normalización de
/// tamaño y color— que ya existía antes del encuadre.
/// </para>
/// <para>
/// Con keyframes cada número pasa a ser una expresión que FFmpeg reevalúa en cada fotograma. Sin
/// ellos sigue emitiendo exactamente los mismos argumentos de siempre: un clip sin animar no
/// paga nada por que esto exista.
/// </para>
/// </remarks>
public static class TransformFilter
{
    /// <summary>Filtros que aplican el encuadre, o <see langword="null"/> si no hay nada que aplicar.</summary>
    /// <param name="transform">Encuadre fijo.</param>
    /// <param name="width">Ancho del fotograma de entrada.</param>
    /// <param name="height">Alto del fotograma de entrada.</param>
    /// <param name="animation">Animaciones del clip, si las tiene.</param>
    /// <param name="offset">Segundos que sumar al instante de cada punto para llevarlo al reloj del filtro.</param>
    /// <param name="timeScale">Cuántos segundos del reloj del filtro dura un segundo del clip.</param>
    public static string? Build(
        ClipTransform? transform,
        int width,
        int height,
        Animation? animation = null,
        TimeSpan offset = default,
        double timeScale = 1)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var animated = KeyframeExpression.IsAnimated(
            animation,
            AnimatedProperty.Scale,
            AnimatedProperty.OffsetX,
            AnimatedProperty.OffsetY,
            AnimatedProperty.Rotation);

        if (!animated && (transform is null || transform.IsNone))
        {
            return null;
        }

        var t = (transform ?? ClipTransform.None).Clamped();

        return animated
            ? BuildAnimated(t, width, height, animation!, offset, timeScale)
            : BuildStatic(t, width, height);
    }

    private static string BuildStatic(ClipTransform t, int width, int height)
    {
        var scaledWidth = EvenRound(width * t.Scale);
        var scaledHeight = EvenRound(height * t.Scale);

        var cropX = Math.Clamp(
            EvenRound(((scaledWidth - width) / 2.0) + (t.OffsetX * width)), 0, Math.Max(0, scaledWidth - width));
        var cropY = Math.Clamp(
            EvenRound(((scaledHeight - height) / 2.0) + (t.OffsetY * height)), 0, Math.Max(0, scaledHeight - height));

        var filters = new List<string>
        {
            $"scale={scaledWidth}:{scaledHeight}",
            $"crop={width}:{height}:{cropX}:{cropY}",
        };

        // Girar deja ver las esquinas del fotograma original si no se acercó lo suficiente antes:
        // se rellenan de negro, igual que en cualquier editor, y es el propio zoom quien lo evita.
        if (Math.Abs(t.Rotation) >= 0.05)
        {
            var radians = t.Rotation * Math.PI / 180;
            filters.Add($"rotate={Number(radians)}:ow={width}:oh={height}:c=black");
        }

        return string.Join(',', filters);
    }

    private static string BuildAnimated(
        ClipTransform t, int width, int height, Animation animation, TimeSpan offset, double timeScale)
    {
        // El zoom se acota dentro de la propia expresión: un punto por debajo de 1 dejaría el
        // fotograma más pequeño que el recorte y FFmpeg abortaría a mitad de la exportación,
        // cuando llegara a ese instante.
        var scale = Clamp(
            KeyframeExpression.ValueOrExpression(
                animation.Track(AnimatedProperty.Scale), t.Scale, offset, timeScale: timeScale),
            ClipTransform.MinimumScale,
            ClipTransform.MaximumScale);

        // A 4:2:0 un ancho o un alto impares no son representables, así que se redondean a par
        // fotograma a fotograma, no una sola vez como en el caso fijo.
        var scaledWidth = $"2*floor({width}*({scale})/2)";
        var scaledHeight = $"2*floor({height}*({scale})/2)";

        var offsetX = KeyframeExpression.ValueOrExpression(
            animation.Track(AnimatedProperty.OffsetX), t.OffsetX, offset, timeScale: timeScale);
        var offsetY = KeyframeExpression.ValueOrExpression(
            animation.Track(AnimatedProperty.OffsetY), t.OffsetY, offset, timeScale: timeScale);

        // 'iw'/'ih' son las del fotograma ya escalado, así que el recorte sigue al zoom solo, sin
        // repetir aquí su expresión. Se acota al margen que el zoom deja: sin esto, un
        // desplazamiento grande con poco zoom pediría recortar fuera del fotograma.
        var cropX = Clamp($"(iw-{width})/2+({offsetX})*{width}", "0", $"iw-{width}");
        var cropY = Clamp($"(ih-{height})/2+({offsetY})*{height}", "0", $"ih-{height}");

        var filters = new List<string>
        {
            $"scale=w='{scaledWidth}':h='{scaledHeight}':eval=frame",
            $"crop={width}:{height}:x='{cropX}':y='{cropY}'",
        };

        var rotationTrack = animation.Track(AnimatedProperty.Rotation);
        if (rotationTrack.IsAnimated || Math.Abs(t.Rotation) >= 0.05)
        {
            // El filtro trabaja en radianes y el modelo guarda grados.
            var radians = KeyframeExpression.ValueOrExpression(
                rotationTrack, t.Rotation, offset, scale: Math.PI / 180, timeScale: timeScale);

            filters.Add($"rotate=a='{radians}':ow={width}:oh={height}:c=black");
        }

        return string.Join(',', filters);
    }

    private static string Clamp(string expression, double minimum, double maximum) =>
        $"min({Number(maximum)},max({Number(minimum)},{expression}))";

    private static string Clamp(string expression, string minimum, string maximum) =>
        $"min({maximum},max({minimum},{expression}))";

    private static int EvenRound(double value) => 2 * (int)Math.Round(value / 2.0);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
