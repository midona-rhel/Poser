using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// The scene's one environment: time, weather, the eight holdable environment
/// sections, water rendering, and the festival slots. The pane owns its
/// collapse state and its two picker surfaces; every value is read live from
/// the services at DISPATCH time and written straight back, so a row never acts
/// on the copy its section opened with.
///
/// <para>INVERSION BOUNDARY. The services state every hold positively: a HELD
/// thing is one Poser keeps stamping. Time and weather are presented the same
/// way round ("Freeze time", "Hold weather"), and the eight section switches
/// are presented INVERTED — on means the game runs that section naturally,
/// which is the service's hold turned off. Every inversion happens at the call
/// site that needs it and nowhere else.</para>
/// </summary>
public sealed class EnvironmentPane
{
    private readonly IEnvironmentService _environment;
    private readonly IWorldRenderingService _rendering;
    private readonly IFestivalService _festivals;
    private readonly ITextureProvider _textures;

    private const string TimeUnavailable =
        "Poser could not hook the game clock, so the time cannot be held";
    private const string WeatherUnavailable =
        "Poser could not hook the weather update, so the weather cannot be held";
    private const string HoldUnavailable =
        "Poser could not hook the environment update, so sections cannot be held";
    private const string WaterUnavailable =
        "Poser could not hook the water renderer, so the surface cannot be frozen";
    private const string FestivalsUnavailable =
        "Festivals can only be changed in GPose";

    private string _status = string.Empty;

    private bool _openTime = true;
    private bool _openWeather = true;
    private bool _openSky;
    private bool _openLighting;
    private bool _openFog;
    private bool _openRain;
    private bool _openParticles;
    private bool _openStars;
    private bool _openWind;
    private bool _openRendering;
    private bool _openFestivals = true;

    /// <summary>Widens the weather surface from this territory's own weathers
    /// to every named weather. Read by the picker's query, so flipping it while
    /// the surface is open re-answers it on the same frame.</summary>
    private bool _showAllWeathers;

    private readonly Crystarium.SearchPicker<WeatherOption> _weatherPicker =
        new("environment-weather");
    private readonly Crystarium.SearchPicker<FestivalEntry> _festivalPicker =
        new("environment-festival");

    /// <summary>One option object per weather id, for the pane's life: the
    /// picker keys its rows by identity string, and a fresh object per frame
    /// would hand every row a new ImGui identity.</summary>
    private readonly Dictionary<uint, WeatherOption> _weatherOptions = new();

    /// <summary>The weather list the open surface is showing, refilled in
    /// place — the query runs once per frame while the popover is up.</summary>
    private readonly List<WeatherOption> _weatherVisible = new();

    /// <summary>The festival rows, ordered once: the dictionary the service
    /// exposes carries no order and the surface needs a stable one.</summary>
    private List<FestivalEntry>? _festivalEntries;

    private readonly Dictionary<uint, string> _idText = new();

    /// <summary>Sheet icon ids are not guaranteed to exist and the game icon
    /// lookup THROWS for those, so a failure is remembered: an exception per
    /// row per frame is a frame-rate cliff.</summary>
    private readonly HashSet<uint> _missingIcons = new();

    private readonly Func<WeatherOption, string> _weatherName =
        static option => option.Name;
    private readonly Func<WeatherOption, string> _weatherKey =
        static option => option.Key;
    private readonly Func<FestivalEntry, string> _festivalName =
        static entry => entry.Name;

    private static readonly Func<FestivalEntry, TablerIcon?> FestivalGlyph =
        static entry =>
            entry.Unsafe ? TablerIcon.AlertTriangle : TablerIcon.Star;

    private readonly Func<string, IReadOnlyList<WeatherOption>> _weatherQuery;
    private readonly Func<WeatherOption, nint> _weatherTexture;
    private readonly Func<WeatherOption, string?> _weatherBadge;
    private readonly Func<FestivalEntry, string> _festivalKey;
    private readonly Func<FestivalEntry, string?> _festivalBadge;

