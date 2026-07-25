using System;
using Poser.Domain.Posing;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Native Havok IK solving. Every solve fully re-initializes the shared
/// native buffers from the request, so no gain, index, axis, limit,
/// enforcement, or target can leak from a previously solved chain.
/// </summary>
public interface IIKService : IDisposable
{
    /// <summary>Solves the endpoint's chain toward the request. A failed or
    /// unavailable solver is a no-op.</summary>
    void Solve(IBone endpoint, in IkSolveRequest request);
}
