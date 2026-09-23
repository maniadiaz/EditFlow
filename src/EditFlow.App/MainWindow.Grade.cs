// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Corrección de color avanzada: ruedas, curvas, color selectivo, LUT e histograma.
public partial class MainWindow
{
    /// <summary>Canales de curva que se pueden editar, en el orden del desplegable.</summary>
    private static readonly string[] CurveChannels = ["Maestra (los tres canales)", "Rojo", "Verde", "Azul"];

    private static readonly (string Label, ColorFamily Family)[] ColorFamilies =
    [
        ("Ninguna", ColorFamily.None),
        ("Rojos", ColorFamily.Reds),
        ("Amarillos", ColorFamily.Yellows),
        ("Verdes", ColorFamily.Greens),
        ("Cianes", ColorFamily.Cyans),
        ("Azules", ColorFamily.Blues),
        ("Magentas", ColorFamily.Magentas),
    ];

    /// <summary>Corrección que había al empezar a arrastrar, para que deshacer vuelva ahí de un paso.</summary>
    private ColorGrade? _gradeBeforeDrag;

    private void WireGrade()
    {
        AdvancedColorToggle.IsCheckedChanged += (_, _) =>
        {
            var open = AdvancedColorToggle.IsChecked == true;
            AdvancedColorPanel.IsVisible = open;

            // El histograma se mide desde el hilo de decodificación, que no puede leer una
            // propiedad de un control: si lo intenta, Avalonia lanza y el fotograma se pierde
            // sin dejar rastro. De ahí esta copia, que solo escribe el hilo de interfaz.
            _scopeVisible = open;

            if (open)
            {
                // Parado no llegan fotogramas nuevos: se pide uno para que el instrumento no
                // aparezca vacío hasta que alguien mueva el cabezal.
                SeekTo(Timeline.Playhead, follow: false);
            }
        };

        // --- ruedas -------------------------------------------------------
        WireWheel(ShadowWheel, (grade, wheel) => grade with { Shadows = wheel });
        WireWheel(MidtoneWheel, (grade, wheel) => grade with { Midtones = wheel });
        WireWheel(HighlightWheel, (grade, wheel) => grade with { Highlights = wheel });

        // --- curvas -------------------------------------------------------
        CurveChannelCombo.ItemsSource = CurveChannels;
        CurveChannelCombo.SelectedIndex = 0;
        CurveChannelCombo.SelectionChanged += (_, _) =>
        {
            if (!_inspectorUpdating)
            {
                RefreshGradePanel();
            }
        };

        Curve.Changing += (_, curve) =>
        {
            _gradeBeforeDrag ??= Timeline.SelectedClip?.Grade;
            ApplyGrade(g => WithCurve(g, curve), live: true);
        };

        Curve.Committed += (_, curve) =>
        {
            ApplyGrade(g => WithCurve(g, curve), live: false);
            _gradeBeforeDrag = null;
        };

        CurveResetButton.Click += (_, _) =>
        {
            ApplyGrade(g => WithCurve(g, ToneCurve.Identity), live: false);
            RefreshGradePanel();
        };

        // --- color selectivo ---------------------------------------------
        SelectiveFamilyCombo.ItemsSource = ColorFamilies.Select(f => f.Label).ToArray();
        SelectiveFamilyCombo.SelectedIndex = 0;
        SelectiveFamilyCombo.SelectionChanged += (_, _) =>
        {
            if (_inspectorUpdating)
            {
                return;
            }

            SelectiveSliders.IsVisible = SelectiveFamilyCombo.SelectedIndex > 0;
            CommitSelective();
        };

        foreach (var slider in new[]
        {
            SelectiveCyanRed, SelectiveMagentaGreen, SelectiveYellowBlue, SelectiveLightness,
        })
        {
            CommitOnRelease(slider, CommitSelective);
        }

        // --- LUT ----------------------------------------------------------
        ImportLutButton.Click += async (_, _) => await ImportLutAsync();
        ClearLutButton.Click += (_, _) =>
        {
            ApplyGrade(g => g with { LutPath = null }, live: false);
            RefreshGradePanel();
            SetStatus("LUT quitado del clip.");
        };

        GradeResetButton.Click += (_, _) =>
        {
            if (Timeline.SetSelectedGrade(ColorGrade.None))
            {
                RefreshGradePanel();
                SeekTo(Timeline.Playhead, follow: false);
                SetStatus("Corrección avanzada quitada; el clip vuelve a su color original.");
            }
        };
    }

    private void WireWheel(Controls.ColorWheelPad pad, Func<ColorGrade, ColorWheel, ColorGrade> apply)
    {
        pad.Changing += (_, wheel) =>
        {
            _gradeBeforeDrag ??= Timeline.SelectedClip?.Grade;
            ApplyGrade(g => apply(g, wheel), live: true);
        };

        pad.Committed += (_, wheel) =>
        {
            ApplyGrade(g => apply(g, wheel), live: false);
            _gradeBeforeDrag = null;
        };
    }

    /// <summary>
    /// Aplica un cambio a la corrección del clip seleccionado.
    /// </summary>
    /// <param name="change">Qué cambiar.</param>
    /// <param name="live">
    /// Si el cambio viene de un arrastre en curso. Mientras se arrastra, el preview se actualiza
    /// pero el historial no: soltar deja una sola entrada, no cien.
    /// </param>
    private void ApplyGrade(Func<ColorGrade, ColorGrade> change, bool live)
    {
        if (_inspectorUpdating || Timeline.SelectedClip is not { IsGap: false } clip)
        {
            return;
        }

        var updated = change(clip.Grade);
        if (updated == clip.Grade)
        {
            return;
        }

        if (live)
        {
            clip.Grade = updated;
        }
        else
        {
            // Al soltar se registra el salto entero, desde lo que había antes de empezar.
            clip.Grade = _gradeBeforeDrag ?? clip.Grade;
            Timeline.SetSelectedGrade(updated, _gradeBeforeDrag);
        }

        SeekTo(Timeline.Playhead, follow: false);
    }

