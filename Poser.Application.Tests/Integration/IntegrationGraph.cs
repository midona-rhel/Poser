using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;

namespace Poser.Application.Tests.Integration;

/// <summary>All four integration ports on one fake, so a single
/// DispatchProxy stands in for the resolver and the three vendor plugins.</summary>
public interface IIntegrationRuntimeFake
    : IIntegrationResolutionPort, IPenumbraPort, IGlamourerPort, ICustomizePlusPort
{
}

/// <summary>The integration owners wired as composition wires them.</summary>
internal sealed class IntegrationGraph : IDisposable
{
    public IntegrationGraph(IIntegrationRuntimeFake port, IMcdfFileBoundary files, ISessionGenerationSource sessions)
    {
        Ownership = new IntegrationOwnership(port, port);
        Mcdf = new McdfTransaction(port, port, port, port, files, sessions, Ownership);
        Selectors = new IntegrationSelectors(port, port, port, port, Ownership, Mcdf);
        Reset = new IntegrationReset(port, port, port, port, Ownership, Mcdf);
    }

    public IntegrationOwnership Ownership { get; }
    public McdfTransaction Mcdf { get; }
    public IntegrationSelectors Selectors { get; }
    public IntegrationReset Reset { get; }

    public void Dispose() => Reset.Dispose();
}
