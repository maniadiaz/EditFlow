// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using EditFlow.App.Controls;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Animación por puntos: las tiras de keyframes de los paneles de encuadre, capa y audio.
public partial class MainWindow
{
    /// <summary>
    /// Qué anima cada tira: sobre qué elemento actúa y qué propiedades toca a la vez.
    /// </summary>
    /// <remarks>
    /// La posición lleva dos propiedades porque se arrastra como una sola cosa, sobre el preview y
    /// en el panel: pedirle a alguien que ponga un punto en X y otro en Y para mover algo en
    /// diagonal sería absurdo.
    /// </remarks>
    private sealed record KeyframeBinding(
        Func<IAnimatable?> Target,
        AnimatedProperty[] Properties,
        string Label);

    private readonly Dictionary<KeyframeStrip, KeyframeBinding> _keyframeStrips = [];

    private void WireKeyframes()
    {
        Bind(FrameScaleKeys, SelectedClipForKeyframes, "el zoom", AnimatedProperty.Scale);
        Bind(FramePositionKeys, SelectedClipForKeyframes, "la posición",
            AnimatedProperty.OffsetX, AnimatedProperty.OffsetY);
        Bind(FrameRotationKeys, SelectedClipForKeyframes, "la rotación", AnimatedProperty.Rotation);

        Bind(LayerPositionKeys, () => Timeline.SelectedOverlay, "la posición",
            AnimatedProperty.OffsetX, AnimatedProperty.OffsetY);
        Bind(LayerWidthKeys, () => Timeline.SelectedOverlay, "el ancho", AnimatedProperty.Width);
        Bind(LayerOpacityKeys, () => Timeline.SelectedOverlay, "la opacidad", AnimatedProperty.Opacity);

        Bind(VolumeKeys, () => Timeline.SelectedAudio, "el volumen", AnimatedProperty.Volume);
    }

    private void Bind(
        KeyframeStrip strip, Func<IAnimatable?> target, string label, params AnimatedProperty[] properties)
    {
        _keyframeStrips[strip] = new KeyframeBinding(target, properties, label);

        strip.Toggled += (_, _) => ToggleKeyframe(strip);
        strip.Cleared += (_, _) => ClearKeyframes(strip);
        strip.Sought += (_, at) => SeekWithin(strip, at);
    }

    /// <summary>Clip de la pista principal seleccionado, si lo hay y no es un hueco.</summary>
    private IAnimatable? SelectedClipForKeyframes() =>
        Timeline.SelectedClip is { IsGap: false } clip ? clip : null;

    /// <summary>Instante del cabezal dentro del elemento que se anima, o <see langword="null"/> si está fuera.</summary>
    private TimeSpan? LocalPosition(IAnimatable target)
    {
        var playhead = Timeline.Playhead;

        return target switch
        {
            Clip clip => Within(Sequence.StartOf(clip), clip.Duration),
            OverlayItem item => Within(item.Start, item.Duration),
            AudioClip audio => Within(audio.TimelineStart, audio.Duration),
            _ => null,
        };

        TimeSpan? Within(TimeSpan start, TimeSpan duration)
        {
            var local = playhead - start;
            return local >= TimeSpan.Zero && local <= duration ? local : null;
        }
    }

    private void ToggleKeyframe(KeyframeStrip strip)
    {
        if (_keyframeStrips[strip] is not { } binding || binding.Target() is not { } target)
        {
            return;
        }

        if (LocalPosition(target) is not { } at)
        {
            SetStatus($"Mueve el cabezal dentro del clip para poner un punto en {binding.Label}.");
            return;
        }

        // Si ya hay un punto en el cabezal se quita; el rombo es un interruptor.
        var existing = binding.Properties.All(p => target.Animation.Track(p).HasPointAt(at));

        foreach (var property in binding.Properties)
        {
            if (existing)
            {
                Timeline.RemoveKeyframe(target, property, at);
            }
            else
            {
                Timeline.SetKeyframe(target, property, at, CurrentValue(target, property));
            }
        }

        RefreshAfterKeyframeChange();

        SetStatus(existing
            ? $"Punto quitado de {binding.Label}."
            : $"Punto puesto en {binding.Label}. Mueve el cabezal y cambia el valor para que se anime.");
    }

    private void ClearKeyframes(KeyframeStrip strip)
    {
        if (_keyframeStrips[strip] is not { } binding || binding.Target() is not { } target)
        {
            return;
        }

        foreach (var property in binding.Properties)
        {
            Timeline.RemoveKeyframe(target, property, null);
        }

        RefreshAfterKeyframeChange();
        SetStatus($"Animación de {binding.Label} quitada; vuelve a valer el ajuste fijo.");
    }

    /// <summary>Lleva el cabezal a un instante contado desde el inicio del elemento animado.</summary>
    private void SeekWithin(KeyframeStrip strip, TimeSpan at)
    {
        if (_keyframeStrips[strip] is not { } binding || binding.Target() is not { } target)
        {
            return;
        }

        var start = target switch
        {
            Clip clip => Sequence.StartOf(clip),
            OverlayItem item => item.Start,
            AudioClip audio => audio.TimelineStart,
            _ => TimeSpan.Zero,
        };

        SeekTo(start + at);
    }

    /// <summary>Valor que tiene ahora mismo la propiedad, que es el que se fija en el punto nuevo.</summary>
    private static double CurrentValue(IAnimatable target, AnimatedProperty property) => target switch
    {
        Clip clip => property switch
        {
            AnimatedProperty.Scale => clip.Transform.Scale,
            AnimatedProperty.OffsetX => clip.Transform.OffsetX,
            AnimatedProperty.OffsetY => clip.Transform.OffsetY,
            _ => clip.Transform.Rotation,
        },

        OverlayItem item => property switch
        {
            AnimatedProperty.OffsetX => item.Transform.CenterX,
            AnimatedProperty.OffsetY => item.Transform.CenterY,
            AnimatedProperty.Width => item.Transform.Width,
            _ => item.Transform.Opacity,
        },

        AudioClip audio => audio.GainDb,
        _ => 0,
    };

    /// <summary>Pone al día las tiras con lo que hay seleccionado y dónde está el cabezal.</summary>
    private void RefreshKeyframeStrips()
    {
        foreach (var (strip, binding) in _keyframeStrips)
        {
            var target = binding.Target();

            if (target is null)
            {
                strip.IsEnabled = false;
                strip.Show(KeyframeTrack.Empty, TimeSpan.FromSeconds(1), TimeSpan.Zero);
                continue;
            }

            strip.IsEnabled = true;

            // Con dos propiedades a la vez se muestra la primera: van siempre juntas, así que
            // cualquiera de las dos cuenta la misma historia.
            var track = target.Animation.Track(binding.Properties[0]);
            strip.Show(track, target.Duration, LocalPosition(target) ?? TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Rehace lo que depende de una animación: el preview, la copia por tramos y los propios paneles.
    /// </summary>
    private void RefreshAfterKeyframeChange()
    {
        RefreshKeyframeStrips();
        RefreshInspector();

        // El encuadre animado entra en los filtros del decodificador, que solo se releen al
        // abrir: hay que pedir el fotograma otra vez para que el cambio se vea sin tocar nada más.
        SeekTo(Timeline.Playhead, follow: false);
    }
}
