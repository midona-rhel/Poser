using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Poser.Files;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// One page of <see cref="EnvironmentPane"/>. The environment's eleven sections
/// do not fit one scroll, so the shell gives it a five-tab strip and hands the
/// pane the tab it is drawing. Positional against the shell's environment strip.
/// </summary>

/// <summary>
/// The scene's one environment: time, weather, the eight holdable environment
/// sections, water rendering, and the festival slots. The pane owns its
/// collapse state and its two picker surfaces; every value is read live from
/// the services at DISPATCH time and written straight back, so a row never acts
/// on the copy its section opened with.
///
/// <para>The eleven sections are hosted across FIVE pages, one per
/// <see cref="EnvironmentTab"/>. Each page states its own id, so two sections on
/// different tabs can never mint the same row identity, and the collapse state
/// stays per SECTION — a tab is where a section is drawn, not what it is.</para>
///
/// <para>INVERSION BOUNDARY. The services state every hold positively: a HELD
/// thing is one Poser keeps stamping. Time and weather are presented the same
/// way round ("Freeze time", "Hold weather"), and the eight section switches
/// are presented INVERTED — on means the game runs that section naturally,
/// which is the service's hold turned off. Every inversion happens at the call
/// site that needs it and nowhere else.</para>
/// </summary>
/// <summary>
/// One page of <see cref="EnvironmentPane"/>. The environment's eleven sections
/// do not fit one scroll, so the shell gives it a five-tab strip and hands the
/// pane the tab it is drawing. Positional against the shell's environment strip.
/// </summary>
public enum EnvironmentTab
{
    /// <summary>TIME, WEATHER, and LIGHTING: how the scene is lit — the
    /// controls a pose touches most, one mental act, first tab.</summary>
    Lighting,

    /// <summary>SKY (skybox and clouds) and STARS: what the sky IS.</summary>
    Sky,

    /// <summary>FOG, RAIN, PARTICLES and WIND: what fills the air between
    /// the camera and the sky.</summary>
    Atmosphere,

    /// <summary>RENDERING and FESTIVALS: the ground the scene stands on
    /// rather than the air above it.</summary>
    World,
}

public sealed class EnvironmentPane
{
    private readonly IEnvironmentService _environment;
    private readonly IWorldRenderingService _rendering;
    private readonly IFestivalService _festivals;
    private readonly Game.Journal.EnvironmentSession _values;
    private readonly ITextureProvider _textures;
    private readonly ISceneWorkflow _workflow;

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

    /// <summary>Where this pane's verb outcomes go; the five pages state
    /// standing facts only.</summary>
    private readonly UserNotices _notices;

    /// <summary>The one wording for a festival verb the zone refuses. It was
    /// written out three times with three different leading words.</summary>
    private const string FestivalPlaceRefusal =
        "This festival cannot be changed where you are standing.";

    // Every section opens EXPANDED. A page carries at most four of them and a
    // collapsed header hides the only thing the page is for; the disclosure is
    // there to put a section away, not to make the user open it first.
    private bool _openTime = true;
    private bool _openWeather = true;
    private bool _openSky;
    private bool _openClouds = true;
    private bool _openLighting;
    private bool _openFog;
    private bool _openRain;
    private bool _openParticles;
    private bool _openStars;
    private bool _openWind = true;
    private bool _openRendering = true;
    private bool _openFestivals = true;

    /// <summary>Widens the weather surface from this territory's own weathers
    /// to every named weather. Read by the picker's query, so flipping it while
    /// the surface is open re-answers it on the same frame.</summary>
    private bool _showAllWeathers;

    private readonly Crystarium.SearchPicker<WeatherOption> _weatherPicker =
        new("environment-weather");
    private readonly Crystarium.SearchPicker<FestivalEntry> _festivalPicker =
        new("environment-festival");

    /// <summary>The three skybox catalogs. One picker per control: each walks
    /// its OWN texture path, and two of them stand on the same row.</summary>
    private readonly Crystarium.TexturePicker _skyTexture;
    private readonly Crystarium.TexturePicker _cloudTexture;
    private readonly Crystarium.TexturePicker _cloudSideTexture;

    /// <summary>The particle catalog, which is TWO game families behind one
    /// id: see <see cref="ParticleTexturePath"/>.</summary>
    private readonly Crystarium.TexturePicker _particleTexture;

    /// <summary>The clock's own quarters, so the track reads as a day rather
    /// than as a number between 0 and 1439.</summary>
    private static readonly float[] DayQuarters = [360f, 720f, 1080f];

    // Marks for the log-mapped ranges: the reference points a compressed
    // track would otherwise hide.
    private static readonly float[] KilometreMarks = [10f, 100f, 500f];
    private static readonly float[] ThicknessMarks = [1f, 5f, 25f];
    private static readonly float[] OpacityMarks = [1f, 5f];
    private static readonly float[] DistanceMarks = [1f, 10f, 50f];

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