    /// <summary>One weather as the picker's row: the sheet's name and icon,
    /// plus the row identity minted with it.</summary>
    private sealed record WeatherOption(
        uint Id, string Name, uint IconId, string Key);

    public EnvironmentPane(
        IEnvironmentService environment,
        IWorldRenderingService rendering,
        IFestivalService festivals,
        ITextureProvider textures)
    {
        _environment = environment;
        _rendering = rendering;
        _festivals = festivals;
        _textures = textures;
        _weatherQuery = WeatherResults;
        _weatherTexture = option => ResolveIcon(option.IconId);
        _weatherBadge = option => IdText(option.Id);
        _festivalKey = entry => IdText(entry.Id);
        _festivalBadge = entry => IdText(entry.Id);
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        DrainPickers();

        Crystarium.Page("environment", origin, size, page =>
        {
            page.Status(_status);

            // The rule is a divider BETWEEN sections, so the page's first
            // section draws neither the rule nor the margin above it.
            page.Section("TIME", _openTime, next => _openTime = next,
                TimeRows, divider: false);
            page.Section("WEATHER", _openWeather, next => _openWeather = next,
                WeatherRows);
            page.Section("SKY", _openSky, next => _openSky = next, SkyRows);
            page.Section("LIGHTING", _openLighting,
                next => _openLighting = next, LightingRows);
            page.Section("FOG", _openFog, next => _openFog = next, FogRows);
            page.Section("RAIN", _openRain, next => _openRain = next, RainRows);
            page.Section("PARTICLES", _openParticles,
                next => _openParticles = next, ParticleRows);
            page.Section("STARS", _openStars, next => _openStars = next,
                StarRows);
            page.Section("WIND", _openWind, next => _openWind = next, WindRows);
            page.Section("RENDERING", _openRendering,
                next => _openRendering = next, RenderingRows);
            page.Section("FESTIVALS", _openFestivals,
                next => _openFestivals = next, FestivalRows);
        });
    }

    /// <summary>Both surfaces are drained at the top of the frame, exactly
    /// where the rows that opened them can still report the outcome.</summary>
    private void DrainPickers()
    {
        if (_weatherPicker.Draw() is { } weather)
        {
            // Picking a weather turns the hold on inside the service: without
            // it the game reverts the pick on its next weather update.
            _environment.SetWeather(
                weather.Item.Id, _environment.TransitionTime);
            _status = string.Empty;
        }

        if (_festivalPicker.Draw() is { } festival)
        {
            _status = _festivals.Add(festival.Item.Id)
                ? string.Empty
                : $"{festival.Item.Name}: this festival cannot be set where you are standing.";
        }
    }

    // ── time ─────────────────────────────────────────────────────────────

    private void TimeRows(Crystarium.FormScope form)
    {
        bool available = _environment.IsTimeFreezeAvailable;
        int minute = _environment.MinuteOfDay;

        form.ReadOnly("Clock", ClockText(minute),
            help: "The Eorzean time the game is showing",
            unavailable: !available);
        form.Slider("Time of day", minute, 0f, 1439f,
            value => _environment.MinuteOfDay = (int)MathF.Round(value),
            format: "0",
            help: "Set the Eorzean minute of day. Moving it freezes the "
                + "clock — an unfrozen clock discards the write within a frame.",
            disabled: !available);
        form.Slider("Day of month", _environment.DayOfMonth, 1f, 31f,
            value => _environment.DayOfMonth = (int)MathF.Round(value),
            format: "0",
            help: "Set the Eorzean day of the month. Moving it freezes the "
                + "clock, exactly as the time does.",
            disabled: !available);
        form.Switch("Freeze time", _environment.IsTimeFrozen,
            value => _environment.IsTimeFrozen = value,
            help: available
                ? "Stop the Eorzean clock where it stands"
                : TimeUnavailable,
            disabled: !available);
        form.Switch("Restore time on exit", _environment.ResetTimeOnGPoseExit,
            value => _environment.ResetTimeOnGPoseExit = value,
            help: "Hand the clock back to the game when GPose ends");
    }

    // ── weather ──────────────────────────────────────────────────────────

