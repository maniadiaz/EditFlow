// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Playback;

namespace EditFlow.App;

// Calidad del preview: a qué resolución y velocidad se decodifica, con qué archivo y con qué
// decodificador.
public partial class MainWindow
{
    private bool _frameLoop;

    /// <summary>
    /// Mueve el cabezal y el reloj a cada fotograma de pantalla mientras se reproduce.
    /// </summary>
    /// <remarks>
    /// El seguimiento de la reproducción (cambiar de clip, pasar página, buscar tramos renderizados)
    /// va cada 120 ms, y con el cabezal atado a él avanzaba a saltos de ocho veces por segundo. Aquí
    /// solo se lee el reloj de audio, ya interpolado, y se coloca: es barato y sigue el ritmo del monitor.
    /// </remarks>
    private void StartFrameLoop()
    {
        if (_frameLoop)
        {
            return;
        }

        _frameLoop = true;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.RequestAnimationFrame(OnAnimationFrame);
        }
        else
        {
            _frameLoop = false;
        }
    }

    private bool _audioStartPending;
    private long _audioWaitFrames;
    private long _audioWaitSince;

    /// <summary>Arranca el sonido cuando el video ya dio su primer fotograma (o pasado un tiempo máximo de espera).</summary>
    private void ReleaseAudioWhenVideoReady()
    {
        if (!_audioStartPending)
        {
            return;
        }

        var ready = _video is null
            || _video.FramesDelivered > _audioWaitFrames
            || Environment.TickCount64 - _audioWaitSince > 800;

        if (ready)
        {
            _audioStartPending = false;
            _audio?.Play();
        }
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        if (!_playing)
        {
            _frameLoop = false;
            return;
        }

        ReleaseAudioWhenVideoReady();

        if (_mixReady && _audio is { HasEnded: false } audio)
        {
            var position = audio.Position;
            if (position < Edit.Duration)
            {
                Timeline.Playhead = position;
                SetPositionText(position);
            }
        }

        RequestFrame();
    }

    private const int ScrubBurstMilliseconds = 180;
    private long _lastScrubTick;

    private readonly UserSettingsStore _userSettings = new(UserSettingsStore.DefaultPath);
    private int _playbackDivisor = 1;

    /// <summary>Rellena el selector de resolución de reproducción y recuerda lo que se elija.</summary>
    private void WirePlaybackResolution()
    {
        foreach (var divisor in PlaybackResolution.Divisors)
        {
            PlaybackBox.Items.Add(PlaybackResolution.Label(divisor));
        }

        _playbackDivisor = _userSettings.Load().PlaybackDivisor;
        PlaybackBox.SelectedIndex = Math.Max(PlaybackResolution.Divisors.ToList().IndexOf(_playbackDivisor), 0);

        PlaybackBox.SelectionChanged += (_, _) =>
        {
            var chosen = PlaybackResolution.Divisors[Math.Max(PlaybackBox.SelectedIndex, 0)];
            if (chosen == _playbackDivisor)
            {
                return;
            }

            _playbackDivisor = chosen;
            _userSettings.Save(new UserSettings(chosen));

            // La copia de preview se renderiza a esta resolución: sus tramos dejan de valer y
            // no debe seguir reproduciéndose una de otra calidad.
            UpdatePreviewCache();

            // Vuelve a mostrar el fotograma actual con el tamaño nuevo; si se está reproduciendo,
            // la reproducción sigue desde donde iba.
            ShowFrameAt(Timeline.Playhead);
        };

        // El teclado no debe cambiar la resolución sin querer al pulsar flechas o espacio.
        PlaybackBox.DropDownClosed += (_, _) => Timeline.Focus();
    }

    private void UpdatePlaybackInfo(int width, int height) =>
        PlaybackInfo.Text = "vista " + width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture);

    /// <summary>Ruta que se muestra: el original para ver bien, la copia ligera para saltar rápido.</summary>
    private string DisplayPath(Clip clip, bool sharp) =>
        sharp ? clip.Source.Path : _proxies?.Resolve(clip.Source.Path) ?? clip.Source.Path;

    /// <summary>Pide al reproductor mostrar un clip en una posición, con la calidad que toca ahora.</summary>
    /// <remarks>
    /// Reproduciendo, siempre el original. Con la reproducción parada se usa la copia ligera, que
    /// salta en decenas de milisegundos, y cuando el cabezal deja de moverse se pasa al original
    /// para que la imagen fija se vea nítida: la calidad completa solo importa cuando se mira.
    /// </remarks>
    private void ShowClipAt(Clip clip, TimeSpan offset)
    {
        if (_video is null)
        {
            return;
        }

        // La copia ligera solo se usa mientras se arrastra el cabezal, que es cuando hay que saltar
        // muy rápido. Un salto suelto (un clic en la regla, una flecha, al pausar) carga directamente
        // el original: si no, la imagen aparecía borrosa un instante y luego «se acomodaba».
        var now = Environment.TickCount64;
        var dragging = now - _lastScrubTick < ScrubBurstMilliseconds;
        _lastScrubTick = now;

        var sharp = _playing || !dragging;
        var path = DisplayPath(clip, sharp);

        ConfigureVideo(clip, path);
        _video.Scrub(path, offset);

        if (!_playing)
        {
            _refineTimer.Stop();
            _refineTimer.Start();
        }
    }

    /// <summary>Cuando el cabezal se detiene, sustituye la imagen de la copia ligera por la del original.</summary>
    private void RefineFrame()
    {
        _refineTimer.Stop();

        if (_playing || _video is null || _playingClip is not { IsGap: false } clip)
        {
            return;
        }

        var original = clip.Source.Path;
        if (string.Equals(_video.CurrentPath, original, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var located = Sequence.ClipAt(Timeline.Playhead);
        if (located is null || !ReferenceEquals(located.Value.Clip, clip))
        {
            return;
        }

        ConfigureVideo(clip, original);
        _video.Scrub(original, clip.SourceIn + clip.SourceTimeAt(located.Value.Offset));
    }

    /// <summary>
    /// Elige resolución, velocidad de fotogramas y decodificador del reproductor para un clip.
    /// </summary>
    private void ConfigureVideo(Clip clip, string path)
    {
        if (_video is null)
        {
            return;
        }

        var isOriginal = string.Equals(path, clip.Source.Path, StringComparison.OrdinalIgnoreCase);

        // Lo que ocupa el preview en píxeles reales de pantalla: en un monitor con escalado del
        // 150 % un panel de 900 unidades son 1350 píxeles.
        var onScreen = Math.Min(Video.Bounds.Height, Video.Bounds.Width * 9 / 16) * RenderScaling;
        var wanted = onScreen > 1 ? PreviewHeights.FirstOrDefault(h => h >= onScreen, PreviewHeights[^1]) : 720;

        // Decodificar por encima de lo que tiene el video no añade nada.
        var available = Math.Max(clip.Source.DisplayHeight, 240);
        var cap = PreviewHeights.FirstOrDefault(h => h >= available, PreviewHeights[^1]);
        var full = Math.Min(wanted, cap);

        // La resolución de reproducción elegida es una fracción del original que nunca supera
        // lo anterior: bajarla no puede costar más que dejarla completa.
        var height = PlaybackResolution.DecodeHeight(_playbackDivisor, clip.Source.DisplayHeight, full);

        // La copia ligera es de 480p: pedir más solo la estiraría.
        if (!isOriginal)
        {
            height = Math.Min(height, 480);
        }

        var width = PlaybackResolution.WidthFor(height);
        UpdatePlaybackInfo(width, height);

        // La velocidad del propio video, hasta 60: forzar 30 tiraba la mitad de los fotogramas
        // de un video a 60 y la imagen no se veía fluida.
        var rate = clip.Source.FrameRate > 1 ? Math.Clamp(clip.Source.FrameRate, 24, 60) : 30;

        // Con la tarjeta solo compensa en videos grandes: arrancar el decodificador de hardware
        // tarda algo, y para un 480p el de software es igual de rápido.
        var hardware = _hardwareDecoding && isOriginal
            && (long)clip.Source.Width * clip.Source.Height >= 1_900_000;

        _video.Configure(width, height, rate, hardware);
        _video.ColorFilter = EditFlow.Engine.Exporting.ColorFilter.Build(clip.Color);
        _video.TransformFilter = EditFlow.Engine.Exporting.TransformFilter.Build(clip.Transform, width, height);
    }
}
