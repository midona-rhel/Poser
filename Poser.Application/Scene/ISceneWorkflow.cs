using Poser.Domain;
using Poser.Domain.Operations;

namespace Poser.Application.Scene;

/// <summary>Saving and loading a whole scene, as the Scene tab, the library and
/// the issue report start them. One save or load runs at a time.</summary>
public interface ISceneWorkflow
{
    /// <summary>How many bytes saving with modded appearance would add right now.</summary>
    long EstimatedAppearanceBytes { get; }

    /// <summary>The running or last save or load: its step, and once it has
    /// ended, its result.</summary>
    SceneProgress? Progress { get; }

    /// <summary>The result of the last save or load. It reads Pending while one
    /// is still running.</summary>
    OperationReceipt? Receipt { get; }

    /// <summary>Whether a save or load is running.</summary>
    bool Busy { get; }

    /// <summary>Asks the running save or load to stop.</summary>
    void Cancel();

    /// <summary>Starts saving the scene to <paramref name="path"/>. Success only
    /// means the save has started; whether the file was written is in
    /// <see cref="Progress"/> once it ends.</summary>
    Outcome BeginSave(string path, string? description = null, SceneSaveOptions? options = null);

    /// <summary>Starts loading the scene at <paramref name="path"/>. Success only
    /// means the load has started; what came back is in <see cref="Progress"/>
    /// once it ends.</summary>
    Outcome BeginLoad(string path, SceneLoadOptions? options = null);
}
