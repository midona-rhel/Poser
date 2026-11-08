namespace Poser.Application.AutoSave;

public enum AutoSaveTerminalStatus
{
    /// <summary>No exit drain has been requested.</summary>
    Pending,
    /// <summary>Every accepted snapshot reached durable storage.</summary>
    Written,
    /// <summary>Clean-on-exit removed all snapshots after a successful drain.</summary>
    Cleaned,
    /// <summary>Capture, writing, cleanup, or worker ownership failed.</summary>
    RecoveryRequired,
    /// <summary>No final snapshot was requested.</summary>
    NotAttempted,
}

