extern alias ProductionPoser;

using Poser.Application.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

public sealed class PoseThumbnailCacheContractTests
{
    [Fact]
    public void Library_import_cancel_is_owned_only_by_a_busy_mcdf_import()
    {
        var pending = OperationReceipt.Pending(
            Guid.NewGuid(), OperationEpoch.First, SessionGeneration.New(),
            ActorId.New());
        var applied = OperationReceipt.Applied(
            Guid.NewGuid(), OperationEpoch.First, SessionGeneration.New(),
            ActorId.New(), "done");
        var import = Progress(McdfOperationKind.Import);

        Assert.True(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, true, import, pending));
        foreach (var type in Enum.GetValues<PoseLibraryPane.LibraryType>())
        {
            if (type == PoseLibraryPane.LibraryType.Mcdf)
                continue;
            Assert.False(PoseLibraryPane.ShowsImportCancel(
                type, true, import, pending));
        }

        Assert.False(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, false, import, pending));
        Assert.False(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, true,
            Progress(McdfOperationKind.Export), pending));
        Assert.False(PoseLibraryPane.ShowsImportCancel(
            PoseLibraryPane.LibraryType.Mcdf, true, import, applied));
    }

    private static McdfProgress Progress(McdfOperationKind kind) =>
        new(ActorId.New(), "look.mcdf", kind, McdfPhase.Extracting,
            0, 0, 0, 0, true, null);
}
