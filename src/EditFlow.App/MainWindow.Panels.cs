// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EditFlow.App.Services;
using EditFlow.Core.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Paneles del editor: columnas de pestañas, panel de medios, propiedades del clip
// seleccionado y controles de la timeline. Vive en su propio archivo porque la ventana
// principal ya concentra la reproducción, la importación y el ciclo del proyecto.
public partial class MainWindow
{
    private const double MediaCardWidth = 126;
    private const double MediaCardHeight = 71;
    private const double ZoomStep = 1.35;

    private enum LeftTab { Media, Text, Transitions }

    private enum RightTab { None, Layer, Audio, Filters, Effects, Color, Speed }

    private MediaThumbnails? _thumbnails;
    private MediaInfo? _selectedMedia;
    private readonly System.Collections.Generic.Dictionary<MediaInfo, Border> _mediaThumbs = [];
    private RightTab _rightTab = RightTab.None;
    private bool _inspectorUpdating;

    /// <summary>Conecta las columnas de pestañas, el panel de propiedades y las barras de herramientas.</summary>
    private void WireEditorChrome()
    {
        RailMedia.Click += (_, _) => ShowLeftTab(LeftTab.Media);
        RailText.Click += (_, _) => ShowLeftTab(LeftTab.Text);
        RailTransitions.Click += (_, _) => ShowLeftTab(LeftTab.Transitions);

        RailLayer.Click += (_, _) => ToggleRightTab(RightTab.Layer);
        RailAudio.Click += (_, _) => ToggleRightTab(RightTab.Audio);
        RailFilters.Click += (_, _) => ToggleRightTab(RightTab.Filters);
        RailEffects.Click += (_, _) => ToggleRightTab(RightTab.Effects);
        RailColor.Click += (_, _) => ToggleRightTab(RightTab.Color);
        RailSpeed.Click += (_, _) => ToggleRightTab(RightTab.Speed);

        UndoButton.Click += (_, _) =>
            SetStatus(Timeline.Undo() ? "Deshecho." : "No hay nada que deshacer.");
        RedoButton.Click += (_, _) =>
            SetStatus(Timeline.Redo() ? "Rehecho." : "No hay nada que rehacer.");
        _history.Changed += (_, _) => RefreshUndoButtons();
        RefreshUndoButtons();

        SplitButton.Click += (_, _) => SetStatus(Timeline.SplitAtPlayhead()
            ? "Clip dividido."
            : "No hay nada que dividir en esta posición.");
        DeleteButton.Click += (_, _) => SetStatus(Timeline.DeleteSelected()
            ? "Eliminado."
            : "Selecciona un clip para eliminarlo.");

        ZoomInButton.Click += (_, _) => Timeline.PixelsPerSecond *= ZoomStep;
        ZoomOutButton.Click += (_, _) => Timeline.PixelsPerSecond /= ZoomStep;
        ZoomFitButton.Click += (_, _) => FitTimeline();

        WireInspector();
        WireLayerPanels();
        ShowLeftTab(LeftTab.Media);
    }

