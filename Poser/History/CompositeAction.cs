using System.Collections.Generic;
using System.Linq;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// A composite action that groups multiple actions into one undo/redo operation.
/// </summary>
public class CompositeAction : IHistoryAction
{
    private readonly List<IHistoryAction> _actions;
    private readonly string _description;

    public string Description => _description;

    public CompositeAction(string description, IEnumerable<IHistoryAction> actions)
    {
        _description = description;
        _actions = actions.ToList();
    }

    public CompositeAction(string description, params IHistoryAction[] actions)
        : this(description, (IEnumerable<IHistoryAction>)actions)
    {
    }

    public void Execute()
    {
        foreach (var action in _actions)
        {
            action.Execute();
        }
    }

    public void Undo()
    {
        // Undo in reverse order
        for (int i = _actions.Count - 1; i >= 0; i--)
        {
            _actions[i].Undo();
        }
    }
}