    private readonly GameIconResolver _icons;

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

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    public EnvironmentPane(
        IEnvironmentService environment,
        IWorldRenderingService rendering,
        IFestivalService festivals,
        ITextureProvider textures,
        ISceneWorkflow workflow,
        UserNotices notices,
        global::Poser.UI.Controls.EntityNameModal names,
        Game.Journal.EnvironmentSession values)
    {
        _values = values;
        _names = names;
        _workflow = workflow;
        _notices = notices;
        _environment = environment;
        _rendering = rendering;
        _festivals = festivals;
        _textures = textures;
        _icons = new GameIconResolver(textures);
        _weatherQuery = WeatherResults;
        _weatherTexture = option => _icons.Resolve(option.IconId);
        _weatherBadge = option => IdText(option.Id);
        _festivalKey = entry => IdText(entry.Id);
        _festivalBadge = entry => IdText(entry.Id);
        // The paths are Ktisis's, which is also where the 0..999 walk comes
        // from: the game exposes no list of these, only files that either
        // exist or do not.
        _skyTexture = new Crystarium.TexturePicker(
            "environment-sky-texture",
            (uint id, out nint handle, out Vector2 pixels) => Preview(
                $"bgcommon/nature/sky/texture/sky_{id:D3}.tex",
                out handle,
                out pixels));
        _cloudTexture = new Crystarium.TexturePicker(
            "environment-cloud-texture",
            (uint id, out nint handle, out Vector2 pixels) => Preview(
                $"bgcommon/nature/cloud/texture/cloud_{id:D3}.tex",
                out handle,
                out pixels));
        _cloudSideTexture = new Crystarium.TexturePicker(
            "environment-cloud-side-texture",
            (uint id, out nint handle, out Vector2 pixels) => Preview(
                $"bgcommon/nature/cloud/texture/cloudside_{id:D3}.tex",
                out handle,
                out pixels));
        _particleTexture = new Crystarium.TexturePicker(
            "environment-particle-texture",
            (uint id, out nint handle, out Vector2 pixels) =>
                Preview(ParticleTexturePath(id), out handle, out pixels));
    }

    /// <summary>
    /// The particle id's game path. This one catalog spans TWO families:
    /// id 1 is the snow sheet, and every id from 2 up is a dust sheet offset
    /// by two (Ktisis ParticlesEditor.ResolvePath).
    ///
    /// <para>ZERO has no sheet: it is the no-texture value, and the picker's
    /// own zero rule captions the empty tile "None". Ktisis lands in the same
    /// place by accident — its <c>id - 2</c> underflows the unsigned id into
    /// a path no client ships — so stating the absence is that behaviour
    /// without the accident. It is also what keeps 0 distinct from 2: a
    /// resolver that clamped both onto dust_000 would put one sheet in the
    /// grid twice and leave the no-texture choice unreachable.</para>
    /// </summary>
    private static string ParticleTexturePath(uint id) => id switch
    {
        0 => string.Empty,
        1 => "bgcommon/nature/snow/texture/snow.tex",
        _ => $"bgcommon/nature/dust/texture/dust_{id - 2:D3}.tex",
    };

    /// <summary>Draws the one page the shell's active tab names. The page id is
    /// the tab's own, so the row ids on two tabs are distinct even where the row
    /// LABELS repeat — "Colour alpha" appears on four of the eleven sections.
    /// </summary>
    public void Draw(Vector2 origin, Vector2 size, EnvironmentTab tab)
    {
        DrainPickers();

        switch (tab)
        {
            case EnvironmentTab.Sky:
                Crystarium.Page("environment-sky", origin, size, SkyPage);
                break;
            case EnvironmentTab.Atmosphere:
                Crystarium.Page(
                    "environment-atmosphere", origin, size, AtmospherePage);
                break;
            case EnvironmentTab.World:
                Crystarium.Page("environment-world", origin, size, WorldPage);
                break;
            default:
                Crystarium.Page(
                    "environment-lighting", origin, size, LightingPage);
                break;
        }
    }

    // ── the four pages ───────────────────────────────────────────────────
    //
    // The rule is a divider BETWEEN sections, so EVERY page's first section
    // states divider: false and draws neither the rule nor the margin above it.

    private void LightingPage(Crystarium.PageScope page)
    {
        page.Section("Time", _openTime, next => _openTime = next,
            TimeRows, divider: false);
        page.Section("Weather", _openWeather, next => _openWeather = next,
            WeatherRows);
        page.Section("Lighting", _openLighting,
            next => _openLighting = next, LightingRows);
    }

    private void SkyPage(Crystarium.PageScope page)
    {
        page.Section("Sky", _openSky, next => _openSky = next, SkyRows,
            divider: false);
        page.Section("Clouds", _openClouds,
            next => _openClouds = next, CloudRows);
        page.Section("Stars", _openStars, next => _openStars = next, StarRows);
    }

