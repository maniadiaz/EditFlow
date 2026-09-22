// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using EditFlow.Core.Timeline;

namespace EditFlow.App;

// Transición de entrada del clip de video seleccionado, desde el que lo precede en la pista principal.
public partial class MainWindow
{
    private static readonly (TransitionKind Kind, string Label)[] TransitionKinds =
    [
        (TransitionKind.Dissolve, "Disolvencia"),
        (TransitionKind.FadeToBlack, "Funde a negro"),
        (TransitionKind.FadeToWhite, "Funde a blanco"),
        (TransitionKind.WipeLeft, "Barrido a la izquierda"),
        (TransitionKind.WipeRight, "Barrido a la derecha"),
        (TransitionKind.SlideLeft, "Desliza a la izquierda"),
        (TransitionKind.SlideRight, "Desliza a la derecha"),
        (TransitionKind.CircleOpen, "Círculo que se abre"),
    ];

    // Valor que había al empezar a arrastrar el deslizador de duración. Igual que en el color:
    // mientras se arrastra el cambio se ve, pero entra en el historial de una sola vez al soltar.
    private Transition? _transitionBaseline;

    private void WireTransitions()
    {
        TransitionKindCombo.ItemsSource = TransitionKinds.Select(t => t.Label).ToArray();
        TransitionKindCombo.SelectionChanged += (_, _) => OnTransitionKindChanged();

        foreach (var (kind, label) in TransitionKinds)
        {
            var button = new Avalonia.Controls.Button
            {
                Content = label,
                Classes = { "quiet" },
                FontSize = 11,
                Padding = new Avalonia.Thickness(10, 5),
                Margin = new Avalonia.Thickness(0, 0, 6, 6),
            };
            button.Click += (_, _) => ApplyGalleryTransition(kind);
            TransitionGallery.Children.Add(button);
        }

        TransitionDurationSlider.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!_inspectorUpdating)
            {
                _transitionBaseline ??= Timeline.SelectedClip?.TransitionIn;
            }
        }, RoutingStrategies.Tunnel);

        TransitionDurationSlider.ValueChanged += (_, _) => OnTransitionDurationChanged();
        TransitionDurationSlider.AddHandler(PointerReleasedEvent, (_, _) => CommitTransitionDuration(), RoutingStrategies.Tunnel);
        TransitionDurationSlider.PointerCaptureLost += (_, _) => CommitTransitionDuration();

        TransitionRemoveButton.Click += (_, _) =>
        {
            _transitionBaseline = null;
            if (Timeline.SetSelectedTransition(Transition.None))
            {
                SetStatus("Transición quitada.");
            }

            RefreshInspector();
        };

        // Un clic en la marca de la timeline abre este panel directamente, en vez de obligar
        // a buscar la pestaña a mano.
        Timeline.TransitionBadgeClicked += (_, _) =>
        {
            if (_rightTab != RightTab.Transition)
            {
                ToggleRightTab(RightTab.Transition);
            }
        };
    }

    /// <summary>Aplica una transición de la galería de la pestaña izquierda al clip seleccionado.</summary>
    private void ApplyGalleryTransition(TransitionKind kind)
    {
        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            SetStatus("Selecciona antes un clip de video en la timeline (que no sea el primero) para aplicarle la transición.");
            return;
        }

        if (Timeline.Sequence?.Video.IndexOf(clip) == 0)
        {
            SetStatus("El primer clip no tiene nada antes con lo que fundirse.");
            return;
        }

        var duration = clip.TransitionIn.IsNone ? Transition.DefaultDuration : clip.TransitionIn.Duration;
        SetStatus(Timeline.SetSelectedTransition(new Transition(kind, duration))
            ? "Transición aplicada."
            : "La capa está bloqueada: desbloquéala para aplicar la transición.");

        if (_rightTab != RightTab.Transition)
        {
            ToggleRightTab(RightTab.Transition);
        }
        else
        {
            RefreshInspector();
        }
    }

    private static TransitionKind KindAt(int index) =>
        index >= 0 && index < TransitionKinds.Length ? TransitionKinds[index].Kind : TransitionKind.Dissolve;

    private void OnTransitionKindChanged()
    {
        if (_inspectorUpdating || Timeline.SelectedClip is not { } clip)
        {
            return;
        }

        // Elegir un tipo sin transición previa arranca una con la duración por defecto; con
        // una ya puesta, solo cambia el tipo y conserva la duración que el usuario había fijado.
        var duration = clip.TransitionIn.IsNone ? Transition.DefaultDuration : clip.TransitionIn.Duration;

        if (Timeline.SetSelectedTransition(new Transition(KindAt(TransitionKindCombo.SelectedIndex), duration)))
        {
            SetStatus("Transición ajustada.");
        }

        RefreshInspector();
    }

    private void OnTransitionDurationChanged()
    {
        if (_inspectorUpdating || Timeline.SelectedClip is not { } clip)
        {
            return;
        }

        var duration = TimeSpan.FromSeconds(TransitionDurationSlider.Value);
        TransitionDurationReadout.Text = FormatTransitionSeconds(duration);

        // Sin transición todavía, mover el deslizador no crea una: hace falta elegir antes
        // un tipo en el desplegable.
        if (!clip.TransitionIn.IsNone)
        {
            Timeline.SetSelectedTransitionProvisional(new Transition(clip.TransitionIn.Kind, duration));
        }
    }

    private void CommitTransitionDuration()
    {
        if (_transitionBaseline is not { IsNone: false } baseline || Timeline.SelectedClip is not { } clip)
        {
            _transitionBaseline = null;
            return;
        }

        _transitionBaseline = null;

        var final = new Transition(clip.TransitionIn.Kind, TimeSpan.FromSeconds(TransitionDurationSlider.Value));
        if (Timeline.SetSelectedTransition(final, baseline))
        {
            SetStatus("Transición ajustada.");
        }
    }

    private static string FormatTransitionSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>Muestra en el panel la transición del clip seleccionado, o explica qué seleccionar.</summary>
    private void RefreshTransitionInspector()
    {
        InspectorTitle.Text = "Transición";

        if (Timeline.SelectedClip is not { IsGap: false } clip)
        {
            InspectorNothing.Text = Timeline.SelectedClip is { IsGap: true }
                ? "Un hueco no admite transición."
                : "Selecciona un clip de video en la timeline para añadirle una transición desde el anterior.";
            return;
        }

        if (Timeline.Sequence?.Video.IndexOf(clip) == 0)
        {
            InspectorNothing.Text = "El primer clip no tiene nada antes con lo que fundirse.";
            return;
        }

        InspectorTarget.Text = System.IO.Path.GetFileName(clip.Source.Path);

        _inspectorUpdating = true;
        try
        {
            InspectorNothing.IsVisible = false;
            TransitionControls.IsVisible = true;
            TransitionControls.IsEnabled = Timeline.SelectionIsEditable;

            var transition = clip.TransitionIn;
            var index = Array.FindIndex(TransitionKinds, t => t.Kind == transition.Kind);
            TransitionKindCombo.SelectedIndex = index < 0 ? 0 : index;

            var duration = transition.IsNone ? Transition.DefaultDuration : transition.Duration;
            TransitionDurationSlider.Value = duration.TotalSeconds;
            TransitionDurationReadout.Text = FormatTransitionSeconds(duration);
            TransitionRemoveButton.IsEnabled = !transition.IsNone;
        }
        finally
        {
            _inspectorUpdating = false;
        }
    }
}
