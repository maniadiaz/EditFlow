// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EditFlow.Core.Media;
using EditFlow.Core.Projects;

namespace EditFlow.App;

// Gestión del proyecto: carpetas, etiquetas de color, reconectar archivos y sustituir material.
public partial class MainWindow
{
    /// <summary>Colores de cada etiqueta, en el orden del menú.</summary>
    private static readonly (MediaLabel Label, string Name, string Color)[] LabelColors =
    [
        (MediaLabel.None, "Sin etiqueta", "#00000000"),
        (MediaLabel.Red, "Rojo", "#e0544e"),
        (MediaLabel.Orange, "Naranja", "#e08a3c"),
        (MediaLabel.Yellow, "Amarillo", "#d9c04a"),
        (MediaLabel.Green, "Verde", "#4fa96a"),
        (MediaLabel.Blue, "Azul", "#4a82d9"),
        (MediaLabel.Purple, "Morado", "#9166c9"),
    ];

    /// <summary>Carpeta que se está mirando; la raíz muestra todo el proyecto.</summary>
    private MediaBin? _currentBin;

    /// <summary>Qué hacer con el nombre que se escriba en el cuadro de la carpeta.</summary>
    private Action<string>? _pendingBinName;

    private MediaLibrary Library => _session.Current.Library;

    private MediaBin CurrentBin => _currentBin ?? Library.Root;

