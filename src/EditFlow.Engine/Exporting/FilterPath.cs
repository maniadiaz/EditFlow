// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Exporting;

/// <summary>
/// Escribe una ruta de archivo de forma que sobreviva entera dentro de un grafo de filtros.
/// </summary>
/// <remarks>
/// <para>
/// Meter una ruta de Windows en un filtro de FFmpeg no es pasar la cadena y ya: la letra de
/// unidad lleva dos puntos, que es justo el carácter que separa las opciones de un filtro, y una
/// ruta normal lleva barras invertidas, que son el carácter de escape. Sin tratar, <c>C:\fotos</c>
/// se interpreta como una opción llamada <c>C</c> y un valor <c>fotos</c>, y FFmpeg responde con
/// un «No option name near» que no menciona la ruta por ninguna parte.
/// </para>
/// <para>
/// Encima, el analizador aplica <b>dos niveles</b> de desescapado a los argumentos de un filtro:
/// primero el del grafo, después el del propio filtro. Por eso un apóstrofo —de lo más corriente
/// en un nombre de archivo en español o en francés— necesita tres barras invertidas y no una,
/// que es el detalle que hacía fracasar los intentos anteriores y por el que la importación de
/// LUT estuvo aparcada desde la Fase 2.
/// </para>
/// <para>
/// Las reglas de aquí están comprobadas contra el FFmpeg empaquetado con una ruta deliberadamente
/// hostil: con espacios, coma, corchetes y apóstrofo.
/// </para>
/// </remarks>
public static class FilterPath
{
    /// <summary>
    /// Devuelve la ruta lista para usarse como valor de una opción de filtro, ya entrecomillada.
    /// </summary>
    /// <param name="path">Ruta del archivo.</param>
    /// <exception cref="ArgumentException">Si la ruta está vacía.</exception>
    public static string Quote(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // FFmpeg entiende las barras normales en Windows, y así la barra invertida deja de ser
        // un carácter de escape suelto dentro de la cadena.
        var text = path.Replace('\\', '/');

        // Dentro de las comillas todo es literal salvo la propia comilla. Los dos puntos, en
        // cambio, hay que escaparlos igualmente: el analizador los mira antes de entrecomillar.
        text = text.Replace(":", "\\:", StringComparison.Ordinal);

        // Para meter un apóstrofo hay que cerrar la comilla, escribirlo escapado y volver a abrir.
        // Las tres barras son los dos niveles de desescapado: con una el apóstrofo desaparece del
        // nombre, y con dos se traga el resto del grafo.
        text = text.Replace("'", "'\\\\\\''", StringComparison.Ordinal);

        return "'" + text + "'";
    }
}