    private void WeatherRows(Crystarium.FormScope form)
    {
        bool available = _environment.IsWeatherOverrideAvailable;
        uint current = _environment.CurrentWeatherId;
        string name = _environment.GetWeatherInfo(current) is { } info
            ? info.Name
            : IdText(current);

        form.Picker("Weather", name, OpenWeatherPicker,
            help: "Choose the weather this territory runs. Picking one holds "
                + "it, or the game's next update takes it back.");
        form.Switch("Show all weathers", _showAllWeathers,
            value => _showAllWeathers = value,
            help: "Widen the list from this territory's own weathers to every "
                + "weather in the game");
        form.Slider("Transition", _environment.TransitionTime, 0f, 10f,
            value => _environment.TransitionTime = value,
            help: "How long the game blends into a picked weather, in seconds");
        form.Switch("Hold weather", _environment.IsWeatherOverrideEnabled,
            value => _environment.IsWeatherOverrideEnabled = value,
            help: available
                ? "Keep the current weather by suppressing the game's own "
                    + "territory weather update"
                : WeatherUnavailable,
            disabled: !available);
        form.Switch("Restore weather on exit",
            _environment.ResetWeatherOnGPoseExit,
            value => _environment.ResetWeatherOnGPoseExit = value,
            help: "Hand the weather back to the game when GPose ends");
    }

    /// <summary>Arms the weather surface. The trigger button is the last
    /// reserved item, which is what the picker anchors to.</summary>
    private void OpenWeatherPicker()
    {
        uint current = _environment.CurrentWeatherId;
        _weatherPicker.Open(
            "Weather",
            Array.Empty<WeatherOption>(),
            _weatherName,
            _weatherKey,
            IdText(current),
            null,
            new PickerOptions<WeatherOption>
            {
                // The list is the caller's, because the "show all" switch
                // widens it while the surface is open.
                Query = _weatherQuery,
                Texture = _weatherTexture,
                Badge = _weatherBadge,
                Glyph = _ => TablerIcon.Sun,
            });
    }