    private void AtmospherePage(Crystarium.PageScope page)
    {
        page.Section("Fog", _openFog, next => _openFog = next, FogRows,
            divider: false);
        page.Section("Rain", _openRain, next => _openRain = next, RainRows);
        page.Section("Particles", _openParticles,
            next => _openParticles = next, ParticleRows);
        page.Section("Wind", _openWind, next => _openWind = next, WindRows);
    }

    private void WorldPage(Crystarium.PageScope page)
    {
        page.Section("Rendering", _openRendering,
            next => _openRendering = next, RenderingRows, divider: false);
        page.Section("Festivals", _openFestivals,
            next => _openFestivals = next, FestivalRows);
    }



    /// <summary>
    /// Both surfaces are drained at the top of the frame, exactly where the rows
    /// that opened them can still report the outcome.
    ///
    /// <para>UNCONDITIONAL, and it has to be: a picker is an ImGui popup, and a
    /// popup that is not submitted on a frame is closed by ImGui at the end of
    /// it. Draining only the page that hosts the picker's rows would therefore
    /// make a tab change silently drop an open surface. Both are pumped on every
    /// frame the pane draws, whichever tab that is — a closed picker's Draw
    /// returns on its first line, so the tab that hosts neither pays nothing.
    /// </para>
    /// </summary>
    private void DrainPickers()
    {
        if (_weatherPicker.Draw() is { } weather)
        {
            // Picking a weather turns the hold on inside the service: without
            // it the game reverts the pick on its next weather update.
            _values.SetWeather(
                weather.Item.Id, _environment.TransitionTime);
        }

        if (_festivalPicker.Draw() is { } festival)
        {
            if (!_values.AddFestival(festival.Item.Id))
                _notices.Refused(
                    $"{festival.Item.Name}: this festival cannot be set "
                    + "where you are standing.");
        }

        // Same rule as above, and for the same reason: the three texture grids
        // are pumped whichever tab is up, so a tab change cannot drop one.
        if (_skyTexture.Draw() is { } skyId)
            _values.SetSky(_environment.Sky with { SkyTextureId = skyId });
        if (_cloudTexture.Draw() is { } cloudId)
            _values.SetClouds(_environment.Clouds with { CloudTexture = cloudId });
        if (_cloudSideTexture.Draw() is { } cloudSideId)
            _values.SetClouds(_environment.Clouds with { CloudSideTexture = cloudSideId });
        if (_particleTexture.Draw() is { } particleId)
            _values.SetParticles(_environment.Particles with { TextureId = particleId });
    }

    /// <summary>
    /// One candidate texture, answered for the CURRENT frame. A game texture
    /// loads asynchronously, so "not yet" and "never" are different answers:
    /// the wrap is null with no exception while it is still loading and null
    /// WITH one when the path is not in the game's files. Nothing is cached —
    /// a shared texture's handle belongs to the frame that asked for it.
    ///
    /// <para>The wrap's pixel size rides along because the tile crops with it:
    /// several of these catalogs are animation sheets rather than pictures.
    /// </para>
    /// </summary>
    private TextureProbe Preview(
        string path, out nint handle, out Vector2 pixels)
    {
        handle = 0;
        pixels = Vector2.Zero;
        // An id with no path behind it — the particle catalog's zero — is
        // answered here rather than by making the texture provider throw on
        // it once per frame for as long as the tile is on screen.
        if (string.IsNullOrEmpty(path))
            return TextureProbe.Missing;
        ISharedImmediateTexture shared;
        try
        {
            shared = _textures.GetFromGame(path);
        }
        catch (Exception)
        {
            return TextureProbe.Missing;
        }
        if (!shared.TryGetWrap(out var wrap, out var error))
            return error is null ? TextureProbe.Pending : TextureProbe.Missing;
        handle = wrap is null ? 0 : (nint)wrap.Handle.Handle;
        if (wrap is not null)
            pixels = new Vector2(wrap.Width, wrap.Height);
        return handle == 0 ? TextureProbe.Pending : TextureProbe.Ready;
    }

    // ── time ─────────────────────────────────────────────────────────────

