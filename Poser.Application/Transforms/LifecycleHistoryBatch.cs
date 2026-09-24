namespace Poser.Application.Transforms;

/// <summary>One removal command's already-recorded inverses, with per-child
/// progress so a refused retry never repeats a sibling that already landed.</summary>
internal sealed class LifecycleHistoryBatch(string description)
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly List<Child> _children = new();
    private string? _failure;

    public bool TryAdd(HistoryEntry entry)
    {
        if (Environment.CurrentManagedThreadId != _thread || entry.Context is not null
            || entry is not (SceneLifecyclePatch or JournalStep)) return false;
        _children.Add(new(entry));
        return true;
    }

    public SceneLifecyclePatch? Build() => _children.Count == 0 ? null : new(
        _children.Count == 1 ? _children[0].Entry.Description : description,
        () => Run(undo: true), () => Run(undo: false))
    {
        FailureDetail = () => _failure,
        DropOnFailure = () => _children.All(child => child.Dropped),
    };

    private bool Run(bool undo)
    {
        _failure = null;
        foreach (var child in undo ? _children.AsEnumerable().Reverse() : _children)
        {
            if (child.Dropped || child.Undone == undo) continue;
            bool landed;
            string? detail = null;
            try
            {
                landed = child.Entry switch
                {
                    SceneLifecyclePatch step => undo ? step.Undo() : step.Redo(),
                    JournalStep step => undo ? step.Undo() : step.Redo(),
                    _ => false,
                };
            }
            catch (Exception ex) { landed = false; detail = ex.Message; }
            if (landed)
            {
                child.Undone = undo;
                child.Refused = false;
                continue;
            }
            switch (child.Entry)
            {
                case SceneLifecyclePatch step:
                    detail ??= step.FailureDetail?.Invoke();
                    child.Dropped = step.DropOnFailure?.Invoke() == true;
                    break;
                case JournalStep step:
                    detail ??= step.FailureDetail?.Invoke();
                    bool retain = step.RetainOnFailure || step.HasDeferredGroupCapture?.Invoke() == true;
                    child.Dropped = !retain && child.Refused;
                    child.Refused = !retain;
                    break;
            }
            _failure = detail ?? $"Could not {(undo ? "undo" : "redo")} {child.Entry.Description.ToLowerInvariant()}.";
            // Stop at the first refusal to preserve inverse ordering. A retry
            // resumes here (or after a permanently discarded child).
            return false;
        }
        return true;
    }

    private sealed class Child(HistoryEntry entry)
    {
        public HistoryEntry Entry { get; } = entry;
        public bool Undone { get; set; }
        public bool Dropped { get; set; }
        public bool Refused { get; set; }
    }
}
