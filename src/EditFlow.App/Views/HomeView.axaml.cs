// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EditFlow.Core.Projects;
using EditFlow.Core.Text;

namespace EditFlow.App.Views;

/// <summary>
/// Pantalla de inicio: crear un proyecto y volver a los que ya existen.
/// </summary>
public partial class HomeView : UserControl
{
    private const double CardWidth = 264;
    private const double CoverHeight = 148;

    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    /// <summary>Crea la vista.</summary>
    public HomeView()
    {
        InitializeComponent();

        NewProjectButton.Click += (_, _) => NewProjectRequested?.Invoke(this, EventArgs.Empty);
        OpenProjectButton.Click += (_, _) => OpenProjectRequested?.Invoke(this, EventArgs.Empty);
        HomeNav.Click += (_, _) => ShowPage(templates: false);
        TemplatesNav.Click += (_, _) => ShowPage(templates: true);

        _messageTimer.Tick += (_, _) => HideMessage();
        MessageBar.PointerPressed += (_, _) => HideMessage();

        Refresh([]);
    }

    /// <summary>El usuario quiere empezar un proyecto vacío.</summary>
    public event EventHandler? NewProjectRequested;

    /// <summary>El usuario quiere elegir un archivo de proyecto.</summary>
    public event EventHandler? OpenProjectRequested;

    /// <summary>El usuario pulsó un proyecto de la lista; lleva su ruta.</summary>
    public event EventHandler<string>? OpenRecentRequested;

    /// <summary>El usuario quiere quitar un proyecto de la lista; lleva su ruta.</summary>
    public event EventHandler<string>? RemoveRecentRequested;

    /// <summary>Redibuja la lista de proyectos.</summary>
    public void Refresh(IReadOnlyList<RecentProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        ProjectsPanel.Children.Clear();

        foreach (var project in projects)
        {
            ProjectsPanel.Children.Add(BuildCard(project));
        }

        // Sin proyectos no se deja un hueco mudo: se invita a empezar el primero.
        EmptyState.IsVisible = projects.Count == 0;
        ProjectsPanel.IsVisible = projects.Count > 0;
    }

    /// <summary>Muestra un aviso breve al pie de la pantalla.</summary>
    public void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageBar.IsVisible = true;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void HideMessage()
    {
        _messageTimer.Stop();
        MessageBar.IsVisible = false;
    }

    private void ShowPage(bool templates)
    {
        HomePage.IsVisible = !templates;
        TemplatesPage.IsVisible = templates;

        HomeNav.Classes.Set("selected", !templates);
        TemplatesNav.Classes.Set("selected", templates);
    }

    private Button BuildCard(RecentProject project)
    {
        var cover = new Border
        {
            Height = CoverHeight,
            Background = new SolidColorBrush(Color.Parse("#17171b")),
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            ClipToBounds = true,
            Child = LoadCover(project.ThumbnailPath),
        };

        var name = new TextBlock
        {
            Text = project.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)this.FindResource("Text")!,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var meta = new TextBlock
        {
            Text = Describe(project),
            FontSize = 11.5,
            Foreground = (IBrush)this.FindResource("TextMuted")!,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var body = new StackPanel { Spacing = 4, Margin = new Thickness(14, 12, 14, 14), Children = { name, meta } };

        var stack = new StackPanel { Children = { cover, body } };

        var open = new MenuItem { Header = "Abrir" };
        open.Click += (_, _) => OpenRecentRequested?.Invoke(this, project.FilePath);

        var remove = new MenuItem { Header = "Quitar de la lista" };
        remove.Click += (_, _) => RemoveRecentRequested?.Invoke(this, project.FilePath);

        var card = new Button
        {
            Classes = { "card" },
            Width = CardWidth,
            Margin = new Thickness(0, 0, 18, 18),
            Content = stack,
            ContextMenu = new ContextMenu { Items = { open, remove } },
        };

        ToolTip.SetTip(card, project.FilePath);
        card.Click += (_, _) => OpenRecentRequested?.Invoke(this, project.FilePath);
        return card;
    }

    private Control LoadCover(string? thumbnailPath)
    {
        if (thumbnailPath is not null && File.Exists(thumbnailPath))
        {
            try
            {
                // Se decodifica a la anchura de la tarjeta: cargar una miniatura de 480 px a
                // tamaño completo para cada tarjeta solo gastaría memoria.
                using var stream = File.OpenRead(thumbnailPath);
                return new Image
                {
                    Source = Bitmap.DecodeToWidth(stream, (int)CardWidth * 2),
                    Stretch = Stretch.UniformToFill,
                };
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
                // Una portada ilegible se sustituye por el icono, como si no existiera.
            }
        }

        return new Avalonia.Controls.Shapes.Path
        {
            Data = (Geometry)this.FindResource("IconFilm")!,
            Fill = (IBrush)this.FindResource("TextFaint")!,
            Stretch = Stretch.Uniform,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string Describe(RecentProject project)
    {
        var clips = project.ClipCount == 1 ? "1 clip" : $"{project.ClipCount} clips";
        var duration = project.Duration.ToString(
            project.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

        return $"{clips} · {duration} · {RelativeTime.Describe(project.LastOpenedUtc, DateTime.UtcNow)}";
    }
}
