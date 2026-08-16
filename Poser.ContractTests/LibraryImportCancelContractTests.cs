extern alias ProductionPoser;

using Poser.Application.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// The library footer's character-file stop. A single-flight MCDF
/// transaction is global state, so the gate that lets a tab claim the
/// footer caption and the action row has to name the tab explicitly:
/// without that, a running import paints its phase and its Cancel over
/// tabs that cannot start one and buries their own status lines (the
/// auto-save tab's health readout above all).
/// </summary>
public sealed class LibraryImportCancelContractTests
{
    private static McdfProgress Running(
        McdfOperationKind kind = McdfOperationKind.Import,
        McdfPhase phase = McdfPhase.Extracting,
        bool cancellable = true) =>
        new(ActorId.New(), "look.mcdf", kind, phase, 0, 0, 0, 0, cancellable, null);

    private static OperationReceipt Pending() =>
        OperationReceipt.Pending(
            Guid.NewGuid(), OperationEpoch.First, SessionGeneration.New(), ActorId.New());

    private static OperationReceipt Applied() =>
        OperationReceipt.Applied(
            Guid.NewGuid(), OperationEpoch.First, SessionGeneration.New(),
            ActorId.New(), "done");

    [Fact]
    public void The_mcdf_tab_claims_the_footer_while_an_import_is_pending() =>
        Assert.True(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, busy: true, Running(), Pending()));

    /// <summary>Every tab that is not the MCDF one, enumerated from the
    /// type itself so a tab added later is covered without editing this.</summary>
    [Fact]
    public void No_other_tab_may_claim_it()
    {
        foreach (var type in Enum.GetValues<PoseLibraryPane.LibraryType>())
        {
            if (type == PoseLibraryPane.LibraryType.Mcdf)
                continue;
            Assert.False(
                PoseLibraryPane.ShowsImportCancel(type, busy: true, Running(), Pending()),
                $"{type} must not claim the footer for an MCDF import.");
        }
    }

    [Fact]
    public void An_idle_transaction_claims_nothing() =>
        Assert.False(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, busy: false, Running(), Pending()));

    [Fact]
    public void An_export_is_not_an_import() =>
        Assert.False(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, busy: true,
            Running(McdfOperationKind.Export), Pending()));

    [Fact]
    public void A_terminal_receipt_hands_the_footer_back_to_the_note() =>
        Assert.False(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, busy: true, Running(), Applied()));

    /// <summary>
    /// The uncancellable phases keep the row — the button GREYS instead of
    /// vanishing, matching the appearance pane's progress row — so the gate
    /// stays true and only the progress's own Cancellable flag drops.
    /// </summary>
    [Fact]
    public void Committing_still_shows_the_stop_so_the_view_can_grey_it()
    {
        var committing = Running(phase: McdfPhase.Committing, cancellable: false);
        Assert.True(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, busy: true, committing, Pending()));
        Assert.False(committing.Cancellable);
    }
}
