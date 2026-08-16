using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;

namespace Poser.ContractTests;

/// <summary>
/// Portable capture/apply build the whole-scene bone map ONCE per pass.
/// There is no injectable seam to count the build (SceneSnapshot deep-copies
/// every list), so this asserts the allocation envelope instead: rebuilding
/// the map per bone allocates a whole-scene dictionary for every bone —
/// well over 100 MB at this scene size — while the single build keeps the
/// whole pass in the low megabytes. The budget sits an order of magnitude
/// above the linear cost and an order of magnitude below the quadratic one,
/// so it is not timing-sensitive and fails only on the asymptotic regression.
/// </summary>
public sealed class PortableBoneMapContractTests
{
    private const int BoneCount = 1500;
    private const long PassAllocationBudget = 24L * 1024 * 1024;

    [Fact]
    public void Portable_capture_and_apply_stay_within_a_linear_allocation_envelope()
    {
        var targets = new TransformTargetId[BoneCount];
        for (int i = 0; i < BoneCount; i++)
            targets[i] = TestIds.BoneTarget(name: $"j_c_{i}", boneIndex: i + 1);
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBonesScene(
            TestIds.Actor(),
            targets.Select(target => target.Bone!.Value).ToArray()));
        foreach (var target in targets)
            app.Runtime.Seed(TestStates.At(target, 0, hasOverride: false));

        long beforeCapture = GC.GetAllocatedBytesForCurrentThread();
        var captured = app.PoseEdits.CapturePortable(targets);
        long captureAllocated =
            GC.GetAllocatedBytesForCurrentThread() - beforeCapture;

        Assert.True(captured.Success, captured.Detail);
        Assert.True(
            captureAllocated < PassAllocationBudget,
            $"CapturePortable allocated {captureAllocated:N0} bytes; "
            + $"budget {PassAllocationBudget:N0} — the scene bone map is "
            + "being rebuilt per bone again.");

        long beforeApply = GC.GetAllocatedBytesForCurrentThread();
        var applied = app.PoseEdits.ApplyPortable(
            targets, captured.Pose!, "portable map single build");
        long applyAllocated =
            GC.GetAllocatedBytesForCurrentThread() - beforeApply;

        Assert.True(applied.Success, applied.Detail);
        Assert.True(
            applyAllocated < PassAllocationBudget,
            $"ApplyPortable allocated {applyAllocated:N0} bytes; "
            + $"budget {PassAllocationBudget:N0} — the scene bone map is "
            + "being rebuilt per bone again.");
    }
}
