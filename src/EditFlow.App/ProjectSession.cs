// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EditFlow.Core.Projects;

namespace EditFlow.App;

/// <summary>
/// Resultado de una operación de proyecto, listo para mostrar al usuario.
/// </summary>
/// <param name="Completed">
/// <see langword="false"/> si el usuario canceló, por ejemplo al cerrar el selector.
/// </param>
/// <param name="Message">Texto para la barra de estado.</param>
public readonly record struct ProjectActionResult(bool Completed, string Message)
{
    /// <summary>Operación cancelada por el usuario, sin nada que informar.</summary>
    public static ProjectActionResult Cancelled { get; } = new(false, string.Empty);
}

/// <summary>
/// Gestiona abrir, guardar y crear proyectos, incluidos los diálogos de archivo.
/// </summary>
/// <remarks>
/// Vive aparte de la ventana principal porque esta ya concentra demasiado: reproductor,
/// timeline, importación y exportación. Separar el ciclo de vida del proyecto mantiene
/// cada parte legible por su cuenta.
/// </remarks>
public sealed class ProjectSession
{
    private static readonly FilePickerFileType ProjectType = new("Proyecto de EditFlow")
    {
        Patterns = ["*" + ProjectSerializer.Extension],
    };

    private readonly TopLevel _owner;

    /// <summary>Crea la sesión asociada a una ventana.</summary>
    public ProjectSession(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _current.UnsavedChangesChanged += OnUnsavedChangesChanged;
    }

    private EditProject _current = new();

    /// <summary>Proyecto abierto.</summary>
    public EditProject Current
    {
        get => _current;
        private set
        {
            _current.UnsavedChangesChanged -= OnUnsavedChangesChanged;
            _current = value;
            _current.UnsavedChangesChanged += OnUnsavedChangesChanged;
        }
    }

    private void OnUnsavedChangesChanged(object? sender, EventArgs e) =>
        StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Se dispara cuando se carga o se crea otro proyecto.</summary>
    public event EventHandler? ProjectReplaced;

    /// <summary>Se dispara cuando cambia el nombre o el estado de guardado.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Se dispara cuando un proyecto se abre o se guarda con éxito en un archivo.</summary>
    /// <remarks>Es el momento de anotarlo entre los recientes.</remarks>
    public event EventHandler? ProjectPersisted;

    /// <summary>Texto para la barra de título.</summary>
    public string WindowTitle =>
        Current.HasUnsavedChanges
            ? $"EditFlow — {Current.DisplayName} •"
            : $"EditFlow — {Current.DisplayName}";

    /// <summary>Registra que el montaje cambió.</summary>
    public void MarkDirty() => Current.MarkDirty();

    /// <summary>Empieza un proyecto vacío.</summary>
    public ProjectActionResult New()
    {
        Current = new EditProject();
        ProjectReplaced?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ProjectActionResult(true, "Proyecto nuevo.");
    }

    /// <summary>Abre un proyecto pidiendo el archivo al usuario.</summary>
    public async Task<ProjectActionResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir proyecto",
            AllowMultiple = false,
            FileTypeFilter = [ProjectType],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        return path is null
            ? ProjectActionResult.Cancelled
            : await OpenAsync(path, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Abre un proyecto desde una ruta concreta.</summary>
    public async Task<ProjectActionResult> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProjectSerializer.LoadAsync(path, cancellationToken).ConfigureAwait(true);
            Current = result.Project;

            ProjectReplaced?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
            ProjectPersisted?.Invoke(this, EventArgs.Empty);

            if (!result.HasMissingMedia)
            {
                return new ProjectActionResult(true,
                    $"Proyecto abierto: {result.Project.Timeline.Clips.Count} clip(s).");
            }

            // El montaje sigue entero: los clips de un archivo que no aparece se conservan con su
            // sitio y sus cortes, y reconectarlo devuelve la imagen. Lo que hay que decir es qué
            // falta y qué hacer, no que se haya perdido nada.
            return new ProjectActionResult(true,
                $"Proyecto abierto. El montaje está intacto, pero faltan {result.MissingMedia.Count} archivo(s) "
                + "por reconectar (botón derecho sobre cada uno en el panel de medios):" +
                Environment.NewLine +
                string.Join(Environment.NewLine,
                    result.MissingMedia.Select(m => "  · " + Path.GetFileName(m))));
        }
        catch (Exception ex) when (ex is ProjectFormatException or FileNotFoundException or IOException)
        {
            return new ProjectActionResult(false, "No se pudo abrir: " + ex.Message);
        }
    }

    /// <summary>Guarda; pide la ruta solo si el proyecto nunca se guardó.</summary>
    public Task<ProjectActionResult> SaveAsync(CancellationToken cancellationToken = default) =>
        Current.FilePath is null
            ? SaveAsAsync(cancellationToken)
            : WriteAsync(Current.FilePath, cancellationToken);

    /// <summary>Guarda pidiendo siempre la ruta.</summary>
    public async Task<ProjectActionResult> SaveAsAsync(CancellationToken cancellationToken = default)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar proyecto",
            DefaultExtension = ProjectSerializer.Extension.TrimStart('.'),
            SuggestedFileName = Current.DisplayName + ProjectSerializer.Extension,
            FileTypeChoices = [ProjectType],
        });

        var path = file?.TryGetLocalPath();
        return path is null
            ? ProjectActionResult.Cancelled
            : await WriteAsync(path, cancellationToken).ConfigureAwait(true);
    }

    private async Task<ProjectActionResult> WriteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await ProjectSerializer.SaveAsync(Current, path, cancellationToken).ConfigureAwait(true);
            StateChanged?.Invoke(this, EventArgs.Empty);
            ProjectPersisted?.Invoke(this, EventArgs.Empty);
            return new ProjectActionResult(true, "Guardado en " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectActionResult(false, "No se pudo guardar: " + ex.Message);
        }
    }
}
