// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Thumbnails;

namespace EditFlow.App;

// Galerías con miniaturas reales de los paneles Filtros y Efectos: un fotograma del clip
// seleccionado con cada preset aplicado, generado en segundo plano y cacheado por clip y preset,
// para no repetir el trabajo cada vez que se abre el panel o cambia el cabezal.
public partial class MainWindow
{
    private const int PresetThumbnailWidth = 108;

    private readonly Dictionary<string, Bitmap> _presetThumbnails = [];
    private readonly Queue<(string CacheKey, string SourcePath, TimeSpan At, string? Filter)> _presetThumbQueue = new();
    private readonly HashSet<string> _presetThumbQueued = [];
    private bool _presetThumbWorker;
    private Action? _presetThumbReady;

    private sealed record PresetCard(Border Card, Image Image, string Label);

    /// <summary>Construye una galería vacía: una tarjeta por opción, con su nombre y sitio para la miniatura.</summary>
    private List<PresetCard> BuildPresetGallery(WrapPanel host, string[] labels, Action<int> onSelect)
    {
        host.Children.Clear();
        var cards = new List<PresetCard>();

        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;

            var image = new Image
            {
                Width = PresetThumbnailWidth,
                Height = PresetThumbnailWidth * 9 / 16,
                Stretch = Stretch.UniformToFill,
            };

            var imageHost = new Border
            {
                Width = PresetThumbnailWidth,
                Height = PresetThumbnailWidth * 9 / 16,
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.Parse("#17171b")),
                Child = image,
            };

            var name = new TextBlock
            {
                Text = labels[i],
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = (IBrush)this.FindResource("Text")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = PresetThumbnailWidth,
            };

            var card = new Border
            {
                Width = PresetThumbnailWidth,
                Margin = new Thickness(0, 0, 8, 10),
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Children = { imageHost, name } },
            };

            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                {
                    onSelect(index);
                }
            };

            host.Children.Add(card);
            cards.Add(new PresetCard(card, image, labels[i]));
        }

        return cards;
    }

    /// <summary>Marca con el acento la tarjeta seleccionada y quita la marca de las demás.</summary>
    private void HighlightPresetCard(IReadOnlyList<PresetCard> cards, int selectedIndex)
    {
        var accent = (IBrush)this.FindResource("Accent")!;
        for (var i = 0; i < cards.Count; i++)
        {
            cards[i].Card.BorderBrush = i == selectedIndex ? accent : Brushes.Transparent;
        }
    }

    /// <summary>Filtra la galería por texto: oculta las tarjetas cuyo nombre no lo contiene.</summary>
    private static void FilterPresetGallery(IReadOnlyList<PresetCard> cards, string? search)
    {
        var text = search?.Trim() ?? string.Empty;
        foreach (var card in cards)
        {
            card.Card.IsVisible = text.Length == 0
                || card.Label.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Pone en cada tarjeta la miniatura ya generada para este clip, si existe en caché, y pide
    /// las que falten en segundo plano. 'onReady' se llama cada vez que llega una nueva.
    /// </summary>
    private void ApplyPresetThumbnails(
        IReadOnlyList<PresetCard> cards, string?[] fragments, Clip clip, Action onReady)
    {
        if (_tools is null || cards.Count != fragments.Length)
        {
            return;
        }

        _presetThumbReady = onReady;

        // Un fotograma de la mitad del clip suele representar mejor el contenido que el
        // primero, que a veces es negro o un rótulo de apertura.
        var at = clip.SourceIn + TimeSpan.FromTicks((clip.SourceOut - clip.SourceIn).Ticks / 2);
        var path = clip.Source.Path;

        for (var i = 0; i < cards.Count; i++)
        {
            var fragment = fragments[i];
            var cacheKey = $"{path.ToLowerInvariant()}|{at.Ticks}|{fragment ?? "(none)"}";

            if (_presetThumbnails.TryGetValue(cacheKey, out var ready))
            {
                cards[i].Image.Source = ready;
                continue;
            }

            if (_presetThumbQueued.Add(cacheKey))
            {
                _presetThumbQueue.Enqueue((cacheKey, path, at, fragment));
            }
        }

        if (!_presetThumbWorker)
        {
            _presetThumbWorker = true;
            _ = RunPresetThumbWorkerAsync();
        }
    }

    private async Task RunPresetThumbWorkerAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "editflow-presetthumbs");

        try
        {
            while (_presetThumbQueue.Count > 0 && _tools is not null)
            {
                var (cacheKey, sourcePath, at, filter) = _presetThumbQueue.Dequeue();
                _presetThumbQueued.Remove(cacheKey);
                var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg");

                if (await new FrameExtractor(_tools).ExtractAsync(
                        sourcePath, at, file, PresetThumbnailWidth * 2, colorFilter: filter))
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        _presetThumbnails[cacheKey] = new Bitmap(stream);
                        _presetThumbReady?.Invoke();
                    }
                    catch (IOException)
                    {
                        // Aún se estaba escribiendo: se pierde esta miniatura, no es grave.
                    }
                }

                DeleteQuietly(file);
            }
        }
        finally
        {
            _presetThumbWorker = false;
        }
    }
}
