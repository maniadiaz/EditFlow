// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using EditFlow.App.Playback;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Videos en capas, en vivo: mientras se reproduce, cada video superpuesto visible tiene su propio decodificador.
public partial class MainWindow
{
    // Más de dos decodificadores a la vez saturarían un equipo justo; los demás se ven como fotogramas sueltos.
    private const int MaxLiveLayers = 2;

    private readonly Dictionary<Guid, LiveVideoLayer> _liveLayers = [];
    private int _liveRepaintPending;

    /// <summary>
    /// Pone al día los decodificadores de las capas: crea los de los videos que acaban de entrar en pantalla y
    /// cierra los que salieron.
    /// </summary>
    /// <remarks>
    /// Se llama con el seguimiento de la reproducción (cada 120 ms). Un video que entra puede empezar hasta
    /// un cuarto de segundo tarde; el reloj de audio lo alcanza descartando fotogramas.
    /// </remarks>
    private void SyncLiveLayers(TimeSpan position)
    {
        // Un tramo renderizado ya lleva las capas dentro; parado no hace falta decodificar nada.
        if (!_playing || _playingRun is not null || _tools is null)
        {
            StopLiveLayers();
            return;
        }

        var wanted = Edit.OverlayTracks
            .Where(track => !track.IsHidden)
            .SelectMany(track => track.Items)
            .Where(item => item is { Kind: OverlayKind.Video, Media: not null } && item.IsVisibleAt(position))
            .Take(MaxLiveLayers)
            .ToList();

        foreach (var id in _liveLayers.Keys.Except(wanted.Select(w => w.Id)).ToList())
        {
            _liveLayers[id].Dispose();
            _liveLayers.Remove(id);
        }

        foreach (var item in wanted.Where(item => !_liveLayers.ContainsKey(item.Id)))
        {
            // La copia ligera decodifica mucho más deprisa que un 4K, y en una capa pequeña no se nota.
            var path = _proxies?.Resolve(item.Media!.Path) ?? item.Media!.Path;

            Func<TimeSpan>? clock = null;
            if (_mixReady && _audio is { } audio)
            {
                clock = () => audio.Position;
            }

            var layer = new LiveVideoLayer(_tools, item, path, clock, RequestLiveRepaint);
            _liveLayers[item.Id] = layer;
            layer.Start(position);
        }
    }

    /// <summary>Cierra todos los decodificadores de capas.</summary>
    private void StopLiveLayers()
    {
        if (_liveLayers.Count == 0)
        {
            return;
        }

        foreach (var layer in _liveLayers.Values)
        {
            layer.Dispose();
        }

        _liveLayers.Clear();
        UpdatePreviewOverlays();
    }

    // Los fotogramas llegan desde hilos de decodificación y en ráfagas: se agrupan en un solo repintado.
    private void RequestLiveRepaint()
    {
        if (Interlocked.Exchange(ref _liveRepaintPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    Interlocked.Exchange(ref _liveRepaintPending, 0);
                    Video.InvalidateVisual();
                },
                DispatcherPriority.Render);
        }
    }
}
