// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace EditFlow.App.Services;

/// <summary>
/// Imágenes ya decodificadas para dibujar, cargadas sin bloquear el hilo de interfaz.
/// </summary>
/// <remarks>
/// La timeline se repinta decenas de veces por segundo, y cada repintado necesita las
/// miniaturas visibles. Decodificar un JPEG dentro de <c>Render</c> lo haría tartamudear, así
/// que <see cref="TryGet"/> responde al instante con lo que haya en memoria y, si falta algo,
/// lo carga en segundo plano y avisa con <see cref="Loaded"/> para que se repinte.
/// </remarks>
public sealed class FrameBitmaps
{
    // Cada miniatura decodificada ocupa unos 15 KB: 600 son menos de 10 MB. Al pasar del límite
    // se vacía la caché entera, que es más simple que llevar un orden de uso y basta aquí,
    // porque las que se vuelven a necesitar se cargan de nuevo en milisegundos.
    private const int Capacity = 600;

    private readonly ConcurrentDictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _loading = new(StringComparer.OrdinalIgnoreCase);
    private int _notifyPending;

    /// <summary>Se dispara, en el hilo de interfaz, cuando llegan imágenes nuevas.</summary>
    public event Action? Loaded;

    /// <summary>Imagen ya cargada, o <see langword="null"/> mientras se carga.</summary>
    public Bitmap? TryGet(string path)
    {
        if (_cache.TryGetValue(path, out var bitmap))
        {
            return bitmap;
        }

        if (_loading.TryAdd(path, 0))
        {
            _ = Task.Run(() => Load(path));
        }

        return null;
    }

    /// <summary>Olvida las imágenes en memoria; las siguientes consultas las cargarán de nuevo.</summary>
    public void Clear() => _cache.Clear();

    private void Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);

            if (_cache.Count >= Capacity)
            {
                _cache.Clear();
            }

            _cache[path] = bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // La imagen puede estar aún escribiéndose. Se olvida la petición para que un
            // repintado posterior vuelva a intentarlo.
            _loading.TryRemove(path, out _);
            return;
        }

        _loading.TryRemove(path, out _);

        // Varias imágenes que llegan seguidas provocan un único repintado.
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    Interlocked.Exchange(ref _notifyPending, 0);
                    Loaded?.Invoke();
                },
                DispatcherPriority.Background);
        }
    }
}
