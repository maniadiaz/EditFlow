namespace EditFlow.Core.Undo;

/// <summary>Una operación de edición que sabe deshacerse.</summary>
public interface IUndoableCommand
{
    /// <summary>Descripción legible, para mostrarla junto a "Deshacer".</summary>
    string Description { get; }

    /// <summary>Aplica la operación.</summary>
    void Execute();

    /// <summary>Revierte la operación, dejando el estado exactamente como estaba.</summary>
    void Undo();
}

/// <summary>
/// Historial de operaciones con deshacer y rehacer.
/// </summary>
/// <remarks>
/// El historial se construye desde el principio, no se añade al final. Reconstruir el
/// estado anterior a posteriori obliga a que cada operación sepa invertirse sobre un
/// modelo que ya ha cambiado, y eso es mucho más difícil que capturar lo necesario en
/// el momento de ejecutarla.
/// </remarks>
public sealed class UndoHistory
{
    private readonly List<IUndoableCommand> _done = [];
    private readonly List<IUndoableCommand> _undone = [];

    /// <summary>Se dispara cuando cambia lo que se puede deshacer o rehacer.</summary>
    public event EventHandler? Changed;

    /// <summary>Indica si hay algo que deshacer.</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>Indica si hay algo que rehacer.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>Descripción de la próxima operación a deshacer.</summary>
    public string? NextUndoDescription => CanUndo ? _done[^1].Description : null;

    /// <summary>Descripción de la próxima operación a rehacer.</summary>
    public string? NextRedoDescription => CanRedo ? _undone[^1].Description : null;

    /// <summary>Ejecuta una operación y la añade al historial.</summary>
    /// <remarks>
    /// Ejecutar algo nuevo descarta la pila de rehacer. Conservarla permitiría rehacer
    /// operaciones que asumían un estado que ya no existe.
    /// </remarks>
    public void Do(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Execute();
        _done.Add(command);
        _undone.Clear();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deshace la última operación.</summary>
    /// <returns><see langword="true"/> si había algo que deshacer.</returns>
    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        var command = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        command.Undo();
        _undone.Add(command);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Rehace la última operación deshecha.</summary>
    /// <returns><see langword="true"/> si había algo que rehacer.</returns>
    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        var command = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        command.Execute();
        _done.Add(command);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Vacía el historial, por ejemplo al abrir otro proyecto.</summary>
    public void Clear()
    {
        if (_done.Count == 0 && _undone.Count == 0)
        {
            return;
        }

        _done.Clear();
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
