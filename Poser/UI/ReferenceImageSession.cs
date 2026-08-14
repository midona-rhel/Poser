using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.Config;

namespace Poser.UI;

/// <summary>
/// One pinned picture at runtime: the config entry it writes through, and the
/// texture it draws. The wrap is OWNED — not a shared immediate texture — so
/// the picture has a definite release point, which is what lets the window's
/// close and the plugin's dispose both free it.
/// </summary>
public sealed class ReferenceImageInstance
{
    internal ReferenceImageInstance(ReferenceImageEntry entry) => Entry = entry;

    public ReferenceImageEntry Entry { get; }

    public int Id => Entry.Id;

    public string Name => Entry.Name;

    /// <summary>Frame-usable ImGui handle, or 0 while the picture is loading
    /// or after it failed.</summary>
    public nint Handle { get; internal set; }

    /// <summary>The picture's own pixels. The window's aspect ratio and its
    /// first seating both come from this and nothing else.</summary>
    public Vector2 Pixels { get; internal set; }

    public bool Loading { get; internal set; }

    /// <summary>Why there is no picture, in the words the empty state shows.
    /// Null while loading and while a picture is up.</summary>
    public string? Failure { get; internal set; }

    internal IDalamudTextureWrap? Wrap;

    public float Aspect =>
        Pixels.X > 0f && Pixels.Y > 0f ? Pixels.X / Pixels.Y : 0f;
}

/// <summary>
/// THE reference-image roster at runtime. Owns the config sync, the file
/// dialog that adds pictures, every texture, and the add/remove events the
/// window set turns into windows.
///
/// <para>Config writes are DEBOUNCED to the end of a gesture: dragging a
/// window or its opacity slider writes the entry every frame and saves the
/// plugin config only once no mouse button is down, so a drag costs one file
/// write rather than one per frame.</para>
/// </summary>
public sealed class ReferenceImageSession : IDisposable
{
    /// <summary>Brio's own filter, verbatim
    /// (<c>Brio/UI/Controls/Editors/SpawnMenu.cs:268-280</c>).</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg"];

    private readonly ITextureProvider _textures;
    private readonly ConfigurationService _configuration;
    private readonly UserNotices _notices;

    private readonly Crystarium.FileDialog _browser =
        new("Add Reference Image", ImageExtensions);

    private readonly List<ReferenceImageInstance> _instances = new();

    /// <summary>The only cross-thread channel. A null wrap carries the reason
    /// instead.</summary>
    private readonly ConcurrentQueue<(int Id, int Generation,
        IDalamudTextureWrap? Wrap, string? Failure)> _completed = new();

    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>Bumped by <see cref="Dispose"/>; a load carries the value it
    /// started under, so a wrap that lands after teardown is disposed instead
    /// of stored.</summary>
    private int _generation;

    private bool _restored;
    private bool _dirty;
    private bool _disposed;

    public ReferenceImageSession(
        ITextureProvider textures,
        ConfigurationService configuration,
        UserNotices notices)
    {
        _textures = textures;
        _configuration = configuration;
        _notices = notices;
    }

    /// <summary>A picture joined the roster and wants a window.</summary>
    public event Action<ReferenceImageInstance>? OnAdded;

    /// <summary>A picture left the roster and its window must go.</summary>
    public event Action<ReferenceImageInstance>? OnRemoved;

    public IReadOnlyList<ReferenceImageInstance> Instances => _instances;

    private ReferenceImageConfiguration Roster =>
        _configuration.Config.ReferenceImages;

    /// <summary>
    /// Rebuilds the stored roster, once per plugin lifetime. Ktisis does the
    /// same at scene setup (<c>Ktisis/Scene/SceneManager.cs:87-93</c>); the
    /// difference is that a picture whose file has gone keeps its window and
    /// says so, rather than silently not appearing.
    /// </summary>
    public void Restore()
    {
        if (_restored || _disposed)
            return;
        _restored = true;
        // Snapshotted: a refused entry stays in the roster, and Adopt writes
        // nothing back, but the guard costs nothing and states the intent.
        var stored = Roster.Images.ToArray();
        foreach (var entry in stored)
            Adopt(entry);
    }

