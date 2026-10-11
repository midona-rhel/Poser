using System.Numerics;
using Poser.Application.Presentation;
using Poser.Domain;
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
    public void Clear_restores_capture_before_release_and_preserves_other_channels()
    {
        var port = new Port(); var session = new ActorPresentationSession(port);
        session.SetColor(Actor, AppearanceColorChannel.Skin, Vector4.Zero);
        session.SetColor(Actor, AppearanceColorChannel.Hair, new Vector4(0.3f));
        port.Values[AppearanceColorChannel.Skin] = new Vector4(0.8f);
        port.BeforeRestore = () => Assert.True(session.OverridesFor(Actor).Colors.ContainsKey(AppearanceColorChannel.Skin));
        Assert.True(session.ClearColor(Actor, AppearanceColorChannel.Skin).Success);
        Assert.Equal(Vector4.One, port.Values[AppearanceColorChannel.Skin]);
        Assert.Equal(new Vector4(0.3f), port.Values[AppearanceColorChannel.Hair]);
        Assert.False(session.OverridesFor(Actor).Colors.ContainsKey(AppearanceColorChannel.Skin));
        Assert.Equal(Vector4.One, session.OverridesFor(Actor).ColorCaptures[AppearanceColorChannel.Skin]);
        session.SetColor(Actor, AppearanceColorChannel.Skin, new Vector4(0.4f));
        Assert.True(session.ClearColor(Actor, AppearanceColorChannel.Skin).Success);
        Assert.Equal(Vector4.One, port.Values[AppearanceColorChannel.Skin]);
    }

    [Theory]
    [InlineData("foreign hold")]
    [InlineData("buffer unavailable")]
    [InlineData("generation replaced")]
    public void Refused_clear_retains_intent_capture_and_native_value(string reason)
    {
        var port = new Port(); var session = new ActorPresentationSession(port);
        session.SetColor(Actor, AppearanceColorChannel.Skin, Vector4.Zero);
        port.Refuse = true; port.Detail = reason;
        Assert.False(session.ClearColor(Actor, AppearanceColorChannel.Skin).Success);
        Assert.Equal(Vector4.Zero, port.Values[AppearanceColorChannel.Skin]);
        Assert.Equal(Vector4.Zero, session.OverridesFor(Actor).Colors[AppearanceColorChannel.Skin]);
        Assert.Equal(Vector4.One, session.OverridesFor(Actor).ColorCaptures[AppearanceColorChannel.Skin]);
        port.Refuse = false;
        Assert.True(session.ClearColor(Actor, AppearanceColorChannel.Skin).Success);
    }

    private sealed class Port : IPresentationRuntimePort
    {
        public bool Refuse;
        public string Detail = "refused";
        public Action? BeforeRestore;
        public readonly Dictionary<AppearanceColorChannel, Vector4> Values =
            Enum.GetValues<AppearanceColorChannel>().ToDictionary(channel => channel, _ => Vector4.One);
        public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> ReadColors(ActorId actor) =>
            IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Ok(new Dictionary<AppearanceColorChannel, Vector4>(Values));
        public Outcome SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value)
        {
            if (Refuse) return Outcome.Fail(Detail);
            Values[channel] = value; return Outcome.Ok();
        }
        public Outcome RestoreColor(ActorId actor, AppearanceColorChannel channel, Vector4 incoming)
        {
            if (Refuse) return Outcome.Fail(Detail);
            BeforeRestore?.Invoke();
            Values[channel] = incoming; return Outcome.Ok();
        }
        public Outcome RestoreColors(ActorId actor, IReadOnlyDictionary<AppearanceColorChannel, Vector4> captures) => Outcome.Ok();
        public bool IsSupported(ActorId actor) => true;
        public PresentationReading? Read(ActorId actor) => null;
        public Outcome SetOpacity(ActorId actor, float value) => Outcome.Ok();
        public Outcome RestoreOpacity(ActorId actor, float value) => Outcome.Ok();
        public Outcome SetTint(ActorId actor, PresentationModel model, Vector4 value) => Outcome.Ok();
        public Outcome RestoreTint(ActorId actor, PresentationModel model, Vector4 value) => Outcome.Ok();
        public Outcome SetWetness(ActorId actor, WetnessState value) => Outcome.Ok();
        public Outcome ClearWetness(ActorId actor, WetnessState value) => Outcome.Ok();
        public void ClearOwned(ActorId actor) { }
    }
}
