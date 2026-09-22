// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EditFlow.App.Controls;
using EditFlow.Core.Timeline;
using EditFlow.Engine.Thumbnails;
using EditFlow.Engine.Overlays;

namespace EditFlow.App;

// Textos e imágenes superpuestos: su dibujo en el preview y los paneles para editarlos.
public partial class MainWindow
{
    /// <summary>
    /// Alto con el que se dibujan los textos para el preview. Se dibujan a 1080 para que la letra
    /// se vea nítida aunque el preview ocupe casi toda la pantalla; después se reducen al lienzo.
    /// </summary>
    private const int PreviewTextHeight = 1080;

    /// <summary>
    /// Pone sobre el preview lo que se ve en el instante del cabezal, de abajo arriba.
    /// </summary>
    /// <remarks>
    /// Los textos son los mismos PNG que la exportación compone con <c>overlay</c>, dibujados
    /// por el mismo código: lo que se ve aquí es lo que sale exportado, no una aproximación.
    /// </remarks>
    /// <summary>Conecta el arrastre de textos e imágenes directamente sobre el preview.</summary>
    private void WirePreviewDragging()
    {
        Video.OverlayGrabbed += (_, item) => Timeline.SelectOverlayItem(item);

        Video.OverlayDropped += (_, drop) =>
        {
            Timeline.SelectOverlayItem(drop.Item);

            var moved = drop.Item.Transform with { CenterX = drop.CenterX, CenterY = drop.CenterY };
            if (!Timeline.SetSelectedOverlayLook(moved))
            {
                SetStatus("La capa está bloqueada: desbloquéala para moverlo.");
            }
        };

        Timeline.SelectionChanged += (_, _) => Video.SelectedOverlay = Timeline.SelectedOverlay;
    }

    /// <summary>Sube el clip seleccionado a una capa superior.</summary>
    private void LiftSelectedClip() => SetStatus(Timeline.LiftSelectedClip()
        ? "Clip subido a una capa superior. En la pista principal queda un hueco; ya puedes moverlo, reducirlo o recortarlo."
        : "Selecciona un clip de la pista principal para subirlo (divídelo antes con S si solo quieres subir una parte).");

    // -------------------------------------------- videos superpuestos en el preview

    // Un video superpuesto no tiene un segundo reproductor: su fotograma se extrae del archivo en el
    // instante que toca, de uno en uno y siempre el más reciente. Parado se ve nítido al momento;
    // reproduciendo sin copia de preview se mueve a pocos fotogramas por segundo, y con la copia de
    // preview (botón Render) se ve de corrido porque ya viene compuesto.
    private readonly Dictionary<Guid, Avalonia.Media.Imaging.Bitmap> _videoOverlayBitmaps = [];
    private readonly Dictionary<Guid, string> _videoOverlayAsked = [];
    private readonly Queue<Avalonia.Media.Imaging.Bitmap> _retiredBitmaps = new();
    private (string Path, TimeSpan At, Guid Id, string? Filter)? _videoFrameRequest;
    private bool _videoFrameWorker;

    private Avalonia.Media.Imaging.Bitmap? VideoOverlayBitmap(OverlayItem item, TimeSpan position)
    {
        if (item.Media is { } media && _tools is not null)
        {
            var at = item.SourceIn + (position - item.Start);
            var slot = (long)(at.TotalMilliseconds / 40);

            // El color entra en la clave: al ajustarlo hay que pedir el fotograma otra vez aunque no se mueva el cabezal.
            var filter = EditFlow.Engine.Exporting.ColorFilter.Build(item.Color);
            var key = slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + filter;

            if (!_videoOverlayAsked.TryGetValue(item.Id, out var asked) || asked != key)
            {
                _videoOverlayAsked[item.Id] = key;
                _videoFrameRequest = (media.Path, TimeSpan.FromMilliseconds(slot * 40), item.Id, filter);

                if (!_videoFrameWorker)
                {
                    _videoFrameWorker = true;
                    _ = RunVideoFrameWorkerAsync();
                }
            }
        }

        return _videoOverlayBitmaps.GetValueOrDefault(item.Id);
    }

    private async Task RunVideoFrameWorkerAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "editflow-ovframes");

        try
        {
            while (_videoFrameRequest is { } request && _tools is not null)
            {
                _videoFrameRequest = null;
                var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg");

                if (await new FrameExtractor(_tools).ExtractAsync(request.Path, request.At, file, 960, colorFilter: request.Filter))
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);

                        if (_videoOverlayBitmaps.TryGetValue(request.Id, out var old))
                        {
                            // La composición puede tener aún el anterior en su cola: se libera unos
                            // cuantos fotogramas más tarde, no de inmediato.
                            _retiredBitmaps.Enqueue(old);
                            while (_retiredBitmaps.Count > 6)
                            {
                                _retiredBitmaps.Dequeue().Dispose();
                            }
                        }

                        _videoOverlayBitmaps[request.Id] = bitmap;
                        UpdatePreviewOverlays();
                    }
                    catch (IOException)
                    {
                        // Aún se estaba escribiendo: llegará el siguiente.
                    }
                }

                DeleteQuietly(file);
            }
        }
        finally
        {
            _videoFrameWorker = false;
        }
    }

    /// <summary>Rectángulo, en unidades del lienzo, que ocupa un elemento superpuesto.</summary>
    private static Rect OverlayArea(OverlayItem item, Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        var transform = item.Transform;
        double width;
        double height;

        if (item.Kind == OverlayKind.Text && bitmap is not null)
        {
            var unit = VideoSurface.CanvasHeight / PreviewTextHeight;
            width = bitmap.PixelSize.Width * unit;
            height = bitmap.PixelSize.Height * unit;
        }
        else
        {
            width = VideoSurface.CanvasWidth * transform.Width;
            height = width / Math.Max(item.AspectRatio, 0.01);
        }

        return new Rect(
            (transform.CenterX * VideoSurface.CanvasWidth) - (width / 2),
            (transform.CenterY * VideoSurface.CanvasHeight) - (height / 2),
            width,
            height);
    }

    private void UpdatePreviewOverlays()
    {
        // Un tramo renderizado ya lleva los textos e imágenes dibujados: repetirlos encima los
        // vería dobles.
        if (_playingRun is not null)
        {
            Video.SetOverlays([]);
            return;
        }

        var visible = new List<PreviewOverlay>();
        var position = Timeline.Playhead;

        // La primera capa es la de delante: se recorre desde la última para dibujar de abajo arriba.
        for (var t = Edit.OverlayTracks.Count - 1; t >= 0; t--)
        {
            var track = Edit.OverlayTracks[t];
            if (track.IsHidden)
            {
                continue;
            }

            foreach (var item in track.Items)
            {
                if (!item.IsVisibleAt(position))
                {
                    continue;
                }

                Avalonia.Media.Imaging.Bitmap? bitmap;

                if (item.Kind == OverlayKind.Video)
                {
                    // Reproduciendo, el video de la capa se ve en vivo; parado (o mientras arranca), como fotograma suelto.
                    if (_liveLayers.TryGetValue(item.Id, out var live) && live.HasFrame)
                    {
                        visible.Add(new PreviewOverlay(null, OverlayArea(item, null), item.Transform.Opacity, item, live.Source));
                        continue;
                    }

                    bitmap = VideoOverlayBitmap(item, position);
                }
                else
                {
                    var path = item.Kind == OverlayKind.Text && item.Text is not null
                        ? TextRenderCache.Shared.GetPath(item.Text, PreviewTextHeight)
                        : item.ImagePath;

                    // Mientras la imagen se decodifica no se dibuja; al llegar se vuelve a llamar aquí.
                    bitmap = path is null ? null : _frameBitmaps.TryGet(path);
                }

                if (bitmap is null)
                {
                    continue;
                }

                var transform = item.Transform;
                double width, height;

                if (item.Kind == OverlayKind.Text)
                {
                    // La imagen está dibujada a 1080 de alto: se pasa a unidades del lienzo.
                    var unit = VideoSurface.CanvasHeight / PreviewTextHeight;
                    width = bitmap.PixelSize.Width * unit;
                    height = bitmap.PixelSize.Height * unit;
                }
                else
                {
                    width = VideoSurface.CanvasWidth * transform.Width;
                    height = width / Math.Max(item.AspectRatio, 0.01);
                }

                var area = new Rect(
                    (transform.CenterX * VideoSurface.CanvasWidth) - (width / 2),
                    (transform.CenterY * VideoSurface.CanvasHeight) - (height / 2),
                    width,
                    height);

                visible.Add(new PreviewOverlay(bitmap, area, transform.Opacity, item));
            }
        }

        Video.SetOverlays(visible);
    }

    // ------------------------------------------------------------ panel de texto

    private static readonly TimeSpan DefaultOverlayDuration = TimeSpan.FromSeconds(5);

    private static readonly string[] SwatchColors =
    [
        "#FFFFFF", "#FFDD55", "#FF8A3D", "#FF4D6D", "#4DA3FF", "#4DE0A0", "#C084FC", "#101010",
    ];

    [GeneratedRegex("^#?([0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    private void WireLayerPanels()
    {
        AddTitleButton.Click += (_, _) => AddPreset(
            new TextStyle("Título", 0.12, "#FFFFFF", Bold: true, Shadow: true), new OverlayTransform(0.5, 0.5));
        AddSubtitleButton.Click += (_, _) =>
        {
            // El subtítulo va a la capa «Sub», que es única y va siempre delante de las demás.
            var item = Timeline.AddSubtitleText("Subtítulo", DefaultOverlayDuration);
            SetStatus(item is null
                ? "Ya hay un subtítulo en ese instante: mueve el cabezal a un punto libre de la capa «Sub»."
                : "Subtítulo añadido en la capa «Sub». Edítalo en el panel de la derecha.");
        };
        AddPlainTextButton.Click += (_, _) => AddPreset(
            new TextStyle("Texto", 0.07, "#FFFFFF", Bold: false, Shadow: true), new OverlayTransform(0.5, 0.5));
        AddImageButton.Click += async (_, _) => await AddImageAsync();

        // Un elemento recién seleccionado se edita en su panel sin que haya que buscarlo.
        Timeline.SelectionChanged += (_, _) =>
        {
            if (Timeline.SelectedOverlay is not null && _rightTab != RightTab.Layer)
            {
                ToggleRightTab(RightTab.Layer);
            }
        };

        foreach (var color in SwatchColors)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
                BorderBrush = (IBrush)this.FindResource("Line")!,
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(swatch, color);
            swatch.Click += (_, _) =>
            {
                ColorHexBox.Text = color;
                CommitLook();
            };
            ColorSwatches.Children.Add(swatch);
        }

        // Los deslizadores se aplican al soltar: arrastrarlos no debe llenar el historial.
        CommitOnRelease(TextSizeSlider, CommitLook);
        CommitOnRelease(ImageWidthSlider, CommitLook);
        CommitOnRelease(PosXSlider, CommitLook);
        CommitOnRelease(PosYSlider, CommitLook);
        CommitOnRelease(OpacitySlider, CommitLook);

        TextSizeSlider.ValueChanged += (_, _) => TextSizeReadout.Text = Percent(TextSizeSlider.Value);
        ImageWidthSlider.ValueChanged += (_, _) => ImageWidthReadout.Text = Percent(ImageWidthSlider.Value);
        PosXSlider.ValueChanged += (_, _) => PosXReadout.Text = Percent(PosXSlider.Value);
        PosYSlider.ValueChanged += (_, _) => PosYReadout.Text = Percent(PosYSlider.Value);
        OpacitySlider.ValueChanged += (_, _) => OpacityReadout.Text = Percent(OpacitySlider.Value);

        // El texto se aplica al salir del cuadro: cada letra sería una entrada del historial.
        TextContentBox.LostFocus += (_, _) => CommitLook();
        ColorHexBox.LostFocus += (_, _) => CommitLook();

        BoldCheck.IsCheckedChanged += (_, _) => CommitLook();
        ItalicCheck.IsCheckedChanged += (_, _) => CommitLook();
        ShadowCheck.IsCheckedChanged += (_, _) => CommitLook();

        // La primera opción («Predeterminada») representa null: la tipografía del sistema, la
        // misma que se usaba antes de que hubiera nada que elegir.
        FontFamilyCombo.ItemsSource = EditFlow.Engine.Overlays.TextRenderer.AvailableFontFamilies()
            .Prepend("(Predeterminada)")
            .ToArray();
        FontFamilyCombo.SelectedIndex = 0;
        FontFamilyCombo.SelectionChanged += (_, _) => CommitLook();

        StartBox.ValueChanged += (_, _) => CommitPlacement();
        DurationBox.ValueChanged += (_, _) => CommitPlacement();

        // Sonido de un video subido a una capa, y volver a la pista principal.
        VideoGainSlider.ValueChanged += (_, _) => VideoGainReadout.Text = FormatGain(VideoGainSlider.Value);
        CommitOnRelease(VideoGainSlider, CommitVideoAudio);
        VideoMuteCheck.IsCheckedChanged += (_, _) => CommitVideoAudio();

        LowerButton.Click += (_, _) => SetStatus(Timeline.LowerSelectedOverlay()
            ? "Video bajado a la pista principal, en el hueco que había."
            : "Solo se puede bajar donde la pista principal está vacía (un hueco) o después de su final.");
    }

    private void CommitVideoAudio()
    {
        if (_inspectorUpdating || Timeline.SelectedOverlay is not { Kind: OverlayKind.Video })
        {
            return;
        }

        Timeline.SetSelectedOverlayAudio(VideoMuteCheck.IsChecked != true, VideoGainSlider.Value);
    }

    private void AddPreset(TextStyle style, OverlayTransform transform)
    {
        var item = Timeline.AddText(style, DefaultOverlayDuration, transform);
        SetStatus(item is null
            ? "No se pudo añadir el texto."
            : "Texto añadido en el cabezal. Edítalo en el panel de la derecha.");
    }

    private async Task AddImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Superponer imagen",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Imagen") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"] },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        if (ImageProbe.TryRead(path) is not { } size)
        {
            SetStatus($"No se pudo leer «{Path.GetFileName(path)}» como imagen.");
            return;
        }

        var item = Timeline.AddImage(path, (double)size.Width / size.Height, DefaultOverlayDuration);
        SetStatus(item is null
            ? "No se pudo añadir la imagen."
            : "Imagen añadida en el cabezal. Ajústala en el panel de la derecha.");
    }

    // -------------------------------------------------------------- inspector

    private static string Percent(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture) + " %";

    private void RefreshLayerInspector()
    {
        InspectorTitle.Text = "Capa";

        var item = Timeline.SelectedOverlay;
        if (item is null)
        {
            InspectorNothing.Text = "Selecciona un texto o una imagen en la timeline, o añade uno desde la pestaña Texto.";
            return;
        }

        InspectorNothing.IsVisible = false;
        LayerControls.IsVisible = true;
        LayerControls.IsEnabled = Timeline.SelectedOverlayTrack is { IsLocked: false };

        var text = item.Text;
        InspectorTarget.Text = item.Kind switch
        {
            OverlayKind.Text => "Texto",
            OverlayKind.Video => Path.GetFileName(item.Media?.Path),
            _ => Path.GetFileName(item.ImagePath),
        };

        _inspectorUpdating = true;
        try
        {
            TextControls.IsVisible = item.Kind == OverlayKind.Text;
            ImageControls.IsVisible = item.Kind != OverlayKind.Text;

            if (text is not null)
            {
                TextContentBox.Text = text.Content;
                TextSizeSlider.Value = Math.Round(text.Size * 100);
                TextSizeReadout.Text = Percent(TextSizeSlider.Value);
                ColorHexBox.Text = text.Color;
                BoldCheck.IsChecked = text.Bold;
                ItalicCheck.IsChecked = text.Italic;
                ShadowCheck.IsChecked = text.Shadow;

                var fonts = FontFamilyCombo.ItemsSource as string[] ?? [];
                var fontIndex = text.FontFamily is null
                    ? 0
                    : Array.FindIndex(fonts, name => string.Equals(name, text.FontFamily, StringComparison.OrdinalIgnoreCase));
                FontFamilyCombo.SelectedIndex = Math.Max(fontIndex, 0);
            }

            var t = item.Transform;
            ImageWidthSlider.Value = Math.Round(t.Width * 100);
            ImageWidthReadout.Text = Percent(ImageWidthSlider.Value);
            PosXSlider.Value = Math.Round(t.CenterX * 100);
            PosXReadout.Text = Percent(PosXSlider.Value);
            PosYSlider.Value = Math.Round(t.CenterY * 100);
            PosYReadout.Text = Percent(PosYSlider.Value);
            OpacitySlider.Value = Math.Round(t.Opacity * 100);
            OpacityReadout.Text = Percent(OpacitySlider.Value);

            StartBox.Value = (decimal)Math.Round(item.Start.TotalSeconds, 1);
            DurationBox.Value = (decimal)Math.Round(item.Duration.TotalSeconds, 1);

            var isVideo = item.Kind == OverlayKind.Video;
            VideoControls.IsVisible = isVideo;
            if (isVideo)
            {
                var hasAudio = item.Media is { HasAudio: true };
                VideoAudioPanel.IsVisible = hasAudio;
                VideoGainSlider.Value = Math.Clamp(item.AudioGainDb, VideoGainSlider.Minimum, VideoGainSlider.Maximum);
                VideoGainReadout.Text = FormatGain(item.AudioGainDb);
                VideoMuteCheck.IsChecked = !item.PlaysAudio;

                var canLower = Timeline.CanLowerSelectedOverlay();
                LowerButton.IsEnabled = canLower;
                LowerHint.Text = canLower
                    ? "Pasa a ser un clip de la pista principal, ocupando el cuadro entero."
                    : "Para bajarlo, el hueco de la pista principal debe estar libre bajo él.";
            }
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }

    /// <summary>Aplica al elemento seleccionado lo que muestran los controles del panel.</summary>
    private void CommitLook()
    {
        if (_inspectorUpdating || Timeline.SelectedOverlay is not { } item)
        {
            return;
        }

        var transform = new OverlayTransform(
            PosXSlider.Value / 100,
            PosYSlider.Value / 100,
            ImageWidthSlider.Value / 100,
            OpacitySlider.Value / 100).Clamped();

        TextStyle? text = null;
        if (item.Kind == OverlayKind.Text && item.Text is { } current)
        {
            var fonts = FontFamilyCombo.ItemsSource as string[] ?? [];
            var fontFamily = FontFamilyCombo.SelectedIndex > 0 && FontFamilyCombo.SelectedIndex < fonts.Length
                ? fonts[FontFamilyCombo.SelectedIndex]
                : null;

            text = new TextStyle(
                TextContentBox.Text ?? string.Empty,
                TextSizeSlider.Value / 100,
                NormalizeColor(ColorHexBox.Text, current.Color),
                BoldCheck.IsChecked == true,
                ItalicCheck.IsChecked == true,
                ShadowCheck.IsChecked == true,
                fontFamily);
        }

        if (transform == item.Transform && text == item.Text)
        {
            return;
        }

        Timeline.SetSelectedOverlayLook(transform, text);
    }

    private void CommitPlacement()
    {
        if (_inspectorUpdating || Timeline.SelectedOverlay is not { } item)
        {
            return;
        }

        var start = TimeSpan.FromSeconds((double)(StartBox.Value ?? 0));
        var duration = TimeSpan.FromSeconds((double)(DurationBox.Value ?? 1));

        if (start == item.Start && duration == item.Duration)
        {
            return;
        }

        if (!Timeline.SetSelectedOverlayPlacement(start, duration))
        {
            // Chocaba con otro elemento de la capa: se muestra lo que hay en realidad en lugar
            // de dejar en pantalla un valor que no se aplicó.
            SetStatus("No cabe ahí: choca con otro elemento de la misma capa.");
            RefreshLayerInspector();
        }
    }

    /// <summary>Acepta <c>#RRGGBB</c> con o sin almohadilla; con algo que no lo sea, deja el color actual.</summary>
    private static string NormalizeColor(string? typed, string fallback)
    {
        var match = HexColor().Match((typed ?? string.Empty).Trim());
        return match.Success ? "#" + match.Groups[1].Value.ToUpperInvariant() : fallback;
    }
}
