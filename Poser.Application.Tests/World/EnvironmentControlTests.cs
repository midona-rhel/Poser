using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain.Scene;
using Poser.Files;

namespace Poser.Application.Tests.World;

public sealed class EnvironmentControlTests
{
    [Fact]
    public void File_application_preserves_clock_order_and_replays_as_one_environment_step()
    {
        var runtime = new Runtime { MinuteOfDay = 120, DayOfMonth = 4, IsTimeFrozen = false };
        runtime.SetWeather(2, 0.5f);
        runtime.Sky = new();
        var history = new TransformHistory();
        var control = new EnvironmentControl(new(history), runtime, runtime, runtime);
        var target = new SceneEnvironment
        {
            MinuteOfDay = 960, DayOfMonth = 7, IsTimeFrozen = false,
            WeatherId = 3, IsWeatherOverrideEnabled = true,
            TransitionTime = 2, HeldSections = [EnvSection.Wind], Wind = new(),
        };
        control.Apply(target);
        Assert.Equal(960, runtime.MinuteOfDay);
        Assert.False(runtime.IsTimeFrozen);
        Assert.False(runtime.IsSectionHeld(EnvSection.Sky));
        Assert.True(runtime.IsSectionHeld(EnvSection.Wind));
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        history.CommitUndo(step);
        Assert.Equal(120, runtime.MinuteOfDay);
        Assert.False(runtime.IsTimeFrozen);
        Assert.Equal(2u, runtime.CurrentWeatherId);
        Assert.True(runtime.IsSectionHeld(EnvSection.Sky));
        Assert.False(history.CanUndo);
        Assert.True(step.Redo());
        history.CommitRedo(step);
        Assert.Equal(960, runtime.MinuteOfDay);
        Assert.False(runtime.IsTimeFrozen);
        Assert.Equal(3u, runtime.CurrentWeatherId);
    }

    [Fact]
    public void Shared_gesture_coalesces_without_a_panel_and_readings_are_detached()
    {
        var runtime = new Runtime();
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var control = new EnvironmentControl(journal, runtime, runtime, runtime);
        var before = control.Read();
        journal.BeginEdit("wind");
        control.SetWind(new() { Speed = 2 });
        control.SetWind(new() { Speed = 5 });
        journal.EndEdit();
        control.Seal();
        runtime.Weathers.Clear();
        Assert.Equal(2, before.AllWeathers.Count);
        Assert.Empty(before.HeldSections);
        Assert.Equal(5, control.Read().Wind.Speed);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        history.CommitUndo(step);
        Assert.Equal(0, runtime.Wind.Speed);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Scene_transaction_uses_the_same_application_without_a_second_history_entry()
    {
        var runtime = new Runtime();
        var history = new TransformHistory();
        var control = new EnvironmentControl(new(history), runtime, runtime, runtime);
        control.Apply(new() { MinuteOfDay = 300, DayOfMonth = 1 }, recordHistory: false);
        Assert.Equal(300, runtime.MinuteOfDay);
        Assert.False(history.CanUndo);
    }

    private sealed class Runtime : IEnvironmentRuntimePort, IWorldRenderingRuntimePort, IFestivalRuntimePort
    {
        private int _minute;
        private int _day = 1;
        private readonly HashSet<EnvSection> _held = [];
        public List<WeatherInfo> Weathers { get; } = [new(2, "Clear", 0), new(3, "Rain", 0)];
        public int MinuteOfDay { get => _minute; set { _minute = value; IsTimeFrozen = true; } }
        public int DayOfMonth { get => _day; set { _day = value; IsTimeFrozen = true; } }
        public bool IsTimeFrozen { get; set; }
        public bool IsTimeFreezeAvailable => true;
        public bool ResetTimeOnGPoseExit { get; set; }
        public bool IsWeatherOverrideEnabled { get; set; }
        public bool IsWeatherOverrideAvailable => true;
        public uint CurrentWeatherId { get; private set; }
        public float TransitionTime { get; set; }
        public void SetWeather(uint id, float transitionTime = 0.5f)
        { CurrentWeatherId = id; TransitionTime = transitionTime; IsWeatherOverrideEnabled = true; }
        public IReadOnlyList<WeatherInfo> TerritoryWeathers => Weathers;
        public IReadOnlyList<WeatherInfo> AllWeathers => Weathers;
        public WeatherInfo? GetWeatherInfo(uint id) => Weathers.FirstOrDefault(w => w.Id == id);
        public bool ResetWeatherOnGPoseExit { get; set; }
        public bool IsSectionHoldAvailable => true;
        public bool IsSectionHeld(EnvSection section) => _held.Contains(section);
        public void SetSectionHeld(EnvSection section, bool held) { if (held) _held.Add(section); else _held.Remove(section); }
        public void ReleaseAllSections() => _held.Clear();
        public bool ResetSectionsOnGPoseExit { get; set; }
        private EnvSkyValues _sky;
        public EnvSkyValues Sky { get => _sky; set { _sky = value; _held.Add(EnvSection.Sky); } }
        private EnvCloudsValues _clouds;
        public EnvCloudsValues Clouds { get => _clouds; set { _clouds = value; _held.Add(EnvSection.Clouds); } }
        private EnvLightingValues _lighting;
        public EnvLightingValues Lighting { get => _lighting; set { _lighting = value; _held.Add(EnvSection.Lighting); } }
        private EnvFogValues _fog;
        public EnvFogValues Fog { get => _fog; set { _fog = value; _held.Add(EnvSection.Fog); } }
        private EnvRainValues _rain;
        public EnvRainValues Rain { get => _rain; set { _rain = value; _held.Add(EnvSection.Rain); } }
        private EnvParticlesValues _particles;
        public EnvParticlesValues Particles { get => _particles; set { _particles = value; _held.Add(EnvSection.Particles); } }
        private EnvStarsValues _stars;
        public EnvStarsValues Stars { get => _stars; set { _stars = value; _held.Add(EnvSection.Stars); } }
        private EnvWindValues _wind;
        public EnvWindValues Wind { get => _wind; set { _wind = value; _held.Add(EnvSection.Wind); } }
        public bool IsWaterFrozen { get; set; }
        public bool IsWaterFreezeAvailable => true;
        public bool ResetWaterOnGPoseExit { get; set; }
        public IReadOnlyList<ActiveFestival> ActiveFestivals => [];
        public IReadOnlyDictionary<uint, FestivalEntry> FestivalList => new Dictionary<uint, FestivalEntry>();
        public bool HasFreeSlot => true;
        public bool HasOverride => false;
        public bool CanModify => true;
        public bool Add(uint id, ushort phase = 1) => true;
        public bool Remove(uint id) => true;
        public bool ChangePhase(uint id, ushort phase) => true;
        public void Reset() { }
    }
}