    private void RefreshUndoButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
    }

    private void FitTimeline()
    {
        var seconds = Math.Max(Edit.Duration.TotalSeconds, 1);

        // Se descuentan la cabecera de las pistas y un margen a la derecha.
        var room = TimelineScroll.Bounds.Width - 124 - 48;
        if (room > 0)
        {
            Timeline.PixelsPerSecond = room / seconds;
        }
    }

    // ---------------------------------------------------------------- pestañas

    private void ShowLeftTab(LeftTab tab)
    {
        RailMedia.Classes.Set("selected", tab == LeftTab.Media);
        RailText.Classes.Set("selected", tab == LeftTab.Text);
        RailTransitions.Classes.Set("selected", tab == LeftTab.Transitions);

        MediaPanel.IsVisible = tab == LeftTab.Media;
        TextPanel.IsVisible = tab == LeftTab.Text;
        LeftSoonPanel.IsVisible = tab == LeftTab.Transitions;

        switch (tab)
        {
            case LeftTab.Transitions:
                LeftSoonTitle.Text = "Transiciones";
                LeftSoonText.Text = "Las transiciones entre clips llegarán en una próxima versión.";
                break;
        }
    }

    private void ToggleRightTab(RightTab tab)
    {
        // Pulsar la pestaña abierta cierra el panel, que devuelve el espacio al preview.
        _rightTab = _rightTab == tab ? RightTab.None : tab;

        RailLayer.Classes.Set("selected", _rightTab == RightTab.Layer);
        RailAudio.Classes.Set("selected", _rightTab == RightTab.Audio);
        RailFilters.Classes.Set("selected", _rightTab == RightTab.Filters);
        RailEffects.Classes.Set("selected", _rightTab == RightTab.Effects);
        RailColor.Classes.Set("selected", _rightTab == RightTab.Color);
        RailSpeed.Classes.Set("selected", _rightTab == RightTab.Speed);

        InspectorPanel.IsVisible = _rightTab != RightTab.None;
        RefreshInspector();
    }

    // ---------------------------------------------------------- panel de medios

    private void RebuildMediaGrid()
    {
        MediaGrid.Children.Clear();
        _mediaThumbs.Clear();

        var media = _session.Current.Media;
        MediaEmptyText.IsVisible = media.Count == 0;

        foreach (var item in media)
        {
            MediaGrid.Children.Add(BuildMediaCard(item));
        }
    }

    private Border BuildMediaCard(MediaInfo media)
    {
        var isAudio = media.Width <= 0;

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = (Geometry)this.FindResource(isAudio ? "IconMusic" : "IconFilm")!,
            Fill = (IBrush)this.FindResource("TextFaint")!,
            Stretch = Stretch.Uniform,
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var image = new Image { Stretch = Stretch.UniformToFill };

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B3000000")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = FormatTime(media.Duration),
                FontSize = 10.5,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            },
        };

        var add = new Button
        {
            Classes = { "primary" },
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)this.FindResource("IconPlus")!,
                Fill = Brushes.White,
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
            },
        };
        ToolTip.SetTip(add, isAudio ? "Añadir a la timeline en el cabezal" : "Añadir al final de la timeline");
        add.Click += (_, e) =>
        {
            AddMediaToTimeline(media);
            e.Handled = true;
        };

        var thumb = new Border
        {
            Width = MediaCardWidth,
            Height = MediaCardHeight,
            Background = new SolidColorBrush(Color.Parse("#17171b")),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new Grid { Children = { icon, image, badge, add } },
        };

        var name = new TextBlock
        {
            Text = Path.GetFileName(media.Path),
            FontSize = 11.5,
            Foreground = (IBrush)this.FindResource("Text")!,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 6, 2, 0),
        };

        var card = new Border
        {
            Width = MediaCardWidth,
            Margin = new Thickness(0, 0, 8, 12),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = new StackPanel { Children = { thumb, name } },
        };

        ToolTip.SetTip(card, media.Path);

        // Un clic selecciona y enseña los datos; dos clics añaden a la timeline.
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
            {
                return;
            }

            SelectMedia(media);

            if (e.ClickCount == 2)
            {
                AddMediaToTimeline(media);
            }
        };

        card.PointerEntered += (_, _) => add.IsVisible = true;
        card.PointerExited += (_, _) => add.IsVisible = false;

        _mediaThumbs[media] = thumb;
        HighlightSelectedMedia();

        _ = LoadThumbnailAsync(media, image);
        return card;
    }

    private async Task LoadThumbnailAsync(MediaInfo media, Image target)
    {
        if (_thumbnails is null)
        {
            return;
        }

        var path = await _thumbnails.GetAsync(media);
        if (path is null)
        {
            return;
        }

        // Decodificar en el hilo de interfaz es aceptable: son JPEG de 320 px.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                target.Source = Bitmap.DecodeToWidth(stream, (int)MediaCardWidth * 2);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
                // Sin miniatura se queda el icono.
            }
        });
    }

    private void SelectMedia(MediaInfo media)
    {
        _selectedMedia = media;
        ShowMediaInfo(media);
        HighlightSelectedMedia();
    }

    // Se marca sin reconstruir la lista: rehacer todas las tarjetas en cada clic haría
    // parpadear las miniaturas y perdería el segundo clic de un doble clic.
    private void HighlightSelectedMedia()
    {
        var accent = (IBrush)this.FindResource("Accent")!;

        foreach (var (media, thumb) in _mediaThumbs)
        {
            var selected = ReferenceEquals(media, _selectedMedia);
            thumb.BorderBrush = selected ? accent : null;
            thumb.BorderThickness = new Thickness(selected ? 2 : 0);
        }
    }

    /// <summary>Añade un medio de la biblioteca a la timeline.</summary>
    private void AddMediaToTimeline(MediaInfo media)
    {
        if (media.Width <= 0)
        {
            PlaceAudio(media);
            SetStatus("Audio añadido en la posición del cabezal.");
        }
        else
        {
            _history.Do(new AppendClipCommand(Sequence, new Clip(media)));
            SetStatus("Video añadido al final de la timeline.");
        }

        OnTimelineEdited();
    }

    /// <summary>Coloca un audio en la primera pista con hueco en el cabezal, creando una si no la hay.</summary>
    private void PlaceAudio(MediaInfo media)
    {
        var start = Timeline.Playhead;
        var track = Edit.AudioTracks.FirstOrDefault(t => !t.IsLocked && t.CanPlace(start, media.Duration));

        if (track is null)
        {
            // Sin hueco en ninguna pista se crea otra. Son dos pasos en el historial, lo
            // que permite deshacer solo el clip y conservar la pista.
            var create = new AddAudioTrackCommand(Edit);
            _history.Do(create);
            track = create.Result!;
        }

        _history.Do(new AddAudioClipCommand(
            track, new AudioClip(media, TimeSpan.Zero, media.Duration, start)));
    }

    // -------------------------------------------------------------- propiedades

    private void WireInspector()
    {
        GainSlider.ValueChanged += (_, _) =>
        {
            GainReadout.Text = FormatGain(GainSlider.Value);
        };
        FadeInSlider.ValueChanged += (_, _) => FadeInReadout.Text = FormatSeconds(FadeInSlider.Value);
        FadeOutSlider.ValueChanged += (_, _) => FadeOutReadout.Text = FormatSeconds(FadeOutSlider.Value);

        // Se aplica al soltar, no en cada movimiento: arrastrar un deslizador generaría
        // decenas de entradas en el historial, y deshacer daría un paso minúsculo cada vez.
        CommitOnRelease(GainSlider, CommitGain);
        CommitOnRelease(FadeInSlider, CommitFades);
        CommitOnRelease(FadeOutSlider, CommitFades);

        GainResetButton.Click += (_, _) => Timeline.SetSelectedGain(0);

        MuteCheck.IsCheckedChanged += (_, _) =>
        {
            if (!_inspectorUpdating)
            {
                Timeline.SetSelectedMuted(MuteCheck.IsChecked == true);
            }
        };

        DetachButton.Click += (_, _) => SetStatus(Timeline.DetachSelectedAudio()
            ? "Audio separado en su propia pista."
            : "Este clip no tiene audio que separar.");

        Timeline.SelectionChanged += (_, _) => RefreshInspector();
    }

    private void CommitOnRelease(Slider slider, Action commit)
    {
        void Handler(object? sender, RoutedEventArgs e)
        {
            if (!_inspectorUpdating)
            {
                commit();
            }
        }

        // 'handledEventsToo': el propio deslizador marca como atendido el fin del arrastre.
        slider.AddHandler(PointerReleasedEvent, Handler, RoutingStrategies.Bubble
            | RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, Handler, RoutingStrategies.Bubble
            | RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(KeyUpEvent, Handler, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private double CurrentGain() =>
        Timeline.SelectedClip?.AudioGainDb ?? Timeline.SelectedAudio?.GainDb ?? 0;

    private void CommitGain()
    {
        if (Math.Abs(GainSlider.Value - CurrentGain()) > 0.01)
        {
            Timeline.SetSelectedGain(GainSlider.Value);
        }
    }

    private void CommitFades()
    {
        if (Timeline.SelectedAudio is not { } clip)
        {
            return;
        }

        var fadeIn = TimeSpan.FromSeconds(FadeInSlider.Value);
        var fadeOut = TimeSpan.FromSeconds(FadeOutSlider.Value);

        if (fadeIn != clip.FadeIn || fadeOut != clip.FadeOut)
        {
            Timeline.SetSelectedFades(fadeIn, fadeOut);
        }
    }

    /// <summary>Pone al día el panel de la derecha con lo que hay seleccionado.</summary>
    private void RefreshInspector()
    {
        if (_rightTab == RightTab.None)
        {
            return;
        }

        AudioControls.IsVisible = false;
        LayerControls.IsVisible = false;
        InspectorNothing.IsVisible = true;
        InspectorTarget.Text = string.Empty;

        if (_rightTab == RightTab.Layer)
        {
            RefreshLayerInspector();
            return;
        }

        if (_rightTab != RightTab.Audio)
        {
            (InspectorTitle.Text, InspectorNothing.Text) = _rightTab switch
            {
                RightTab.Filters => ("Filtros", "Los filtros llegarán con el panel de color, en la versión 0.4."),
                RightTab.Effects => ("Efectos", "Los efectos llegarán junto a las transiciones, en la versión 0.5."),
                RightTab.Color => ("Color", "La corrección de color estilo Lumetri (curvas, ruedas, LUTs) llegará en la versión 0.4."),
                _ => ("Velocidad", "El cambio de velocidad llegará en la versión 0.5."),
            };
            return;
        }

        InspectorTitle.Text = "Audio";

        var video = Timeline.SelectedClip;
        var audio = Timeline.SelectedAudio;

        if (video is null && audio is null)
        {
            InspectorNothing.Text = "Selecciona un clip en la timeline para ajustar su volumen, silenciarlo o separar su audio.";
            return;
        }

        if (video is not null)
        {
            InspectorTarget.Text = Path.GetFileName(video.Source.Path);

            if (!video.Source.HasAudio)
            {
                InspectorNothing.Text = "Este video no tiene pista de audio.";
                return;
            }

            if (video.IsAudioDetached)
            {
                InspectorNothing.Text = "El audio de este clip está separado. Selecciónalo en su pista para ajustarlo.";
                return;
            }
        }
        else
        {
            InspectorTarget.Text = Path.GetFileName(audio!.Source.Path);
        }

        _inspectorUpdating = true;
        try
        {
            InspectorNothing.IsVisible = false;
            AudioControls.IsVisible = true;
            AudioControls.IsEnabled = Timeline.SelectionIsEditable;

            GainSlider.Value = Math.Clamp(CurrentGain(), GainSlider.Minimum, GainSlider.Maximum);
            GainReadout.Text = FormatGain(CurrentGain());
            MuteCheck.IsChecked = video?.IsAudioMuted ?? audio!.IsMuted;

            DetachButton.IsVisible = video is not null;
            FadeControls.IsVisible = audio is not null;

            if (audio is not null)
            {
                FadeInSlider.Value = audio.FadeIn.TotalSeconds;
                FadeOutSlider.Value = audio.FadeOut.TotalSeconds;
                FadeInReadout.Text = FormatSeconds(audio.FadeIn.TotalSeconds);
                FadeOutReadout.Text = FormatSeconds(audio.FadeOut.TotalSeconds);
            }
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }

    private static string FormatGain(double db) =>
        db.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " dB";

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
}