    /// <summary>The visible weathers: the territory's own or every named one,
    /// narrowed by the field's query. Refilled in place — the open surface asks
    /// for it every frame.</summary>
    private IReadOnlyList<WeatherOption> WeatherResults(string query)
    {
        var source = _showAllWeathers
            ? _environment.AllWeathers
            : _environment.TerritoryWeathers;
        _weatherVisible.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            var weather = source[i];
            if (query.Length > 0
                && !weather.Name.Contains(
                    query, StringComparison.OrdinalIgnoreCase)
                && !IdText(weather.Id).Contains(
                    query, StringComparison.Ordinal))
                continue;
            _weatherVisible.Add(Option(weather));
        }
        return _weatherVisible;
    }

    private WeatherOption Option(WeatherInfo weather)
    {
        if (_weatherOptions.TryGetValue(weather.Id, out var existing))
            return existing;
        var option = new WeatherOption(
            weather.Id, weather.Name, weather.IconId, IdText(weather.Id));
        _weatherOptions[weather.Id] = option;
        return option;
    }

    // ── sky and clouds ───────────────────────────────────────────────────

    private void SkyRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural sky", EnvSection.Sky,
            "Let the game run the skybox. Changing any sky value below holds "
                + "it for Poser.");
        var sky = _environment.Sky;
        form.NumericSlider("Sky texture", sky.SkyTextureId, 0f, 450f,
            value => _environment.Sky =
                _environment.Sky with { SkyTextureId = ToId(value) },
            perPixel: 0.25f,
            format: "0",
            help: "The skybox texture the zone draws");
        form.Slider("Sun visibility", sky.SunVisibility, 0f, 1f,
            value => _environment.Sky =
                _environment.Sky with { SunVisibility = value },
            help: "How much of the sun disc shows through the sky");

        SectionSwitch(form, "Natural clouds", EnvSection.Clouds,
            "Let the game run the clouds. Changing any cloud value below "
                + "holds them for Poser.");
        var clouds = _environment.Clouds;
        form.NumericSlider("Cloud texture", clouds.CloudTexture, 0f, 75f,
            value => _environment.Clouds =
                _environment.Clouds with { CloudTexture = ToId(value) },
            perPixel: 0.25f,
            format: "0",
            help: "The texture the overhead cloud layer draws");
        form.NumericSlider(
            "Cloud side texture", clouds.CloudSideTexture, 0f, 75f,
            value => _environment.Clouds =
                _environment.Clouds with { CloudSideTexture = ToId(value) },
            perPixel: 0.25f,
            format: "0",
            help: "The texture the horizon cloud band draws");
        form.ColorWells("Cloud colours", wells =>
        {
            wells.Well("Top", Opaque(clouds.CloudColor1),
                value => _environment.Clouds =
                    _environment.Clouds with { CloudColor1 = Rgb(value) });
            wells.Well("Side", Opaque(clouds.CloudColor2),
                value => _environment.Clouds =
                    _environment.Clouds with { CloudColor2 = Rgb(value) });
        }, help: "Tint the overhead clouds and the horizon band");
        form.Slider("Shadow stop", clouds.ShadowStop, 0f, 2f,
            value => _environment.Clouds =
                _environment.Clouds with { ShadowStop = value },
            help: "Where the cloud shading gradient ends");
        form.Slider("Cloud height", clouds.CloudHeight, 0f, 2f,
            value => _environment.Clouds =
                _environment.Clouds with { CloudHeight = value },
            help: "How tall the horizon cloud band stands");
    }

    // ── lighting ─────────────────────────────────────────────────────────

    private void LightingRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural lighting", EnvSection.Lighting,
            "Let the game run the ambient lighting. Changing any value below "
                + "holds it for Poser.");
        var lighting = _environment.Lighting;
        form.ColorWells("Colours", wells =>
        {
            wells.Well("Sun", Opaque(lighting.SunlightColor),
                value => _environment.Lighting =
                    _environment.Lighting with { SunlightColor = Rgb(value) });
            wells.Well("Moon", Opaque(lighting.MoonlightColor),
                value => _environment.Lighting =
                    _environment.Lighting with { MoonlightColor = Rgb(value) });
            wells.Well("Ambient", Opaque(lighting.AmbientColor),
                value => _environment.Lighting =
                    _environment.Lighting with { AmbientColor = Rgb(value) });
        }, help: "The three lights the zone lights everything with");
        form.Slider("Saturation", lighting.AmbientSaturation, 0f, 5f,
            value => _environment.Lighting =
                _environment.Lighting with { AmbientSaturation = value },
            help: "How colourful the ambient light is");
        form.Slider("Temperature", lighting.AmbientTemperature, -2.5f, 2.5f,
            value => _environment.Lighting =
                _environment.Lighting with { AmbientTemperature = value },
            help: "How warm or cold the ambient light is");
        form.Slider("Light distance", lighting.LightDistance, 0f, 100f,
            value => _environment.Lighting =
                _environment.Lighting with { LightDistance = value },
            help: "How far the zone's lighting reaches");
        // The reference UIs draw these three unidentified members rather than
        // hide them; they keep their reference names until someone names them.
        form.Slider("Unknown 1", lighting.Unknown1, 0f, 10f,
            value => _environment.Lighting =
                _environment.Lighting with { Unknown1 = value },
            help: "An unidentified lighting value the references still expose");
        form.Slider("Unknown 2", lighting.Unknown2, 0f, 100f,
            value => _environment.Lighting =
                _environment.Lighting with { Unknown2 = value },
            help: "An unidentified lighting value the references still expose");
        form.Slider("Unknown 4", lighting.Unknown4, 0f, 1f,
            value => _environment.Lighting =
                _environment.Lighting with { Unknown4 = value },
            help: "An unidentified lighting value the references still expose");
    }

    // ── fog ──────────────────────────────────────────────────────────────

    private void FogRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural fog", EnvSection.Fog,
            "Let the game run the fog. Changing any value below holds it for "
                + "Poser.");
        var fog = _environment.Fog;
        form.ColorWells("Colour", wells =>
        {
            wells.Well("Fog", fog.Color with { W = 1f },
                value => _environment.Fog = _environment.Fog with
                {
                    Color = Rgb(value, _environment.Fog.Color.W),
                });
        }, help: "The colour the fog washes the distance with");
        // The colour well edits RGB only, so the fog colour's own alpha — which
        // both references edit — takes the row beside it.
        form.Slider("Colour alpha", fog.Color.W, 0f, 1f,
            value => _environment.Fog = _environment.Fog with
            {
                Color = _environment.Fog.Color with { W = value },
            },
            help: "How strongly the fog colour applies");
        form.Slider("Distance", fog.Distance, 0f, 1000f,
            value => _environment.Fog =
                _environment.Fog with { Distance = value },
            format: "0",
            help: "How far away the fog starts");
        form.Slider("Thickness", fog.Thickness, 0f, 50f,
            value => _environment.Fog =
                _environment.Fog with { Thickness = value },
            help: "How dense the fog is once it starts");
        form.Slider("Fog opacity", fog.FogOpacity, 0f, 10f,
            value => _environment.Fog =
                _environment.Fog with { FogOpacity = value },
            help: "How much the fog hides what is behind it");
        form.Slider("Sky opacity", fog.SkyOpacity, 0f, 10f,
            value => _environment.Fog =
                _environment.Fog with { SkyOpacity = value },
            help: "How much of the fog reaches the sky");
        form.Slider("Sky smoothness", fog.SkySmoothness, 0f, 1000f,
            value => _environment.Fog =
                _environment.Fog with { SkySmoothness = value },
            format: "0",
            help: "How gradually the fog blends into the sky");
    }

    // ── rain ─────────────────────────────────────────────────────────────

    private void RainRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural rain", EnvSection.Rain,
            "Let the game run the rain. Changing any value below holds it for "
                + "Poser.");
        var rain = _environment.Rain;
        form.Slider("Intensity", rain.Intensity, 0f, 1f,
            value => _environment.Rain =
                _environment.Rain with { Intensity = value },
            help: "How hard it rains");
        form.Slider("Line thickness", rain.Size, 0f, 1f,
            value => _environment.Rain = _environment.Rain with { Size = value },
            help: "How thick a single rain line draws");
        form.ColorWells("Colour", wells =>
        {
            wells.Well("Rain", rain.Color with { W = 1f },
                value => _environment.Rain = _environment.Rain with
                {
                    Color = Rgb(value, _environment.Rain.Color.W),
                });
        }, help: "The colour the rain lines draw in");
        form.Slider("Colour alpha", rain.Color.W, 0f, 1f,
            value => _environment.Rain = _environment.Rain with
            {
                Color = _environment.Rain.Color with { W = value },
            },
            help: "How strongly the rain colour applies");
        form.Slider("Weight", rain.Weight, 0f, 10f,
            value => _environment.Rain =
                _environment.Rain with { Weight = value },
            help: "How fast the rain falls");
        form.Slider("Scattering", rain.Scatter, 0f, 10f,
            value => _environment.Rain =
                _environment.Rain with { Scatter = value },
            help: "How much the rain spreads as it falls");
        form.Slider("Raindrops", rain.Raindrops, 0f, 1f,
            value => _environment.Rain =
                _environment.Rain with { Raindrops = value },
            help: "How many drops splash on surfaces");
    }

    // ── particles ────────────────────────────────────────────────────────

    private void ParticleRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural particles", EnvSection.Particles,
            "Let the game run the particles — dust, snow and leaves all come "
                + "from this one block. Changing any value below holds it for "
                + "Poser.");
        var particles = _environment.Particles;
        form.Slider("Intensity", particles.Intensity, 0f, 1f,
            value => _environment.Particles =
                _environment.Particles with { Intensity = value },
            help: "How many particles the air carries");
        form.Slider("Size", particles.Size, 0f, 20f,
            value => _environment.Particles =
                _environment.Particles with { Size = value },
            help: "How large a single particle draws");
        form.Slider("Glow", particles.Glow, 0f, 10f,
            value => _environment.Particles =
                _environment.Particles with { Glow = value },
            help: "How brightly the particles glow");
        form.ColorWells("Colour", wells =>
        {
            wells.Well("Particles", particles.Color with { W = 1f },
                value => _environment.Particles = _environment.Particles with
                {
                    Color = Rgb(value, _environment.Particles.Color.W),
                });
        }, help: "The colour the particles draw in");
        form.Slider("Colour alpha", particles.Color.W, 0f, 1f,
            value => _environment.Particles = _environment.Particles with
            {
                Color = _environment.Particles.Color with { W = value },
            },
            help: "How strongly the particle colour applies");
        form.Slider("Weight", particles.Weight, 0f, 10f,
            value => _environment.Particles =
                _environment.Particles with { Weight = value },
            help: "How quickly the particles sink");
        form.Slider("Spread", particles.Spread, 0f, 10f,
            value => _environment.Particles =
                _environment.Particles with { Spread = value },
            help: "How widely the particles scatter");
        form.Slider("Speed", particles.Speed, 0f, 1f,
            value => _environment.Particles =
                _environment.Particles with { Speed = value },
            help: "How fast the particles travel");
        form.Slider("Spin", particles.Spin, 0.05f, 5f,
            value => _environment.Particles =
                _environment.Particles with { Spin = value },
            help: "How fast the particles turn as they travel");
        form.NumericSlider("Texture", particles.TextureId, 0f, 20f,
            value => _environment.Particles =
                _environment.Particles with { TextureId = ToId(value) },
            perPixel: 0.25f,
            format: "0",
            help: "The particle texture: 1 is snow, the rest are dust");
    }

    // ── stars ────────────────────────────────────────────────────────────

    private void StarRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural stars", EnvSection.Stars,
            "Let the game run the night sky. Changing any value below holds "
                + "it for Poser.");
        var stars = _environment.Stars;
        form.Slider("Stars", stars.StarCount, 0f, 20f,
            value => _environment.Stars =
                _environment.Stars with { StarCount = value },
            help: "How many stars the night sky carries");
        form.Slider("Star intensity", stars.StarIntensity, 0f, 2.5f,
            value => _environment.Stars =
                _environment.Stars with { StarIntensity = value },
            help: "How brightly the stars burn");
        form.Slider("Constellations", stars.ConstellationCount, 0f, 10f,
            value => _environment.Stars =
                _environment.Stars with { ConstellationCount = value },
            help: "How many constellations the night sky carries");
        form.Slider(
            "Constellation intensity", stars.ConstellationIntensity, 0f, 2.5f,
            value => _environment.Stars =
                _environment.Stars with { ConstellationIntensity = value },
            help: "How brightly the constellations burn");
        form.Slider("Galaxy intensity", stars.GalaxyIntensity, 0f, 10f,
            value => _environment.Stars =
                _environment.Stars with { GalaxyIntensity = value },
            help: "How brightly the galaxy band shows");
        form.ColorWells("Moon colour", wells =>
        {
            wells.Well("Moon", stars.MoonColor with { W = 1f },
                value => _environment.Stars = _environment.Stars with
                {
                    MoonColor = Rgb(value, _environment.Stars.MoonColor.W),
                });
        }, help: "The colour the moon draws in");
        form.Slider("Moon alpha", stars.MoonColor.W, 0f, 1f,
            value => _environment.Stars = _environment.Stars with
            {
                MoonColor = _environment.Stars.MoonColor with { W = value },
            },
            help: "How strongly the moon colour applies");
        form.Slider("Moon brightness", stars.MoonBrightness, 0f, 1f,
            value => _environment.Stars =
                _environment.Stars with { MoonBrightness = value },
            help: "How brightly the moon shines");
    }

    // ── wind ─────────────────────────────────────────────────────────────

    private void WindRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural wind", EnvSection.Wind,
            "Let the game run the wind. Changing any value below holds it for "
                + "Poser.");
        var wind = _environment.Wind;
        form.Slider("Direction", wind.Direction, 0f, 360f,
            value => _environment.Wind =
                _environment.Wind with { Direction = value },
            format: "0",
            help: "Which way the wind blows, in degrees");
        form.Slider("Angle", wind.Angle, 0f, 180f,
            value => _environment.Wind = _environment.Wind with { Angle = value },
            format: "0",
            help: "How far the wind tilts from level, in degrees");
        form.Slider("Speed", wind.Speed, 0f, 1.5f,
            value => _environment.Wind = _environment.Wind with { Speed = value },
            help: "How hard the wind blows");
    }

    // ── rendering ────────────────────────────────────────────────────────

    private void RenderingRows(Crystarium.FormScope form)
    {
        bool water = _rendering.IsWaterFreezeAvailable;
        form.Switch("Freeze water", _rendering.IsWaterFrozen,
            value => _rendering.IsWaterFrozen = value,
            help: water
                ? "Stop the water renderer, freezing every surface where it "
                    + "stands"
                : WaterUnavailable,
            disabled: !water);
        form.Switch("Restore water on exit", _rendering.ResetWaterOnGPoseExit,
            value => _rendering.ResetWaterOnGPoseExit = value,
            help: "Hand the water back to the game when GPose ends");
        // The environment sections' own lifetime rows live here rather than in
        // eight copies, one per section.
        form.Switch("Restore environment on exit",
            _environment.ResetSectionsOnGPoseExit,
            value => _environment.ResetSectionsOnGPoseExit = value,
            help: "Release every held environment section when GPose ends");
        form.Actions("Sections", actions =>
        {
            actions.Button("Release all sections",
                _environment.ReleaseAllSections,
                help: "Hand every held section back to the game. The next "
                    + "environment update restores the zone's own values.");
        });
    }

    // ── festivals ────────────────────────────────────────────────────────

    private void FestivalRows(Crystarium.FormScope form)
    {
        bool canModify = _festivals.CanModify;
        var slots = _festivals.ActiveFestivals;
        int shown = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Id == 0)
                continue;
            shown++;
            uint id = slot.Id;
            string label = $"Slot {i + 1}";
            var entry = _festivals.FestivalList.TryGetValue(id, out var known)
                ? known
                : null;
            form.ReadOnlyWithActions(
                label,
                $"{entry?.Name ?? "Unknown festival"} · {IdText(id)}",
                actions =>
                {
                    actions.Button("Remove",
                        () => _status = _festivals.Remove(id)
                            ? string.Empty
                            : "Remove: this festival cannot be changed where you are standing.",
                        disabled: !canModify,
                        help: canModify
                            ? "Clear this festival slot"
                            : FestivalsUnavailable);
                },
                help: entry is { Unsafe: true }
                    ? "This festival is known to break the layout in some "
                        + "places"
                    : "A festival the zone is running for Poser",
                unavailable: entry == null);

            var phases = entry?.KnownPhases;
            if (phases is { Count: > 0 })
            {
                var names = new string[phases.Count];
                int selected = -1;
                for (int p = 0; p < phases.Count; p++)
                {
                    names[p] = phases[p].Name;
                    if (phases[p].Id == slot.Phase)
                        selected = p;
                }
                form.Dropdown($"{label} phase", names, selected,
                    chosen => _status = _festivals.ChangePhase(
                        id, (ushort)phases[chosen].Id)
                        ? string.Empty
                        : "Phase: this festival cannot be changed where you are standing.",
                    help: "Which stage of the festival the zone shows",
                    disabled: !canModify);
            }
            else
            {
                form.NumericSlider($"{label} phase", slot.Phase, 0f, 255f,
                    value => _status = _festivals.ChangePhase(
                        id, (ushort)Math.Clamp((int)MathF.Round(value), 0, 255))
                        ? string.Empty
                        : "Phase: this festival cannot be changed where you are standing.",
                    perPixel: 0.25f,
                    format: "0",
                    help: "Which stage of the festival the zone shows — this "
                        + "festival's stages are not named",
                    disabled: !canModify);
            }
        }

        if (shown == 0)
            form.Status("No festivals are running for Poser.");

        form.Actions("Slots", actions =>
        {
            actions.Button("Add festival…", OpenFestivalPicker,
                disabled: !canModify || !_festivals.HasFreeSlot,
                help: !canModify
                    ? FestivalsUnavailable
                    : _festivals.HasFreeSlot
                        ? "Run one more festival in this zone"
                        : "All eight festival slots are taken");
            actions.Button("Reset",
                () =>
                {
                    _festivals.Reset();
                    _status = string.Empty;
                },
                disabled: !_festivals.HasOverride,
                help: _festivals.HasOverride
                    ? "Put back the festivals the zone was running before "
                        + "Poser changed them"
                    : "Poser has not changed this zone's festivals");
        });
    }

    /// <summary>Arms the festival surface on the whole curated list; the
    /// service refuses the ones the player's position excludes.</summary>
    private void OpenFestivalPicker()
    {
        _festivalPicker.Open(
            "Festival",
            FestivalEntries(),
            _festivalName,
            _festivalKey,
            null,
            null,
            new PickerOptions<FestivalEntry>
            {
                Glyph = FestivalGlyph,
                Badge = _festivalBadge,
            });
    }

    /// <summary>The festival rows in one stable order, built on first use: the
    /// service's map is fixed for the session.</summary>
    private List<FestivalEntry> FestivalEntries()
    {
        if (_festivalEntries is { } cached)
            return cached;
        var entries = new List<FestivalEntry>(_festivals.FestivalList.Count);
        foreach (var entry in _festivals.FestivalList.Values)
            entries.Add(entry);
        entries.Sort(static (left, right) =>
        {
            // The named rows lead; two of a kind sort by name, then by id so
            // the order never depends on the map's enumeration.
            if (left.Unknown != right.Unknown)
                return left.Unknown ? 1 : -1;
            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : left.Id.CompareTo(right.Id);
        });
        _festivalEntries = entries;
        return entries;
    }

    // ── shared row shapes ────────────────────────────────────────────────

    /// <summary>
    /// One section's own switch. INVERSION: the switch states "the game runs
    /// this section naturally", which is the service's hold turned OFF. This is
    /// the only place the eight section switches invert.
    /// </summary>
    private void SectionSwitch(
        Crystarium.FormScope form, string label, EnvSection section,
        string help)
    {
        bool available = _environment.IsSectionHoldAvailable;
        form.Switch(label, !_environment.IsSectionHeld(section),
            natural => _environment.SetSectionHeld(section, !natural),
            help: available ? help : HoldUnavailable,
            disabled: !available);
    }

    // ── state ────────────────────────────────────────────────────────────

    private static string ClockText(int minute) =>
        $"{minute / 60:00}:{minute % 60:00}";

    private static Vector4 Opaque(Vector3 color) => new(color, 1f);

    private static Vector3 Rgb(Vector4 color) => new(color.X, color.Y, color.Z);

    private static Vector4 Rgb(Vector4 color, float alpha) =>
        new(color.X, color.Y, color.Z, alpha);

    private static uint ToId(float value) =>
        (uint)Math.Max(0, (int)MathF.Round(value));

    /// <summary>An id as text, minted once and kept: it is a picker row's badge
    /// on every frame the surface draws it, and a row's identity.</summary>
    private string IdText(uint id)
    {
        if (_idText.TryGetValue(id, out var text))
            return text;
        text = id.ToString(CultureInfo.InvariantCulture);
        _idText[id] = text;
        return text;
    }

    /// <summary>
    /// Resolves a weather's game icon to an ImGui handle, or 0 when there is
    /// none. Sheet icon ids are not guaranteed to exist and the game icon
    /// lookup THROWS for those, so this uses the try-variant, catches anyway,
    /// and remembers the failures. The WRAP is never cached: shared textures
    /// must be re-resolved each frame.
    /// </summary>
    private nint ResolveIcon(uint iconId)
    {
        if (iconId == 0 || _missingIcons.Contains(iconId))
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            if (_textures.TryGetFromGameIcon(
                    new GameIconLookup(iconId), out var shared))
                wrap = shared.GetWrapOrDefault();
            else
                _missingIcons.Add(iconId);
        }
        catch (Exception)
        {
            _missingIcons.Add(iconId);
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }
}
