// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EditFlow.App.Views;

/// <summary>Qué decide el usuario ante cambios sin guardar.</summary>
public enum UnsavedChoice
{
    /// <summary>No hacer nada y volver al proyecto.</summary>
    Cancel,

    /// <summary>Guardar y continuar.</summary>
    Save,

    /// <summary>Continuar sin guardar.</summary>
    Discard,
}

/// <summary>Pregunta qué hacer con los cambios sin guardar antes de cerrar o cambiar de proyecto.</summary>
public sealed class UnsavedChangesDialog : Window
{
    /// <summary>Crea el diálogo para el proyecto indicado.</summary>
    public UnsavedChangesDialog(string projectName)
    {
        Title = "Cambios sin guardar";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1c1c20"));

        var save = new Button { Content = "Guardar", Classes = { "primary" }, Padding = new Thickness(18, 8) };
        var discard = new Button { Content = "No guardar", Padding = new Thickness(18, 8) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(18, 8) };

        save.Click += (_, _) => Close(UnsavedChoice.Save);
        discard.Click += (_, _) => Close(UnsavedChoice.Discard);
        cancel.Click += (_, _) => Close(UnsavedChoice.Cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 22),
            Spacing = 20,
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "¿Guardar los cambios?",
                            FontSize = 18,
                            FontWeight = FontWeight.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = $"«{projectName}» tiene cambios que aún no se han guardado. " +
                                   "Si continúas sin guardar, se perderán.",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(Color.Parse("#9a9aa2")),
                        },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, discard, save },
                },
            },
        };

        // Escape equivale a cancelar: es la salida que nunca pierde trabajo.
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close(UnsavedChoice.Cancel);
            }
        };
    }
}
