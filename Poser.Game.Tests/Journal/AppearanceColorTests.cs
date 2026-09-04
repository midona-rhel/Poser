using System.Numerics;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Game.Presentation;

namespace Poser.Game.Tests.Journal;

public sealed class AppearanceColorTests
{
    [Fact]
    public void Native_channel_writes_preserve_other_colours_and_non_RGB_lanes()
    {
        var data = new FFXIVClientStructs.FFXIV.Shader.CustomizeParameter();
        data.SkinColor.W = 0.7f;
        data.LeftColor.W = 0.8f;
        data.RightColor.W = 0.9f;
        foreach (var channel in Enum.GetValues<AppearanceColorChannel>())
            PresentationRuntimePort.WriteColor(ref data, channel, new Vector4(0.5f, 0.25f, -0.5f, 0.3f));
        PresentationRuntimePort.WriteColor(ref data, AppearanceColorChannel.Hair, Vector4.One);
        Assert.Equal(0.7f, data.SkinColor.W);
        Assert.Equal(0.8f, data.LeftColor.W);
        Assert.Equal(0.9f, data.RightColor.W);
        Assert.Equal(0.3f, data.LipColor.W);
        Assert.Equal(0.25f, data.SkinColor.X);
        Assert.Equal(-0.25f, data.OptionColor.Z);
        Assert.Equal(1f, data.MainColor.X);
        Assert.Equal(0.25f, data.MeshColor.X);
    }
    private static readonly ActorId Actor = new(Guid.NewGuid(), 1);

    [Fact]
    public void Signed_RGB_round_trips_and_mouth_alpha_stays_linear()
    {
        var value = new Vector4(-0.5f, 0.25f, 2f, 0.3f);
        var shader = AppearanceColorSpace.ToShader(value);
        Assert.Equal(new Vector4(-0.25f, 0.0625f, 4f, 0.3f), shader);
        Assert.Equal(value, AppearanceColorSpace.FromShader(shader));
    }

    [Fact]
    public void Read_does_not_claim_and_failed_set_does_not_capture()
    {
        var port = new Port { Refuse = true };
        var session = new ActorPresentationSession(port);
        Assert.True(session.ReadColors(Actor).Success);
        Assert.False(session.OverridesFor(Actor).HasAny);
        Assert.False(session.SetColor(Actor, AppearanceColorChannel.Skin, Vector4.Zero).Success);
        Assert.False(session.OverridesFor(Actor).HasAny);
    }

    [Fact]
    public void Failed_clear_retains_intent_and_success_keeps_original_capture_and_other_channels()
    {
        var port = new Port(); var session = new ActorPresentationSession(port);
        session.SetColor(Actor, AppearanceColorChannel.Skin, Vector4.Zero);
        session.SetColor(Actor, AppearanceColorChannel.Hair, Vector4.Zero);
        int callbacks = 0;
        session.BeginClearColor(Actor, AppearanceColorChannel.Skin, Apply, _ => callbacks++);
        Assert.False(session.SetColor(Actor, AppearanceColorChannel.Mouth, Vector4.One).Success);
        port.Done!(PresentationPortResult.Fail("foreign hold"));
        Assert.True(session.OverridesFor(Actor).Colors.ContainsKey(AppearanceColorChannel.Skin));
        session.BeginClearColor(Actor, AppearanceColorChannel.Skin, Apply, _ => callbacks++);
        port.Land(); port.Land();
        Assert.Equal(2, callbacks);
        var owned = session.OverridesFor(Actor);
        Assert.False(owned.Colors.ContainsKey(AppearanceColorChannel.Skin));
        Assert.True(owned.Colors.ContainsKey(AppearanceColorChannel.Hair));
        Assert.Equal(Vector4.One, owned.ColorCaptures[AppearanceColorChannel.Skin]);
    }

    [Fact]
    public void Reset_invalidates_old_completion_before_restoring_capture()
    {
        var port = new Port(); var session = new ActorPresentationSession(port);
        session.SetColor(Actor, AppearanceColorChannel.Skin, Vector4.Zero);
        session.BeginClearColor(Actor, AppearanceColorChannel.Skin, Apply, _ => { });
        var oldCommit = port.Commit!;
        Assert.True(session.ResetActor(Actor).Success);
        session.SetColor(Actor, AppearanceColorChannel.Skin, new Vector4(0.5f));
        bool wrote = false;
        Assert.False(oldCommit(() => wrote = true).Success);
        Assert.False(wrote);
        Assert.Equal(new Vector4(0.5f), session.OverridesFor(Actor).Colors[AppearanceColorChannel.Skin]);
    }

    [Fact]
    public void Readiness_requires_event_then_later_readable_frame_and_is_bounded()
    {
        long now = 0;
        var readiness = new ColorRedrawReadiness(() => now);
        Assert.False(readiness.IsReady(10, true));
        readiness.Redrawn(10);
        Assert.False(readiness.IsReady(10, true));
        Assert.False(readiness.IsReady(11, false));
        readiness.Redrawn(11);
        Assert.True(readiness.IsReady(11, true));
        now = 5000;
        Assert.True(readiness.IsExpired);
    }

    private static PresentationPortResult Apply(Action mutation) { mutation(); return PresentationPortResult.Ok(); }

    private sealed class Port : IPresentationRuntimePort
    {
        public bool Refuse;
        public Func<Action, PresentationPortResult>? Commit;
        public Action<PresentationPortResult>? Done;
        public void Land() => Done!(Commit!(() => { }));
        public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> ReadColors(ActorId actor) =>
            IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Ok(
                Enum.GetValues<AppearanceColorChannel>().ToDictionary(channel => channel, _ => Vector4.One));
        public PresentationPortResult SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value) =>
            Refuse ? PresentationPortResult.Fail("refused") : PresentationPortResult.Ok();
        public void BeginClearColor(ActorId actor, AppearanceColorChannel channel,
            Func<Action, PresentationPortResult> commit, Action<PresentationPortResult> completed)
        { Commit = commit; Done = completed; }
        public void SuspendColors(ActorId actor) => Done?.Invoke(PresentationPortResult.Fail("reset"));
        public PresentationPortResult RestoreColors(ActorId actor, IReadOnlyDictionary<AppearanceColorChannel, Vector4> captures) => PresentationPortResult.Ok();
        public bool IsSupported(ActorId actor) => true;
        public PresentationReading? Read(ActorId actor) => null;
        public PresentationPortResult SetOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public PresentationPortResult RestoreOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public PresentationPortResult SetTint(ActorId actor, PresentationModel model, Vector4 value) => PresentationPortResult.Ok();
        public PresentationPortResult RestoreTint(ActorId actor, PresentationModel model, Vector4 value) => PresentationPortResult.Ok();
        public PresentationPortResult SetWetness(ActorId actor, WetnessState value) => PresentationPortResult.Ok();
        public PresentationPortResult ClearWetness(ActorId actor, WetnessState value) => PresentationPortResult.Ok();
        public void ClearOwned(ActorId actor) { }
    }
}
