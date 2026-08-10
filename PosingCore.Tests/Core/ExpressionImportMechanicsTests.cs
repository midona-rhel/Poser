using System.Numerics;
using Poser.Core;
using Poser.Files;

namespace Poser.Tests.Core;

/// <summary>
/// The stack mechanics Brio's expression dance depends on: import writes
/// are forceNewStack (PoseImporter.cs passes true on every call), so the
/// head restore's RemoveLastStack pops EXACTLY the phase-1 head write, and
/// the expression scope includes the head plus every ExpressionOptions
/// category member.
/// </summary>
public class ExpressionImportMechanicsTests
{
    private static readonly Transform Basis = new(
        new Vector3(0f, 1.5f, 0f),
        Quaternion.Identity,
        Vector3.One);

    [Fact]
    public void ForceNewStack_AppendsInsteadOfCombining()
    {
        var info = new BonePoseInfo("j_kao", 0);
        var first = new Transform(
            new Vector3(0f, 1.5f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f),
            Vector3.One);
        var second = new Transform(
            new Vector3(0.1f, 1.6f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f),
            Vector3.One);

        Assert.NotNull(info.Apply(first, Basis,
            TransformComponents.All, TransformComponents.All, forceNewStack: true));
        Assert.NotNull(info.Apply(second, first,
            TransformComponents.All, TransformComponents.All, forceNewStack: true));

        Assert.Equal(2, info.Stacks.Count);
    }

    [Fact]
    public void RemoveLastInteractiveStack_UndoesExactlyTheLastForcedWrite()
    {
        var info = new BonePoseInfo("j_kao", 0);
        var userEdit = new Transform(
            new Vector3(0f, 1.5f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f),
            Vector3.One);
        // The user's own edit (default combine path).
        Assert.NotNull(info.Apply(userEdit, Basis));
        var beforeImport = info.Stacks[^1].Transform;

        // Phase 1: the file's head, forced onto its own stack.
        var fileHead = new Transform(
            new Vector3(0.2f, 1.7f, 0.1f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f),
            Vector3.One);
        Assert.NotNull(info.Apply(fileHead, userEdit,
            TransformComponents.All, TransformComponents.All, forceNewStack: true));
        Assert.Equal(2, info.Stacks.Count);

        // Phase 2's pop: the user's stack survives byte-identical.
        Assert.True(info.RemoveLastInteractiveStack());
        Assert.Single(info.Stacks);
        Assert.Equal(beforeImport, info.Stacks[0].Transform);
    }

    [Fact]
    public void RemoveLastInteractiveStack_LeavesNamedLayersAlone()
    {
        var info = new BonePoseInfo("j_f_eye_l", 1);
        Assert.NotNull(info.Apply(
            new Transform(new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.One),
            Transform.Identity,
            TransformComponents.All, TransformComponents.All, forceNewStack: true));
        Assert.True(info.SetLayerTransform(
            "expression", Transform.Zero, TransformComponents.All));

        Assert.True(info.RemoveLastInteractiveStack());

        // The named layer is the sole survivor.
        Assert.Single(info.Stacks);
        Assert.Equal("expression", info.Stacks[0].Layer);
        Assert.False(info.RemoveLastInteractiveStack());
    }

    [Theory]
    // Brio ExpressionOptions categories: head, ears, hair, face, eyes,
    // lips, jaw (PosingService.cs:77-86), evaluated through the shipped
    // BoneCategories.json prefixes exactly as its BoneFilter does.
    [InlineData("j_kao", true)]            // head — the dance depends on it
    [InlineData("j_f_eye_l", true)]        // eyes
    [InlineData("j_f_ulip_01_r", true)]    // lips
    [InlineData("j_f_ago", true)]          // jaw
    [InlineData("j_f_bero_01", true)]      // jaw (Dawntrail tongue)
    // "j_ago" is the LEGACY row, which ExpressionOptions leaves disabled — it
    // only looked in-scope while the scope was approximated from a j_f_/j_ago
    // name test instead of read off the catalog. Same for the "ex" row and
    // the legacy j_f_ names: Brio enables neither for an expression.
    [InlineData("j_ago", false)]
    [InlineData("j_f_noanim_ago", false)]
    [InlineData("j_f_memoto", false)]
    [InlineData("j_mimi_l", true)]         // ears
    [InlineData("j_zera_a_l", true)]       // ears (Viera)
    [InlineData("n_ear_b_r", true)]        // ears (accessory)
    [InlineData("j_kami_a", true)]         // hair
    [InlineData("j_ex_h0104_ke_a", true)]  // hair (ex strands)
    [InlineData("j_sebo_a", false)]        // spine — body stays put
    [InlineData("j_ude_a_l", false)]       // arm
    [InlineData("n_hara", false)]          // abdomen
    [InlineData("n_throw", false)]
    public void ExpressionScope_MatchesBrioExpressionOptions(string bone, bool inScope)
    {
        Assert.Equal(inScope, PoseFileService.IsExpressionScopeBone(bone));
    }
}
