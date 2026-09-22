// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EditFlow.Core.Timeline;

namespace EditFlow.Engine.Playback;

/// <summary>
/// Huella de todo lo que se oye en un montaje: cambia si y solo si cambia el sonido.
/// </summary>
/// <remarks>
/// Renderizar la mezcla de audio de un montaje largo tarda, y mientras tanto el preview no puede usar la
/// copia renderizada. Muchas ediciones no cambian el sonido —dividir un clip, mover un texto, subir un
/// video a otra capa sin tocar su volumen…—, y con esta huella se reconocen para conservar la mezcla ya
/// cargada en vez de volver a renderizarla.
/// </remarks>
public static class AudioMixSignature
{
    /// <summary>Calcula la huella del sonido de una secuencia.</summary>
    public static string Compute(EditSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"d{sequence.Duration.Ticks}\n");

        // Pista principal: tramos con sonido (unidos si son seguidos del mismo archivo) y silencios.
        var clips = sequence.Video.Clips;
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];

            if (!clip.HasOwnAudio)
            {
                var silence = clip.Duration;
                while (i + 1 < clips.Count && !clips[i + 1].HasOwnAudio)
                {
                    i++;
                    silence += clips[i].Duration;
                }

                text.Append(CultureInfo.InvariantCulture, $"s{silence.Ticks}\n");
                continue;
            }

            var sourceOut = clip.SourceOut;
            while (i + 1 < clips.Count
                   && clips[i + 1].HasOwnAudio
                   && string.Equals(clips[i + 1].Source.Path, clip.Source.Path, StringComparison.OrdinalIgnoreCase)
                   && clips[i + 1].SourceIn == sourceOut
                   && Math.Abs(clips[i + 1].AudioGainDb - clip.AudioGainDb) < 0.0001)
            {
                i++;
                sourceOut = clips[i].SourceOut;
            }

            text.Append(CultureInfo.InvariantCulture,
                $"v{clip.Source.Path.ToLowerInvariant()}|{clip.SourceIn.Ticks}|{sourceOut.Ticks}|{clip.AudioGainDb:R}\n");
        }

        // Pistas de audio.
        foreach (var track in sequence.AudioTracks)
        {
            text.Append(CultureInfo.InvariantCulture, $"t{track.IsMuted}|{track.IsSolo}|{track.GainDb:R}\n");
            foreach (var audio in track.Clips)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"a{audio.Source.Path.ToLowerInvariant()}|{audio.SourceIn.Ticks}|{audio.SourceOut.Ticks}|{audio.TimelineStart.Ticks}|{audio.GainDb:R}|{audio.IsMuted}|{audio.FadeIn.Ticks}|{audio.FadeOut.Ticks}\n");
            }
        }

        // Videos superpuestos con sonido, en capas visibles.
        foreach (var layer in sequence.OverlayTracks)
        {
            if (layer.IsHidden)
            {
                continue;
            }

            foreach (var item in layer.Items)
            {
                if (item is { Kind: OverlayKind.Video, PlaysAudio: true, Media: not null })
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"o{item.Media.Path.ToLowerInvariant()}|{item.SourceIn.Ticks}|{item.Start.Ticks}|{item.Duration.Ticks}|{item.AudioGainDb:R}\n");
                }
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 12).ToLowerInvariant();
    }
}