    private void TimeRows(Crystarium.FormScope form)
    {
        bool available = _environment.IsTimeFreezeAvailable;
        int minute = _environment.MinuteOfDay;

        form.Cells(cells =>
        {
            cells.Cell(
                "Time of day",
                cell => cell.Slider(
                    "##env-time-of-day", minute, 0f, 1439f,
                    value => _values.SetMinuteOfDay((int)MathF.Round(value)),
                    disabled: !available,
                    // The slider IS the clock. A raw minute-of-day readout
                    // said 468 where the game says 07:48, and the read-only
                    // Clock row that translated it is gone with it.
                    readout: value => ClockText((int)MathF.Round(value)),
                    marks: DayQuarters),
                help: "Set the Eorzean minute of day. Moving it freezes the "
                    + "clock — an unfrozen clock discards the write within a "
                    + "frame.");
            cells.Cell(
                "Day of month",
                cell => cell.Slider(
                    "##env-day-of-month", _environment.DayOfMonth, 1f, 31f,
                    value => _values.SetDayOfMonth((int)MathF.Round(value)),
                    format: "0",
                    disabled: !available),
                help: "Set the Eorzean day of the month. Moving it freezes "
                    + "the clock, exactly as the time does.");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Freeze time",
                cell => cell.Switch(
                    "##env-time-freeze", _environment.IsTimeFrozen,
                    value => _values.SetTimeFrozen(value),
                    disabled: !available),
                help: available
                    ? "Stop the Eorzean clock where it stands"
                    : TimeUnavailable);
            cells.Cell(
                "Restore on exit",
                cell => cell.Switch(
                    "##env-time-restore", _environment.ResetTimeOnGPoseExit,
                    value => _values.SetResetTimeOnGPoseExit(value)),
                help: "Hand the clock back to the game when GPose ends");
        });
    }

    // ── weather ──────────────────────────────────────────────────────────

    private void WeatherRows(Crystarium.FormScope form)
    {
        bool available = _environment.IsWeatherOverrideAvailable;
        uint current = _environment.CurrentWeatherId;
        string name = _environment.GetWeatherInfo(current) is { } info
            ? info.Name
            : IdText(current);

        form.Cells(cells =>
        {
            cells.Cell(
                "Weather",
                cell => cell.Button("##env-weather", name, OpenWeatherPicker,
                    disabled: !available),
                help: available
                    ? "Choose and hold a weather. All weathers includes choices outside this territory's usual list."
                    : WeatherUnavailable);
            // "Show all weathers" overran the label column; the section it
            // stands in already says what is being shown.
            cells.Cell(
                "All weathers",
                cell => cell.Switch(
                    "##env-weather-all", _showAllWeathers,
                    value => _showAllWeathers = value),
                help: "Widen the list from this territory's own weathers to "
                    + "every weather in the game");
        });
        // The slider keeps at least half the row; three-way cells choked
        // it to a third. The two lifetime switches pair below it.
        form.Slider("Transition", _environment.TransitionTime, 0f, 10f,
            value => _values.SetTransitionTime(value),
            help: "Blend time into a picked weather, seconds", onBegin: _values.Seal);
        form.Cells(cells =>
        {
            cells.Cell(
                "Hold weather",
                cell => cell.Switch(
                    "##env-weather-hold",
                    _environment.IsWeatherOverrideEnabled,
                    value => _values.SetWeatherOverrideEnabled(value),
                    disabled: !available),
                help: available
                    ? "Keep this weather against the game's updates"
                    : WeatherUnavailable);
            cells.Cell(
                "Restore on exit",
                cell => cell.Switch(
                    "##env-weather-restore",
                    _environment.ResetWeatherOnGPoseExit,
                    value => _values.SetResetWeatherOnGPoseExit(value)),
                help: "Hand the weather back when GPose ends");
        });
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
        // None is a real selectable ID, not a missing selection. It is also
        // available in territory mode, where zero slots mean no further IDs.
        if (!_showAllWeathers && _environment.GetWeatherInfo(0) is { } none
            && (query.Length == 0 || none.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || "0".Contains(query, StringComparison.Ordinal)))
            _weatherVisible.Add(Option(none));
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
        SectionSwitch(form, "Natural", EnvSection.Sky,
            "Let the game run the skybox. Changing any sky value below holds "
                + "it for Poser.");
        var sky = _environment.Sky;
        // A texture id is a CHOICE, not a magnitude: the slider that used to
        // carry it had an invented ceiling and showed nothing of what was
        // being chosen. The grid the tile opens is the catalog.
        form.Cells(cells =>
        {
            cells.Cell(
                "Sky texture",
                cell => _skyTexture.Field(
                    cell,
                    sky.SkyTextureId,
                    id => _values.SetSky(_environment.Sky with { SkyTextureId = id })),
                help: "The skybox texture the zone draws. Step the id, or "
                    + "open the tile for the whole catalog.");
            cells.Cell(
                "Sun visibility",
                cell => cell.Slider(
                    "##env-sky-sun", sky.SunVisibility, 0f, 1f,
                    value => _values.SetSky(_environment.Sky with { SunVisibility = value })),
                help: "How much of the sun disc shows through the sky");
        });
    }

    private void CloudRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural", EnvSection.Clouds,
            "Let the game run the clouds. Changing any cloud value below "
                + "holds them for Poser.");
        var clouds = _environment.Clouds;
        form.Cells(cells =>
        {
            cells.Cell(
                "Cloud texture",
                cell => _cloudTexture.Field(
                    cell,
                    clouds.CloudTexture,
                    id => _values.SetClouds(_environment.Clouds with { CloudTexture = id })),
                help: "The texture the overhead cloud layer draws");
            // "Cloud side texture" overran the label column; it stands beside
            // the overhead one, which is what "side" is said against.
            cells.Cell(
                "Side texture",
                cell => _cloudSideTexture.Field(
                    cell,
                    clouds.CloudSideTexture,
                    id => _values.SetClouds(_environment.Clouds with { CloudSideTexture = id })),
                help: "The texture the horizon cloud band draws");
        });
        form.ColorWells("Cloud colours", wells =>
        {
            wells.Well("Top", Opaque(clouds.CloudColor1),
                value => _values.SetClouds(_environment.Clouds with { CloudColor1 = Rgb(value) }));
            wells.Well("Side", Opaque(clouds.CloudColor2),
                value => _values.SetClouds(_environment.Clouds with { CloudColor2 = Rgb(value) }));
        }, help: "Tint the overhead clouds and the horizon band");
        form.Cells(cells =>
        {
            cells.Cell(
                "Shadow stop",
                cell => cell.Slider(
                    "##env-cloud-shadow-stop", clouds.ShadowStop, 0f, 2f,
                    value => _values.SetClouds(_environment.Clouds with { ShadowStop = value })),
                help: "Where the cloud shading gradient ends");
            cells.Cell(
                "Cloud height",
                cell => cell.Slider(
                    "##env-cloud-height", clouds.CloudHeight, 0f, 2f,
                    value => _values.SetClouds(_environment.Clouds with { CloudHeight = value })),
                help: "How tall the horizon cloud band stands");
        });
    }

    // ── lighting ─────────────────────────────────────────────────────────

    private void LightingRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural", EnvSection.Lighting,
            "Let the game run the ambient lighting. Changing any value below "
                + "holds it for Poser.");
        var lighting = _environment.Lighting;
        form.ColorWells("Colours", wells =>
        {
            wells.Well("Sun", Opaque(lighting.SunlightColor),
                value => _values.SetLighting(_environment.Lighting with { SunlightColor = Rgb(value) }));
            wells.Well("Moon", Opaque(lighting.MoonlightColor),
                value => _values.SetLighting(_environment.Lighting with { MoonlightColor = Rgb(value) }));
            wells.Well("Ambient", Opaque(lighting.AmbientColor),
                value => _values.SetLighting(_environment.Lighting with { AmbientColor = Rgb(value) }));
        }, help: "The three lights the zone lights everything with");
        form.Cells(cells =>
        {
            cells.Cell(
                "Saturation",
                cell => cell.Slider(
                    "##env-light-saturation", lighting.AmbientSaturation,
                    0f, 5f,
                    value => _values.SetLighting(_environment.Lighting with
                        {
                            AmbientSaturation = value,
                        })),
                help: "How colourful the ambient light is");
            cells.Cell(
                "Temperature",
                cell => cell.Slider(
                    "##env-light-temperature", lighting.AmbientTemperature,
                    -2.5f, 2.5f,
                    value => _values.SetLighting(_environment.Lighting with
                        {
                            AmbientTemperature = value,
                        })),
                help: "How warm or cold the ambient light is");
        });
        // A world vignette measured off the camera: two decades of range whose
        // whole effect lives in the first one, so the travel is exponential.
        form.Slider("Light distance", lighting.LightDistance, 0f, 100f,
            value => _values.SetLighting(_environment.Lighting with { LightDistance = value }),
            help: "How far the zone's lighting reaches",
            marks: DistanceMarks,
            scale: SliderScale.Decades,
            logCurvature: 2f, onBegin: _values.Seal);
        // The reference UIs draw these three unidentified members rather than
        // hide them; they keep their reference names until someone names them.
        form.Cells(cells =>
        {
            cells.Cell(
                "Unknown 1",
                cell => cell.Slider(
                    "##env-light-unknown-1", lighting.Unknown1, 0f, 10f,
                    value => _values.SetLighting(_environment.Lighting with { Unknown1 = value })),
                help: "An unidentified lighting value the references still "
                    + "expose");
            cells.Cell(
                "Unknown 2",
                cell => cell.Slider(
                    "##env-light-unknown-2", lighting.Unknown2, 0f, 100f,
                    value => _values.SetLighting(_environment.Lighting with { Unknown2 = value })),
                help: "An unidentified lighting value the references still "
                    + "expose");
        });
        form.Slider("Unknown 4", lighting.Unknown4, 0f, 1f,
            value => _values.SetLighting(_environment.Lighting with { Unknown4 = value }),
            help: "An unidentified lighting value the references still expose", onBegin: _values.Seal);
    }

    // ── fog ──────────────────────────────────────────────────────────────

    private void FogRows(Crystarium.FormScope form)
    {
        form.PairRows();
        SectionSwitch(form, "Natural", EnvSection.Fog,
            "Let the game run the fog. Changing any value below holds it for "
                + "Poser.");
        var fog = _environment.Fog;
        form.ColorWells("Colour", wells =>
        {
            // No caption: the row label and the section already say fog.
            wells.Well("", fog.Color with { W = 1f },
                value => _values.SetFog(_environment.Fog with
                {
                    Color = Rgb(value, _environment.Fog.Color.W),
                }));
        }, help: "The colour the fog washes the distance with");
        form.Slider("Colour alpha", fog.Color.W, 0f, 1f,
            value => _values.SetFog(_environment.Fog with
            {
                Color = _environment.Fog.Color with { W = value },
            }),
            help: "How strongly the fog colour applies", onBegin: _values.Seal);
        // Distance is the case the range audit was called on: the scene
        // changes across the first tens of units and nothing above about a
        // hundred reads at all, so the 0..1000 both references state stays and
        // the TRAVEL is exponential instead.
        form.Slider("Distance", fog.Distance, 0f, 1000f,
            value => _values.SetFog(_environment.Fog with { Distance = value }),
            help: "How far away the fog starts",
            marks: KilometreMarks,
            scale: SliderScale.Log, onBegin: _values.Seal);
        // 0..50 is Brio's; Ktisis states 0..100 for the same field. Extinction
        // is exponential in thickness, which is why the lower ceiling AND the
        // log travel — the top of this range is one flat wall either way.
        form.Slider("Thickness", fog.Thickness, 0f, 50f,
            value => _values.SetFog(_environment.Fog with { Thickness = value }),
            help: "How dense the fog is once it starts",
            marks: ThicknessMarks,
            scale: SliderScale.Log, onBegin: _values.Seal);
        // Brio states 0..10 for this field and Ktisis states 0..1 for the same
        // offset. The wider ceiling is kept because a held value above 1 must
        // stay reachable; the log travel gives Ktisis's band half the track.
        form.Slider("Fog opacity", fog.FogOpacity, 0f, 10f,
            value => _values.SetFog(_environment.Fog with { FogOpacity = value }),
            help: "How much the fog hides what is behind it",
            marks: OpacityMarks,
            scale: SliderScale.Log, onBegin: _values.Seal);
        form.Slider("Sky opacity", fog.SkyOpacity, 0f, 10f,
            value => _values.SetFog(_environment.Fog with { SkyOpacity = value }),
            help: "How much of the fog reaches the sky", onBegin: _values.Seal);
        // A sky depth like Distance, and mapped like it. "Sky blend"
        // because "Sky smoothness" truncated — a truncated label never
        // ships.
        form.Slider("Sky blend", fog.SkySmoothness, 0f, 1000f,
            value => _values.SetFog(_environment.Fog with { SkySmoothness = value }),
            help: "How gradually the fog blends into the sky",
            marks: KilometreMarks,
            scale: SliderScale.Log, onBegin: _values.Seal);
    }

    // ── rain ─────────────────────────────────────────────────────────────

    private void RainRows(Crystarium.FormScope form)
    {
        form.PairRows();
        SectionSwitch(form, "Natural", EnvSection.Rain,
            "Let the game run the rain. Changing any value below holds it for "
                + "Poser.");
        var rain = _environment.Rain;
        form.Slider("Intensity", rain.Intensity, 0f, 1f,
            value => _values.SetRain(_environment.Rain with { Intensity = value }),
            help: "How hard it rains", onBegin: _values.Seal);
        form.Slider("Line thickness", rain.Size, 0f, 1f,
            value => _values.SetRain(_environment.Rain with { Size = value }),
            help: "How thick a single rain line draws", onBegin: _values.Seal);
        form.ColorWells("Colour", wells =>
        {
            wells.Well("", rain.Color with { W = 1f },
                value => _values.SetRain(_environment.Rain with
                {
                    Color = Rgb(value, _environment.Rain.Color.W),
                }));
        }, help: "The colour the rain lines draw in");
        form.Slider("Colour alpha", rain.Color.W, 0f, 1f,
            value => _values.SetRain(_environment.Rain with
            {
                Color = _environment.Rain.Color with { W = value },
            }),
            help: "How strongly the rain colour applies", onBegin: _values.Seal);
        form.Slider("Weight", rain.Weight, 0f, 10f,
            value => _values.SetRain(_environment.Rain with { Weight = value }),
            help: "How fast the rain falls", onBegin: _values.Seal);
        form.Slider("Scattering", rain.Scatter, 0f, 10f,
            value => _values.SetRain(_environment.Rain with { Scatter = value }),
            help: "How much the rain spreads as it falls", onBegin: _values.Seal);
        form.Slider("Raindrops", rain.Raindrops, 0f, 1f,
            value => _values.SetRain(_environment.Rain with { Raindrops = value }),
            help: "How many drops splash on surfaces", onBegin: _values.Seal);
    }

    // ── particles ────────────────────────────────────────────────────────

    private void ParticleRows(Crystarium.FormScope form)
    {
        form.PairRows();
        SectionSwitch(form, "Natural", EnvSection.Particles,
            "Let the game run the particles — dust, snow and leaves all come "
                + "from this one block. Changing any value below holds it for "
                + "Poser.");
        var particles = _environment.Particles;
        form.Slider("Intensity", particles.Intensity, 0f, 1f,
            value => _values.SetParticles(_environment.Particles with { Intensity = value }),
            help: "How many particles the air carries", onBegin: _values.Seal);
        form.Slider("Size", particles.Size, 0f, 20f,
            value => _values.SetParticles(_environment.Particles with { Size = value }),
            help: "How large a single particle draws", onBegin: _values.Seal);
        form.Slider("Glow", particles.Glow, 0f, 10f,
            value => _values.SetParticles(_environment.Particles with { Glow = value }),
            help: "How brightly the particles glow", onBegin: _values.Seal);
        form.ColorWells("Colour", wells =>
        {
            wells.Well("", particles.Color with { W = 1f },
                value => _values.SetParticles(_environment.Particles with
                {
                    Color = Rgb(value, _environment.Particles.Color.W),
                }));
        }, help: "The colour the particles draw in");
        form.Slider("Colour alpha", particles.Color.W, 0f, 1f,
            value => _values.SetParticles(_environment.Particles with
            {
                Color = _environment.Particles.Color with { W = value },
            }),
            help: "How strongly the particle colour applies", onBegin: _values.Seal);
        form.Slider("Weight", particles.Weight, 0f, 10f,
            value => _values.SetParticles(_environment.Particles with { Weight = value }),
            help: "How quickly the particles sink", onBegin: _values.Seal);
        form.Slider("Spread", particles.Spread, 0f, 10f,
            value => _values.SetParticles(_environment.Particles with { Spread = value }),
            help: "How widely the particles scatter", onBegin: _values.Seal);
        form.Slider("Speed", particles.Speed, 0f, 1f,
            value => _values.SetParticles(_environment.Particles with { Speed = value }),
            help: "How fast the particles travel", onBegin: _values.Seal);
        form.Slider("Spin", particles.Spin, 0.05f, 5f,
            value => _values.SetParticles(_environment.Particles with { Spin = value }),
            help: "How fast the particles turn as they travel", onBegin: _values.Seal);
        // A texture id is a CHOICE, not a magnitude — the same rule the sky
        // and cloud rows already follow. The slider this replaces had an
        // invented ceiling of 20 and showed nothing of what was being chosen;
        // the walk finds whatever the client actually ships.
        form.Cells(cells =>
        {
            cells.Cell(
                "Texture",
                cell => _particleTexture.Field(
                    cell,
                    particles.TextureId,
                    id => _values.SetParticles(_environment.Particles with { TextureId = id })),
                help: "The particle sheet the air carries — 1 is snow, the "
                    + "rest are dust. Step the id, or open the tile for the "
                    + "whole catalog.");
        });
    }

    // ── stars ────────────────────────────────────────────────────────────

    private void StarRows(Crystarium.FormScope form)
    {
        SectionSwitch(form, "Natural", EnvSection.Stars,
            "Let the game run the night sky. Changing any value below holds "
                + "it for Poser.");
        var stars = _environment.Stars;
        form.Cells(cells =>
        {
            cells.Cell(
                "Stars",
                cell => cell.Slider(
                    "##env-star-count", stars.StarCount, 0f, 20f,
                    value => _values.SetStars(_environment.Stars with { StarCount = value })),
                help: "How many stars the night sky carries");
            cells.Cell(
                "Star intensity",
                cell => cell.Slider(
                    "##env-star-intensity", stars.StarIntensity, 0f, 2.5f,
                    value => _values.SetStars(_environment.Stars with { StarIntensity = value })),
                help: "How brightly the stars burn");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Constellations",
                cell => cell.Slider(
                    "##env-constellation-count", stars.ConstellationCount,
                    0f, 10f,
                    value => _values.SetStars(_environment.Stars with
                        {
                            ConstellationCount = value,
                        })),
                help: "How many constellations the night sky carries");
            // "Constellation intensity" overran the label column; it stands
            // beside its own count, which names what it is the intensity of.
            cells.Cell(
                "Intensity",
                cell => cell.Slider(
                    "##env-constellation-intensity",
                    stars.ConstellationIntensity, 0f, 2.5f,
                    value => _values.SetStars(_environment.Stars with
                        {
                            ConstellationIntensity = value,
                        })),
                help: "How brightly the constellations burn");
        });
        form.PairRows();
        form.Slider("Galaxy intensity", stars.GalaxyIntensity, 0f, 10f,
            value => _values.SetStars(_environment.Stars with { GalaxyIntensity = value }),
            help: "How brightly the galaxy band shows", onBegin: _values.Seal);
        form.EndPair();
        form.Cells(cells =>
        {
            cells.Cell(
                "Moon colour",
                cell => cell.ColorWell(
                    "##env-moon-colour", stars.MoonColor with { W = 1f },
                    value => _values.SetStars(_environment.Stars with
                    {
                        MoonColor = Rgb(
                            value, _environment.Stars.MoonColor.W),
                    })),
                help: "The colour the moon draws in");
            // The well edits RGB only, so the moon colour's own alpha keeps
            // the cell beside it.
            cells.Cell(
                "Moon alpha",
                cell => cell.Slider(
                    "##env-moon-alpha", stars.MoonColor.W, 0f, 1f,
                    value => _values.SetStars(_environment.Stars with
                    {
                        MoonColor =
                            _environment.Stars.MoonColor with { W = value },
                    })),
                help: "How strongly the moon colour applies");
            cells.Cell(
                "Brightness",
                cell => cell.Slider(
                    "##env-moon-brightness", stars.MoonBrightness, 0f, 1f,
                    value => _values.SetStars(_environment.Stars with { MoonBrightness = value })),
                help: "How brightly the moon shines");
        });
    }

    // ── wind ─────────────────────────────────────────────────────────────

    private void WindRows(Crystarium.FormScope form)
    {
        form.PairRows();
        SectionSwitch(form, "Natural", EnvSection.Wind,
            "Let the game run the wind. Changing any value below holds it for "
                + "Poser.");
        var wind = _environment.Wind;
        form.Slider("Direction", wind.Direction, 0f, 360f,
            value => _values.SetWind(_environment.Wind with { Direction = value }),
            help: "Which way the wind blows, in degrees", onBegin: _values.Seal);
        form.Slider("Angle", wind.Angle, 0f, 180f,
            value => _values.SetWind(_environment.Wind with { Angle = value }),
            help: "How far the wind tilts from level, in degrees", onBegin: _values.Seal);
        form.Slider("Speed", wind.Speed, 0f, 1.5f,
            value => _values.SetWind(_environment.Wind with { Speed = value }),
            help: "How hard the wind blows", onBegin: _values.Seal);
    }

    // ── rendering ────────────────────────────────────────────────────────

    private void RenderingRows(Crystarium.FormScope form)
    {
        bool water = _rendering.IsWaterFreezeAvailable;
        // Two rows, not four: the water pair, then the lifetime pair.
        form.PairRows();
        form.Switch("Freeze water", _rendering.IsWaterFrozen,
            value => _values.SetWaterFrozen(value),
            help: water
                ? "Freeze every water surface"
                : WaterUnavailable,
            disabled: !water);
        form.Switch("Restore water", _rendering.ResetWaterOnGPoseExit,
            value => _values.SetResetWaterOnGPoseExit(value),
            help: "Hand the water back when GPose ends");
        // One Sections row: the lifetime switch and the release verb are
        // the same subject — and neither label truncates.
        form.SwitchActions("Sections",
            _environment.ResetSectionsOnGPoseExit,
            value => _values.SetResetSectionsOnGPoseExit(value),
            actions => actions.Button("Release all",
                _values.ReleaseAllSections,
                help: "Hand every held section back"),
            help: "Release held sections when GPose ends");
        form.ActionDropdown("More", ["Save to library"], -1, "More",
                _ => _names.Open(
                    "Save environment to library", "Environment",
                    SaveToLibrary),
                help: "Save the environment into the library");
    }

    /// <summary>The naming prompt precedes this (ruled 2026-08-31); the
    /// entry lands in the objects home as an .xive — a scene save
    /// restricted to the environment, restoring through the same load
    /// every scene uses.</summary>
    private void SaveToLibrary(string name)
    {
        var root = Config.ConfigurationService.Instance.Config.Library
            .ResolveObjectsRoot();
        if (!global::Poser.Library.LibraryConfiguration.TryEnsureDirectory(root, out var detail))
        {
            _notices.Refused(detail);
            return;
        }
        var path = global::Poser.Library.LibraryConfiguration.NewEntryPath(
            root, name, SceneFile.EnvironmentEntryExtension);
        var result = _workflow.BeginSave(
            path, null, SceneSaveOptions.EnvironmentEntry);
        if (!result.Success)
            _notices.Refused(
                result.Detail ?? "The environment could not be saved.");
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
                        () =>
                        {
                            if (!_values.RemoveFestival(id))
                                _notices.Refused(FestivalPlaceRefusal);
                        },
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
                    chosen =>
                    {
                        if (!_values.ChangeFestivalPhase(
                                id, (ushort)phases[chosen].Id))
                            _notices.Refused(FestivalPlaceRefusal);
                    },
                    help: "Which stage of the festival the zone shows",
                    disabled: !canModify);
            }
            else
            {
                form.NumericSlider($"{label} phase", slot.Phase, 0f, 255f,
                    value =>
                    {
                        if (!_values.ChangeFestivalPhase(
                                id,
                                (ushort)Math.Clamp(
                                    (int)MathF.Round(value), 0, 255)))
                            _notices.Refused(FestivalPlaceRefusal);
                    },
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
            actions.Button("Add festival", OpenFestivalPicker,
                disabled: !canModify || !_festivals.HasFreeSlot,
                help: !canModify
                    ? FestivalsUnavailable
                    : _festivals.HasFreeSlot
                        ? "Run one more festival in this zone"
                        : "All eight festival slots are taken");
            actions.Button("Reset",
                () =>
                {
                    _values.ResetFestivals();
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
            natural => _values.SetSectionHeld(section, !natural),
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
}
