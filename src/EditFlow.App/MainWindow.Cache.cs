// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EditFlow.Engine;
using EditFlow.Engine.Playback;
using EditFlow.Engine.PreviewCache;

namespace EditFlow.App;

// Copia de preview: renderizar la timeline por trozos y reproducirla sin decodificar los originales.
public partial class MainWindow
{
    // Un tramo renderizado solo compensa si dura lo bastante: entrar y salir de él cuesta un
    // arranque de decodificador cada vez.
    private static readonly TimeSpan MinimumCachedRun = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheProbeInterval = TimeSpan.FromMilliseconds(400);

    // Altura máxima de la copia con la resolución de reproducción «Completa»: más no se nota en
    // un panel de preview y multiplicaría el espacio y el tiempo de renderizado.
    private const int CacheMaxHeight = 1080;

    private PreviewCacheManager? _previewCache;
    private CacheRun? _playingRun;
    private DateTime _lastCacheProbe;
    private int _cacheUiPending;

    private readonly DispatcherTimer _cacheTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    /// <summary>Conecta los controles de la copia de preview.</summary>
    private void WirePreviewCache()
    {
        _cacheTimer.Tick += (_, _) =>
        {
            _cacheTimer.Stop();
            UpdatePreviewCache();
        };

        CacheButton.Click += (_, _) => ToggleCacheRender();
        CacheClearItem.Click += (_, _) => _previewCache?.Clear();
        CacheButton.IsEnabled = false;
    }

    /// <summary>Crea el gestor de la copia cuando ya hay FFmpeg.</summary>
    private void SetupPreviewCache(FFmpegTools tools)
    {
        var manager = new PreviewCacheManager(tools, PreviewCacheManager.DefaultDirectory);
        _previewCache = manager;

        // Los avisos llegan desde el hilo de renderizado y en ráfagas: se agrupan en un solo repintado.
        manager.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref _cacheUiPending, 1) == 0)
            {
                Dispatcher.UIThread.Post(RefreshCacheUi);
            }
        };

        // Restos de sesiones anteriores que nadie va a volver a usar.
        _ = Task.Run(() => manager.DeleteStale(TimeSpan.FromDays(3)));

        UpdatePreviewCache();
    }

    /// <summary>La timeline cambió: recalcula los trozos un momento después, sin frenar la edición.</summary>
    private void ScheduleCacheUpdate()
    {
        if (_previewCache is null)
        {
            return;
        }

        _cacheTimer.Stop();
        _cacheTimer.Start();
    }

    /// <summary>Con qué tamaño y velocidad se renderiza la copia, según los videos y la resolución de reproducción.</summary>
    private PreviewCacheSettings? CacheSettingsNow()
    {
        if (Sequence.IsEmpty)
        {
            return null;
        }

        var maxHeight = Sequence.Clips.Max(c => c.Source.DisplayHeight);
        var rate = Sequence.Clips.Max(c => c.Source.FrameRate);

        var full = Math.Min(CacheMaxHeight, Math.Max(maxHeight, 240));
        var height = PlaybackResolution.DecodeHeight(_playbackDivisor, maxHeight, full);

        return PreviewCacheSettings.For(height, rate);
    }

    private void UpdatePreviewCache()
    {
        _cacheTimer.Stop();
        _previewCache?.Update(Edit, CacheSettingsNow());
    }

    private void ToggleCacheRender()
    {
        if (_previewCache is null)
        {
            return;
        }

        if (_previewCache.IsRendering)
        {
            _previewCache.CancelRender();
            return;
        }

        UpdatePreviewCache();
        _previewCache.StartRender(Timeline.Playhead);
    }

    private void RefreshCacheUi()
    {
        Interlocked.Exchange(ref _cacheUiPending, 0);

        if (_previewCache is not { } cache)
        {
            return;
        }

        var summary = cache.Summary;
        Timeline.CacheSections = cache.Sections;

        CacheButton.IsEnabled = summary.Total > 0;

        if (summary.IsRendering)
        {
            CacheButton.Content = $"Renderizando {summary.Ready}/{summary.Total}";
        }
        else
        {
            CacheButton.Content = summary.Pending == 0 && summary.Total > 0 ? "Render ✓" : "Render";
        }

        var megabytes = summary.Bytes / (1024.0 * 1024.0);
        Avalonia.Controls.ToolTip.SetTip(
            CacheButton,
            "Copia de preview: renderiza el montaje (con textos e imágenes) a la resolución de reproducción " +
            "para verlo sin tirones, aunque tenga varias capas o venga de un video pesado. " +
            "Es temporal, no toca tus archivos y no afecta a la exportación." + Environment.NewLine + Environment.NewLine +
            "Franja bajo la regla:  verde = renderizado · amarillo = renderizando · rojo = necesita render." +
            Environment.NewLine +
            $"{summary.Ready} de {summary.Total} tramos listos · {megabytes.ToString("0.#", CultureInfo.InvariantCulture)} MB en disco." +
            Environment.NewLine + Environment.NewLine +
            (summary.IsRendering ? "Clic: cancelar el renderizado." : "Clic: renderizar lo que falta.") +
            "  Clic derecho: limpiar copias.");

        if (cache.LastError is { } error)
        {
            SetStatus("No se pudo renderizar una parte del preview: " + error);
        }
    }

    // ------------------------------------------------------------- reproducir desde la copia

    /// <summary>
    /// Si el instante cae en un tramo ya renderizado (y es largo), reproduce desde él.
    /// </summary>
    /// <returns><see langword="true"/> si se cargó el tramo.</returns>
    /// <remarks>
    /// Solo reproduciendo y con la mezcla de audio lista, que es el reloj al que se ata el video.
    /// Con la reproducción parada se sigue usando el original: es lo que permite arrastrar textos
    /// sobre el preview y ver la imagen nítida al detenerse.
    /// </remarks>
    private bool TryPlayFromCache(TimeSpan position)
    {
        if (!_playing || !_mixReady || _previewCache is null || _video is null)
        {
            return false;
        }

        if (_previewCache.FindRun(position) is not { } run)
        {
            return false;
        }

        if (run.End - position < MinimumCachedRun && run.End < Sequence.Duration)
        {
            return false;
        }

        LoadRun(run, position);
        return true;
    }

    private void LoadRun(CacheRun run, TimeSpan position)
    {
        if (_video is null)
        {
            return;
        }

        _playingClip = null;
        _playingRun = run;
        UpdateVideoClock();

        _video.Configure(run.Settings.Width, run.Settings.Height, run.Settings.FrameRate, hardwareDecoding: false);
        _video.Scrub(run.Path, position - run.Start);
        _video.Play();

        UpdatePlaybackInfo(run.Settings.Width, run.Settings.Height);
        UpdatePreviewOverlays();
    }

    /// <summary>Mientras se reproduce un tramo renderizado, comprueba cada poco si hay uno más adelante.</summary>
    private bool ProbeCache(TimeSpan position)
    {
        var now = DateTime.UtcNow;
        if (now - _lastCacheProbe < CacheProbeInterval)
        {
            return false;
        }

        _lastCacheProbe = now;
        return TryPlayFromCache(position);
    }
}