    private void WireMediaPool()
    {
        NewBinButton.Click += (_, _) => AskBinName(
            CurrentBin.IsRoot ? "Nueva carpeta" : "Dentro de " + CurrentBin.Name,
            name =>
            {
                var command = new CreateBinCommand(Library, name, CurrentBin);
                Timeline.Apply(command);
                _currentBin = command.Result;
                RefreshMediaPool();
                SetStatus($"Carpeta «{name}» creada.");
            });

        RenameBinButton.Click += (_, _) =>
        {
            if (CurrentBin.IsRoot)
            {
                SetStatus("La carpeta raíz no se puede renombrar.");
                return;
            }

            AskBinName(CurrentBin.Name, name =>
            {
                Timeline.Apply(new RenameBinCommand(CurrentBin, name));
                RefreshMediaPool();
            });
        };

        DeleteBinButton.Click += (_, _) =>
        {
            if (CurrentBin.IsRoot)
            {
                SetStatus("La carpeta raíz no se puede borrar.");
                return;
            }

            var bin = CurrentBin;
            var parent = bin.Parent ?? Library.Root;

            Timeline.Apply(new RemoveBinCommand(Library, bin));
            _currentBin = parent;
            RefreshMediaPool();
            SetStatus($"Carpeta «{bin.Name}» borrada. Lo que había dentro subió un nivel.");
        };

        BinNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                ConfirmBinName();
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                CancelBinName();
                e.Handled = true;
            }
        };

        BinNameBox.LostFocus += (_, _) => CancelBinName();

        RelinkAllButton.Click += async (_, _) => await RelinkAllAsync();
    }

    // ------------------------------------------------------------- carpetas

    private void AskBinName(string suggestion, Action<string> apply)
    {
        _pendingBinName = apply;
        BinNameBox.Text = suggestion;
        BinNameBox.IsVisible = true;
        BinNameBox.Focus();
        BinNameBox.SelectAll();
    }

    private void ConfirmBinName()
    {
        var apply = _pendingBinName;
        var name = BinNameBox.Text?.Trim();

        CancelBinName();

        if (apply is not null && !string.IsNullOrWhiteSpace(name))
        {
            apply(name);
        }
    }

    private void CancelBinName()
    {
        _pendingBinName = null;
        BinNameBox.IsVisible = false;
    }

    /// <summary>Dibuja el árbol de carpetas y marca la que se está mirando.</summary>
    private void RefreshBinList()
    {
        BinList.Children.Clear();

        foreach (var bin in Library.AllBins())
        {
            var depth = 0;
            for (var parent = bin.Parent; parent is not null; parent = parent.Parent)
            {
                depth++;
            }

            var count = _session.Current.Media.Count(m => IsInside(m, bin));
            var selected = ReferenceEquals(bin, CurrentBin);

            var row = new Button
            {
                Classes = { "quiet" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6 + (depth * 12), 3, 6, 3),
                FontSize = 12,
                Content = new TextBlock
                {
                    Text = bin.Name + (count > 0 ? $"  ({count})" : string.Empty),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = (IBrush)this.FindResource(selected ? "Text" : "TextMuted")!,
                },
            };

            if (selected)
            {
                row.Background = new SolidColorBrush(Color.Parse("#2a2a32"));
            }

            var target = bin;
            row.Click += (_, _) =>
            {
                _currentBin = target;
                RefreshMediaPool();
            };

            BinList.Children.Add(row);
        }
    }

    /// <summary>
    /// Indica si un medio se ve dentro de una carpeta, contando lo que hay en sus subcarpetas.
    /// </summary>
    /// <remarks>
    /// Una carpeta que solo enseñara lo suyo obligaría a recorrer el árbol entero para encontrar
    /// algo. Mirando la raíz se ve todo el proyecto, que es lo que se espera de ella.
    /// </remarks>
    private bool IsInside(MediaInfo media, MediaBin bin) => bin.Contains(Library.BinOf(media));

    // ------------------------------------------------------- menú de la tarjeta

    /// <summary>Menú contextual de un medio: etiqueta, carpeta, reconectar y sustituir.</summary>
    private ContextMenu BuildMediaMenu(MediaInfo media)
    {
        var menu = new ContextMenu();

        var label = new MenuItem { Header = "Etiqueta" };
        foreach (var (value, name, color) in LabelColors)
        {
            var current = Library.LabelOf(media) == value;

            var item = new MenuItem
            {
                Header = name,
                Icon = value == MediaLabel.None
                    ? null
                    : new Border
                    {
                        Width = 12,
                        Height = 12,
                        CornerRadius = new CornerRadius(6),
                        Background = new SolidColorBrush(Color.Parse(color)),
                    },
                FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
            };

            var chosen = value;
            item.Click += (_, _) =>
            {
                Timeline.Apply(new SetMediaLabelCommand(Library, media, chosen));
                RefreshMediaPool();
            };

            label.Items.Add(item);
        }

        menu.Items.Add(label);

        var move = new MenuItem { Header = "Mover a" };
        foreach (var bin in Library.AllBins())
        {
            var item = new MenuItem
            {
                Header = bin.IsRoot ? "Todo (raíz)" : bin.DisplayPath,
                FontWeight = ReferenceEquals(Library.BinOf(media), bin) ? FontWeight.SemiBold : FontWeight.Normal,
            };

            var target = bin;
            item.Click += (_, _) =>
            {
                Timeline.Apply(new MoveMediaCommand(Library, media, target));
                RefreshMediaPool();
                SetStatus($"«{Path.GetFileName(media.Path)}» movido a {target.Name}.");
            };

            move.Items.Add(item);
        }

        menu.Items.Add(move);
        menu.Items.Add(new Separator());

        var relink = new MenuItem
        {
            Header = media.IsOffline ? "Reconectar el archivo…" : "Sustituir el material…",
        };

        relink.Click += async (_, _) => await ReplaceMediaAsync(media);
        menu.Items.Add(relink);

        return menu;
    }

    // ------------------------------------------- reconectar y sustituir

    /// <summary>Pide un archivo y lo pone en el lugar de un medio, en todo el montaje.</summary>
    private async Task ReplaceMediaAsync(MediaInfo media)
    {
        var picked = await PickMediaFileAsync(media.IsOffline
            ? "Buscar «" + Path.GetFileName(media.Path) + "»"
            : "Sustituir «" + Path.GetFileName(media.Path) + "»");

        if (picked is null)
        {
            return;
        }

        await ApplyReplacementAsync(media, picked);
    }

    /// <summary>
    /// Reconecta todos los archivos ausentes a partir de uno.
    /// </summary>
    /// <remarks>
    /// Casi siempre lo que se movió fue la carpeta entera, no un archivo suelto: en cuanto se
    /// encuentra uno, los demás están al lado. Buscarlos de uno en uno sería pedirle al usuario
    /// que repita veinte veces lo mismo.
    /// </remarks>
    private async Task RelinkAllAsync()
    {
        var offline = _session.Current.OfflineMedia.ToList();
        if (offline.Count == 0)
        {
            return;
        }

        var picked = await PickMediaFileAsync("Buscar «" + Path.GetFileName(offline[0].Path) + "»");
        if (picked is null)
        {
            return;
        }

        var reconnected = await ApplyReplacementAsync(offline[0], picked) ? 1 : 0;

        // Con la carpeta en la mano, el resto se buscan por nombre sin volver a preguntar.
        var folder = Path.GetDirectoryName(picked);
        if (folder is not null)
        {
            foreach (var missing in _session.Current.OfflineMedia.ToList())
            {
                var candidate = Path.Combine(folder, Path.GetFileName(missing.Path));
                if (File.Exists(candidate) && await ApplyReplacementAsync(missing, candidate, quiet: true))
                {
                    reconnected++;
                }
            }
        }

        var left = _session.Current.OfflineMedia.Count();
        SetStatus(left == 0
            ? $"{reconnected} archivo(s) reconectado(s). Ya no falta ninguno."
            : $"{reconnected} archivo(s) reconectado(s); quedan {left} por encontrar.");

        RefreshMediaPool();
    }

    private async Task<string?> PickMediaFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video o audio")
                {
                    Patterns =
                    [
                        "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v",
                        "*.wav", "*.mp3", "*.aac", "*.flac", "*.m4a", "*.ogg",
                    ],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Lee el archivo nuevo y cambia el medio por él en todo el proyecto.</summary>
    private async Task<bool> ApplyReplacementAsync(MediaInfo media, string path, bool quiet = false)
    {
        if (_tools is null)
        {
            return false;
        }

        MediaInfo probed;
        try
        {
            probed = await new EditFlow.Engine.Probing.FFprobeService(_tools)
                .ProbeAsync(path, System.Threading.CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            SetStatus($"No se pudo leer «{Path.GetFileName(path)}»: {ex.Message}");
            return false;
        }

        var command = new ReplaceMediaCommand(_session.Current, media, probed);
        Timeline.Apply(command);

        RefreshMediaPool();
        SeekTo(Timeline.Playhead, follow: false);

        if (!quiet)
        {
            var what = media.IsOffline ? "reconectado" : "sustituido";
            SetStatus(command.Trimmed
                ? $"Archivo {what}, pero es más corto: se acortó lo que no cabía. Ctrl+Z lo deshace."
                : $"Archivo {what} en {command.AffectedCount} sitio(s) del montaje.");
        }

        return true;
    }

    // ---------------------------------------------------------------- refresco

    /// <summary>Pone al día el árbol, el aviso de ausentes y la rejilla.</summary>
    private void RefreshMediaPool()
    {
        RefreshBinList();
        RefreshOfflineBanner();
        RebuildMediaGrid();
    }

    private void RefreshOfflineBanner()
    {
        var offline = _session.Current.OfflineMedia.ToList();
        OfflineBanner.IsVisible = offline.Count > 0;

        if (offline.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", offline.Take(3).Select(m => Path.GetFileName(m.Path)));
        var more = offline.Count > 3 ? $" y {offline.Count - 3} más" : string.Empty;

        OfflineBannerText.Text =
            $"Faltan {offline.Count} archivo(s): {names}{more}. El montaje está intacto; "
            + "reconéctalos y vuelve la imagen. Hasta entonces no se puede exportar.";
    }
}