    private ColorGrade WithCurve(ColorGrade grade, ToneCurve curve) => CurveChannelCombo.SelectedIndex switch
    {
        1 => grade with { Red = curve },
        2 => grade with { Green = curve },
        3 => grade with { Blue = curve },
        _ => grade with { Master = curve },
    };

    private ToneCurve CurrentCurve(ColorGrade grade) => CurveChannelCombo.SelectedIndex switch
    {
        1 => grade.RedCurve,
        2 => grade.GreenCurve,
        3 => grade.BlueCurve,
        _ => grade.MasterCurve,
    };

    private void CommitSelective()
    {
        var index = Math.Clamp(SelectiveFamilyCombo.SelectedIndex, 0, ColorFamilies.Length - 1);

        ApplyGrade(
            g => g with
            {
                Selective = new SelectiveColor(
                    ColorFamilies[index].Family,
                    SelectiveCyanRed.Value / 100,
                    SelectiveMagentaGreen.Value / 100,
                    SelectiveYellowBlue.Value / 100,
                    SelectiveLightness.Value / 100),
            },
            live: false);
    }

    private async Task ImportLutAsync()
    {
        if (Timeline.SelectedClip is not { IsGap: false })
        {
            SetStatus("Selecciona un clip de video para aplicarle un LUT.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar LUT",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Tabla de color") { Patterns = ["*.cube"] }],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        // Se comprueba antes de aplicarlo: un archivo que no es un .cube haría fallar la
        // exportación entera mucho más tarde, cuando ya no se sabe por qué.
        if (!LooksLikeCube(path))
        {
            SetStatus("Ese archivo no parece un LUT .cube válido.");
            return;
        }

        ApplyGrade(g => g with { LutPath = path }, live: false);
        RefreshGradePanel();
        SetStatus($"LUT «{Path.GetFileName(path)}» aplicado al clip.");
    }

    /// <summary>Comprueba por encima que un archivo es una tabla de color.</summary>
    private static bool LooksLikeCube(string path)
    {
        try
        {
            // Basta con la cabecera: un .cube declara su tamaño en las primeras líneas.
            return File.ReadLines(path)
                .Take(40)
                .Any(line => line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Último instante en que se midió el histograma, en milisegundos de reloj.</summary>
    private long _lastHistogramTick;

    /// <summary>Copia de si el panel avanzado está abierto, legible desde cualquier hilo.</summary>
    private volatile bool _scopeVisible;

    /// <summary>
    /// Mide el histograma del fotograma que acaba de llegar al preview.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Se llama desde el hilo de decodificación, así que la medida se hace ahí —recorrer una
    /// muestra del fotograma cuesta una fracción de milisegundo— y solo el repintado va al hilo
    /// de interfaz.
    /// </para>
    /// <para>
    /// Reproduciendo llegan hasta 60 fotogramas por segundo y el instrumento no necesita tantos:
    /// se limita a unos diez por segundo, que ya se ve como algo continuo y deja el resto del
    /// tiempo para decodificar.
    /// </para>
    /// </remarks>
    private void MeasureHistogram(EditFlow.Engine.Playback.VideoFrame frame)
    {
        if (!_scopeVisible || !frame.IsValid)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastHistogramTick < 100)
        {
            return;
        }

        _lastHistogramTick = now;

        // Se cuenta aquí mismo, antes de que el fotograma se recicle; al hilo de interfaz solo
        // va el repintado.
        Histogram.Measure(frame.Pixels, frame.Stride, frame.Width, frame.Height);

        Avalonia.Threading.Dispatcher.UIThread.Post(
            Histogram.Refresh,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Pone al día el panel avanzado con la corrección del clip seleccionado.</summary>
    private void RefreshGradePanel()
    {
        var grade = Timeline.SelectedClip is { IsGap: false } clip ? clip.Grade : ColorGrade.None;

        _inspectorUpdating = true;
        try
        {
            ShadowWheel.Value = grade.ShadowWheel;
            MidtoneWheel.Value = grade.MidtoneWheel;
            HighlightWheel.Value = grade.HighlightWheel;

            Curve.Curve = CurrentCurve(grade);
            Curve.Stroke = CurveChannelCombo.SelectedIndex switch
            {
                1 => Brushes.IndianRed,
                2 => Brushes.MediumSeaGreen,
                3 => Brushes.CornflowerBlue,
                _ => Brushes.White,
            };

            Curve.InvalidateVisual();

            var selective = grade.SelectiveAdjust;
            var familyIndex = Array.FindIndex(ColorFamilies, f => f.Family == selective.Family);
            SelectiveFamilyCombo.SelectedIndex = Math.Max(familyIndex, 0);
            SelectiveSliders.IsVisible = selective.Family != ColorFamily.None;
            SelectiveCyanRed.Value = Math.Round(selective.CyanRed * 100);
            SelectiveMagentaGreen.Value = Math.Round(selective.MagentaGreen * 100);
            SelectiveYellowBlue.Value = Math.Round(selective.YellowBlue * 100);
            SelectiveLightness.Value = Math.Round(selective.Lightness * 100);

            var hasLut = !string.IsNullOrWhiteSpace(grade.LutPath);
            ClearLutButton.IsEnabled = hasLut;
            LutLabel.IsVisible = hasLut;
            LutLabel.Text = hasLut ? Path.GetFileName(grade.LutPath) : string.Empty;
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }
}
