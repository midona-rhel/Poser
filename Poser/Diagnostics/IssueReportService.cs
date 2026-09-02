using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Poser.Application.Diagnostics;
using Poser.Application.Scene;
using Poser.Config;
using Poser.Services;

namespace Poser.Diagnostics;

/// <summary>
/// Writes the issue report: one zip in the plugin's own folder holding a
/// JSON with the recorded actions and their values, the notices, the
/// caught exceptions, the versions, the loaded plugins, the settings and
/// the plugin's own log lines — and, when asked, the scene as scene data
/// only. Nothing leaves the machine; the user attaches the file. Names
/// are tokens throughout: the recorder scrubbed them as they were
/// written, and the scene file is scrubbed before it is packed.
/// </summary>
public sealed class IssueReportService
{
    public const int LogLines = 200;

    private readonly ActionRecorder _recorder;
    private readonly IDalamudPluginInterface _plugin;
    private readonly ConfigurationService _config;
    private readonly SceneSession _scene;
    private readonly ISceneWorkflow _scenes;
    private readonly IPluginLog _log;
    private readonly Dictionary<Guid, string> _tokens = new();
    private string? _pendingScene;
    private string? _pendingZip;
    private Action<string>? _pendingDone;
    private Action<string>? _pendingFailed;

    public IssueReportService(
        ActionRecorder recorder,
        IDalamudPluginInterface plugin,
        ConfigurationService config,
        SceneSession scene,
        ISceneWorkflow scenes,
        IPluginLog log,
        UI.UserNotices notices)
    {
        _recorder = recorder;
        _plugin = plugin;
        _config = config;
        _scene = scene;
        _scenes = scenes;
        _log = log;
        _recorder.ActorToken = TokenFor;
        _recorder.Scrub = Scrub;
        notices.Posted += _recorder.Notice;
    }

    /// <summary>Where the reports land.</summary>
    public string Folder => Path.Combine(_plugin.GetPluginConfigDirectory(), "issues");

    /// <summary>Whether a scene save for a report is still running.</summary>
    public bool Pending => _pendingZip is not null;

    /// <summary>Writes the report. With the scene, the scene save runs on
    /// the workflow and the zip closes on <see cref="Tick"/> when it lands.</summary>
    public void Save(bool includeScene, Action<string> done, Action<string> failed)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string zip = Path.Combine(Folder, $"poser-issue-{stamp}.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("report.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(BuildReport());
            }
            if (!includeScene)
            {
                done(zip);
                return;
            }
            string scenePath = Path.Combine(Folder, $"scene-{stamp}.json");
            var begun = _scenes.BeginSave(scenePath, "Issue report scene");
            if (!begun.Success)
            {
                failed($"The scene could not be saved: {begun.Detail}. The report was written without it.");
                done(zip);
                return;
            }
            _pendingScene = scenePath;
            _pendingZip = zip;
            _pendingDone = done;
            _pendingFailed = failed;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Issue report failed");
            failed(ex.Message);
        }
    }

    /// <summary>Closes a report whose scene save has landed. Called once a
    /// frame by the dialog while a save is pending.</summary>
    public void Tick()
    {
        if (_pendingZip is null || _scenes.Busy)
            return;
        string zip = _pendingZip;
        string scenePath = _pendingScene!;
        var done = _pendingDone!;
        var failed = _pendingFailed!;
        _pendingZip = null;
        _pendingScene = null;
        _pendingDone = null;
        _pendingFailed = null;
        try
        {
            if (!File.Exists(scenePath))
            {
                failed("The scene save produced no file. The report was written without it.");
                done(zip);
                return;
            }
            string scene = Scrub(File.ReadAllText(scenePath));
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("scene.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(scene);
            }
            File.Delete(scenePath);
            done(zip);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Issue report scene failed");
            failed(ex.Message);
        }
    }

    private string BuildReport()
    {
        var plugins = new List<object>();
        foreach (var installed in _plugin.InstalledPlugins)
            if (installed.IsLoaded)
                plugins.Add(new { installed.Name, Version = installed.Version.ToString() });
        var report = new
        {
            Poser = typeof(IssueReportService).Assembly.GetName().Version?.ToString(),
            Dalamud = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString(),
            Os = Environment.OSVersion.VersionString,
            WrittenAt = DateTime.UtcNow,
            Plugins = plugins,
            Actors = _scene.Snapshot.Actors.Count,
            Actions = _recorder.Snapshot(),
            Settings = JsonConvert.DeserializeObject(Scrub(JsonConvert.SerializeObject(_config.Config))),
            Log = LogTail(),
        };
        return JsonConvert.SerializeObject(report, Formatting.Indented, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        });
    }

    /// <summary>The plugin's own lines from the Dalamud log, newest last.</summary>
    private List<string> LogTail()
    {
        var lines = new List<string>();
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "dalamud.log");
            var all = new List<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                    if (line.Contains("Poser", StringComparison.Ordinal))
                        all.Add(line);
            }
            int start = Math.Max(0, all.Count - LogLines);
            for (int i = start; i < all.Count; i++)
                lines.Add(Scrub(all[i]));
        }
        catch (Exception ex)
        {
            lines.Add("The log could not be read: " + ex.Message);
        }
        return lines;
    }

    /// <summary>"Actor 1", "Actor 2", … in order of first sight, stable for
    /// the session.</summary>
    private string TokenFor(Guid lineage)
    {
        if (!_tokens.TryGetValue(lineage, out var token))
            _tokens[lineage] = token = "Actor " + (_tokens.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return token;
    }

    /// <summary>Every known actor name becomes its token; the user's
    /// profile path and name become a tilde.</summary>
    private string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (string.IsNullOrWhiteSpace(actor.Name))
                continue;
            string token = TokenFor(actor.Id.LogicalId);
            text = text.Replace(actor.Name, token, StringComparison.OrdinalIgnoreCase);
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            text = text.Replace(profile, "~", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(profile.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }
        string user = Environment.UserName;
        if (!string.IsNullOrEmpty(user) && user.Length > 2)
            text = text.Replace(user, "~", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
