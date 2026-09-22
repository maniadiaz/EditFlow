// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.PreviewCache;

/// <summary>
/// Huella de un trozo de la timeline: cambia si y solo si cambia algo que se ve en él.
/// </summary>
/// <remarks>
/// Entran los fragmentos de video (archivo, su tamaño y fecha, y qué intervalo se usa), los
/// textos e imágenes visibles y los ajustes de renderizado. Como los tiempos van medidos desde
/// el inicio del trozo, mover algo fuera de él no cambia su huella: una edición solo invalida
/// los trozos a los que afecta. Y como el nombre de la copia es la huella, deshacer la edición
/// vuelve a encontrar la copia anterior sin renderizar de nuevo.
/// </remarks>
public static class SectionHasher
{
    private const string Version = "v1";

    /// <summary>Calcula la huella de un trozo ya cortado.</summary>
    /// <param name="slice">El trozo, tal como lo devuelve <see cref="SequenceSlicer"/>.</param>
    /// <param name="settings">Ajustes de renderizado.</param>
    /// <param name="fileStamp">Marca de un archivo (tamaño y fecha); permite cachear la lectura del disco.</param>
    public static string Compute(EditSequence slice, PreviewCacheSettings settings, Func<string, string> fileStamp)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fileStamp);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{Version}|{settings.Width}x{settings.Height}@{settings.FrameRate}\n");

        // Dividir un clip no cambia lo que se ve: dos fragmentos seguidos del mismo archivo, uno a
        // continuación del otro, se cuentan como uno. Sin esto, cortar un video con S invalidaría la
        // copia de preview de todo lo que rodea el corte.
        var clips = slice.Video.Clips;
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var sourceOut = clip.SourceOut;

            while (i + 1 < clips.Count
                   && string.Equals(clips[i + 1].Source.Path, clip.Source.Path, StringComparison.OrdinalIgnoreCase)
                   && clips[i + 1].Source.Rotation == clip.Source.Rotation
                   && clips[i + 1].Color == clip.Color
                   && clips[i + 1].SourceIn == sourceOut
                   && clips[i + 1].TransitionIn.IsNone
                   && clips[i + 1].Speed.Equals(clip.Speed)
                   && clips[i + 1].Transform == clip.Transform
                   && clips[i + 1].Filter == clip.Filter
                   && clips[i + 1].Effect == clip.Effect
                   && clips[i + 1].FadeIn == TimeSpan.Zero
                   && clips[i + 1].FadeOut == TimeSpan.Zero)
            {
                i++;
                sourceOut = clips[i].SourceOut;
            }

            // La transición que abre la tanda entra en la huella: cambiar su tipo o duración
            // cambia lo que se ve aunque ningún clip haya cambiado de archivo ni de recorte. La
            // velocidad y el encuadre igual: a otra velocidad o con otro zoom/posición/rotación
            // se decodifican y se muestran otros fotogramas. Un fundido en el clip que se suma a
            // la tanda tampoco vale como continuación lisa: por eso frena la fusión arriba, igual
            // que una transición.
            var transform = clip.Transform;
            text.Append(CultureInfo.InvariantCulture,
                $"c|{clip.Source.Path.ToLowerInvariant()}|{fileStamp(clip.Source.Path)}|{clip.SourceIn.Ticks}|{sourceOut.Ticks}|{clip.Source.Rotation}|{ColorFilter.Build(clip.Color)}|{clip.TransitionIn.Kind}|{clip.TransitionIn.Duration.Ticks}|{clip.Speed:R}|{transform.Scale:R}|{transform.OffsetX:R}|{transform.OffsetY:R}|{transform.Rotation:R}|{clip.Filter}|{clip.Effect}|{clip.FadeIn.Ticks}|{clip.FadeOut.Ticks}\n");
        }

        foreach (var track in slice.OverlayTracks)
        {
            text.Append("t\n");

            foreach (var item in track.Items)
            {
                var transform = item.Transform;
                text.Append(CultureInfo.InvariantCulture,
                    $"o|{item.Kind}|{item.Start.Ticks}|{item.Duration.Ticks}|{transform.CenterX:R}|{transform.CenterY:R}|{transform.Width:R}|{transform.Opacity:R}|{item.FadeIn.Ticks}|{item.FadeOut.Ticks}|");

                if (item.Text is { } style)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"{style.Size:R}|{style.Color}|{style.Bold}|{style.Italic}|{style.Shadow}|{style.FontFamily}|{style.FontFilePath}|{style.Content}");
                }

                if (item.Media is { } video)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"{video.Path.ToLowerInvariant()}|{fileStamp(video.Path)}|{item.SourceIn.Ticks}|{item.AspectRatio:R}|{ColorFilter.Build(item.Color)}");
                }

                if (item.ImagePath is { } image)
                {
                    text.Append(CultureInfo.InvariantCulture, $"{image.ToLowerInvariant()}|{fileStamp(image)}|{item.AspectRatio:R}");
                }

                text.Append('\n');
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    /// <summary>Marca de un archivo: su tamaño y fecha de modificación. Cambia si el archivo se reemplaza.</summary>
    public static string StampOf(string path)
    {
        // Un hueco de la pista principal no tiene archivo.
        if (string.IsNullOrEmpty(path))
        {
            return "gap";
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}")
                : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }
}