    /// <summary>Opens the picker. The dialog is owned here rather than by the
    /// surface that opened it, for the reason LightPane's is: the spawn
    /// browser closes on focus loss, and a dialog pumped from a closed window
    /// is a dead dialog.</summary>
    public void OpenAddDialog()
    {
        _browser.Open(_lastPath, path =>
        {
            _lastPath = Path.GetDirectoryName(path) ?? _lastPath;
            Add(path);
        });
    }

    /// <summary>Pumped every frame from the UI root, outside every window —
    /// see <see cref="OpenAddDialog"/>.</summary>
    public void DrawDialogs() => _browser.Draw();

    public ReferenceImageInstance Add(string filePath)
    {
        var entry = Roster.Add(filePath);
        _configuration.Save();
        return Adopt(entry);
    }

    /// <summary>Closes one picture for good: the entry leaves the config, the
    /// window leaves the window set, and the texture is released here.
    /// </summary>
    public void Close(ReferenceImageInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!_instances.Remove(instance))
            return;
        Roster.Remove(instance.Id);
        _dirty = false;
        _configuration.Save();
        Release(instance);
        OnRemoved?.Invoke(instance);
    }

    /// <summary>
    /// Takes a picture off screen, or puts it back. The session is the ONE
    /// owner of this answer: the window set reads it to decide whether the
    /// window stands, and the sidebar row's eye restates it. Nothing else
    /// holds a second copy, so the eye and the window cannot disagree.
    ///
    /// <para>Saved on the spot rather than marked dirty: this is a click, not
    /// a drag, and the end-of-gesture save exists for gestures.</para>
    /// </summary>
    public void SetHidden(ReferenceImageInstance instance, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Entry.Hidden == hidden)
            return;
        instance.Entry.Hidden = hidden;
        _dirty = false;
        _configuration.Save();
    }

    /// <summary>Whether the picture is currently set aside.</summary>
    public static bool IsHidden(ReferenceImageInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Entry.Hidden;
    }

    /// <summary>
    /// A second placement of the SAME picture — the roster's own reason for
    /// minting identity instead of deriving it from the path (two crops of one
    /// sheet, two placements of one pose sheet). The copy carries the
    /// original's opacity and arrives visible, seating itself from the
    /// picture's own pixels rather than landing exactly under the original
    /// where it could not be told apart.
    /// </summary>
    public ReferenceImageInstance Duplicate(ReferenceImageInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var copy = Add(instance.Entry.FilePath);
        copy.Entry.Opacity = instance.Entry.Opacity;
        _dirty = false;
        _configuration.Save();
        return copy;
    }

    /// <summary>Writes an opacity through the floor and marks the roster for
    /// the end-of-gesture save.</summary>
    public void SetOpacity(ReferenceImageInstance instance, float opacity)
    {
        ArgumentNullException.ThrowIfNull(instance);
        float clamped = ReferenceImageConfiguration.ClampOpacity(opacity);
        if (clamped == instance.Entry.Opacity)
            return;
        instance.Entry.Opacity = clamped;
        _dirty = true;
    }

    /// <summary>
    /// Records where a window ended up this frame. Placement is LOGICAL, so
    /// the stored roster is independent of the global UI scale.
    ///
    /// <para>Compared with a half-pixel tolerance rather than exactly: a size
    /// written out as logical, scaled up by ImGui and divided back down does
    /// not always land on the same float, and an exact comparison would mark
    /// the roster dirty on every idle frame — which is a config file write per
    /// frame.</para>
    /// </summary>
    public void SetPlacement(
        ReferenceImageInstance instance, Vector2 position, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var entry = instance.Entry;
        const float tolerance = 0.5f;
        if (MathF.Abs(entry.X - position.X) < tolerance
            && MathF.Abs(entry.Y - position.Y) < tolerance
            && MathF.Abs(entry.Width - size.X) < tolerance
            && MathF.Abs(entry.Height - size.Y) < tolerance)
            return;
        entry.X = position.X;
        entry.Y = position.Y;
        entry.Width = size.X;
        entry.Height = size.Y;
        _dirty = true;
    }

    /// <summary>
    /// One frame of housekeeping, run before the windows draw: finished loads
    /// are integrated and a settled gesture's roster is saved.
    /// </summary>
    public void Tick()
    {
        while (_completed.TryDequeue(out var done))
            Integrate(done.Id, done.Generation, done.Wrap, done.Failure);

        // The save waits for every button to come up: a window drag and an
        // opacity drag both write the entry per frame, and the config is a
        // file.
        if (_dirty && !ImGui.IsAnyMouseDown())
        {
            _dirty = false;
            _configuration.Save();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Bump first: anything a worker enqueues from here on is stale and is
        // disposed by the drain below rather than stored.
        _generation++;
        if (_dirty)
        {
            _dirty = false;
            _configuration.Save();
        }
        foreach (var instance in _instances)
            Release(instance);
        _instances.Clear();
        while (_completed.TryDequeue(out var done))
            done.Wrap?.Dispose();
    }

    private ReferenceImageInstance Adopt(ReferenceImageEntry entry)
    {
        entry.Opacity = ReferenceImageConfiguration.ClampOpacity(entry.Opacity);
        if (string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = ReferenceImageConfiguration.NameFor(entry.FilePath);
        var instance = new ReferenceImageInstance(entry);
        _instances.Add(instance);
        StartLoad(instance);
        OnAdded?.Invoke(instance);
        return instance;
    }

    private static void Release(ReferenceImageInstance instance)
    {
        instance.Wrap?.Dispose();
        instance.Wrap = null;
        instance.Handle = 0;
        instance.Pixels = Vector2.Zero;
    }

    private void StartLoad(ReferenceImageInstance instance)
    {
        instance.Loading = true;
        instance.Failure = null;
        int id = instance.Id;
        int generation = _generation;
        string path = instance.Entry.FilePath;
        var queue = _completed;
        var textures = _textures;
        _ = Task.Run(async () =>
        {
            IDalamudTextureWrap? wrap = null;
            string? failure = null;
            try
            {
                if (!File.Exists(path))
                    failure = "gone";
                else
                {
                    var bytes = await File.ReadAllBytesAsync(path)
                        .ConfigureAwait(false);
                    wrap = await textures
                        .CreateFromImageAsync(bytes, "Poser reference image")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                wrap?.Dispose();
                wrap = null;
                failure = "unreadable";
            }
            queue.Enqueue((id, generation, wrap, failure));
        });
    }

    /// <summary>
    /// A finished load, on the render thread. A refusal is stated ONCE, here —
    /// naming the picture, because the roster can restore several at a time
    /// and "a reference image is missing" would not say which.
    /// </summary>
    private void Integrate(
        int id, int generation, IDalamudTextureWrap? wrap, string? failure)
    {
        if (_disposed || generation != _generation)
        {
            wrap?.Dispose();
            return;
        }
        var instance = Find(id);
        if (instance is null)
        {
            // Closed while the load was in flight.
            wrap?.Dispose();
            return;
        }

        instance.Loading = false;
        if (wrap is null)
        {
            instance.Failure = failure == "gone"
                ? "This picture is no longer on disk."
                : "This picture could not be read.";
            _notices.Refused(failure == "gone"
                ? $"The reference image \"{instance.Name}\" is no longer at "
                    + $"{instance.Entry.FilePath}."
                : $"The reference image \"{instance.Name}\" could not be "
                    + $"read from {instance.Entry.FilePath}.");
            return;
        }

        instance.Wrap = wrap;
        instance.Handle = (nint)wrap.Handle.Handle;
        instance.Pixels = new Vector2(wrap.Width, wrap.Height);
        instance.Failure = null;
    }

    private ReferenceImageInstance? Find(int id)
    {
        for (int i = 0; i < _instances.Count; i++)
            if (_instances[i].Id == id)
                return _instances[i];
        return null;
    }
}
