using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Poser.UI;

namespace Crystarium.Capture;

/// <summary>
/// Minimal synchronous font-atlas host. It implements only the pre-build
/// operations used by FontRegistry; unsupported Dalamud/game font paths fail
/// loudly so a comparison cannot silently fall back to a different face.
/// </summary>
internal sealed class StandaloneFontAtlas : IFontAtlas
{
    private sealed class PopScope(bool pushed) : IDisposable
    {
        public void Dispose()
        {
            if (pushed)
                ImGui.PopFont();
        }
    }

    private sealed class LockedFont(ImFontPtr font) : ILockedImFont
    {
        public ImFontPtr ImFont { get; } = font;
        public ILockedImFont NewRef() => new LockedFont(ImFont);
        public void Dispose() { }
    }

    private sealed class Handle(FontAtlasBuildStepDelegate build) : IFontHandle
    {
        private ImFontPtr _font;
        public Exception? LoadException { get; private set; }
        public bool Available => !_font.IsNull && LoadException == null;
        public event IFontHandle.ImFontChangedDelegate? ImFontChanged;

        internal void Build(Toolkit toolkit)
        {
            try
            {
                toolkit.Font = default;
                build(toolkit);
                _font = toolkit.Font;
                if (_font.IsNull)
                    throw new InvalidOperationException(
                        "Font build did not choose a font.");
                ImFontChanged?.Invoke(this, new LockedFont(_font));
            }
            catch (Exception exception)
            {
                LoadException = exception;
            }
        }

        public IDisposable Push()
        {
            if (Available)
                ImGui.PushFont(_font);
            return new PopScope(Available);
        }

        public void Pop()
        {
            if (Available)
                ImGui.PopFont();
        }

        public ILockedImFont? TryLock(out string? errorMessage)
        {
            if (!Available)
            {
                errorMessage = LoadException?.Message ?? "Font is unavailable.";
                return null;
            }
            errorMessage = null;
            return new LockedFont(_font);
        }

        public ILockedImFont Lock() =>
            Available
                ? new LockedFont(_font)
                : throw new InvalidOperationException(
                    LoadException?.Message ?? "Font is unavailable.");

        public Task<IFontHandle> WaitAsync() =>
            Available
                ? Task.FromResult<IFontHandle>(this)
                : Task.FromException<IFontHandle>(
                    LoadException ?? new InvalidOperationException(
                        "Font is unavailable."));

        public Task<IFontHandle> WaitAsync(
            CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<IFontHandle>(cancellationToken)
                : WaitAsync();

        public void Dispose() { }
    }

    private sealed unsafe class Toolkit(
        ImFontAtlasPtr atlas) : IFontAtlasBuildToolkitPreBuild
    {
        // Latin + punctuation, combining diacritics, CJK punctuation/kana
        // and unified ideographs for the truncation-safety fixtures. Faces
        // without coverage simply contribute no glyphs (the comparison then
        // shows the honest missing-glyph difference). Astral-plane emoji sit
        // outside ImGui's 16-bit glyph ranges and cannot be requested here.
        private static readonly ushort[] GlyphRanges =
        [
            0x20, 0xff,
            0x0300, 0x036f,
            0x2013, 0x2026,
            0x3000, 0x30ff,
            0x4e00, 0x9fff,
            0,
        ];

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern ImFontConfig*
            ImFontConfig_ImFontConfig();

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ImFontConfig_destroy(
            ImFontConfig* config);

        public ImFontPtr Font { get; set; }
        public float Scale => 1f;
        public bool IsAsyncBuildOperation => false;
        public FontAtlasBuildStep BuildStep => FontAtlasBuildStep.PreBuild;
        public ImFontAtlasPtr NewImAtlas => atlas;
        public ImVectorWrapper<ImFontPtr> Fonts =>
            throw Unsupported();

        public T DisposeWithAtlas<T>(T disposable) where T : IDisposable =>
            disposable;
        public GCHandle DisposeWithAtlas(GCHandle handle) => handle;
        public void DisposeWithAtlas(Action action) { }
        public ImFontPtr GetFont(IFontHandle fontHandle) =>
            fontHandle is Handle handle && handle.Available
                ? handle.Lock().ImFont
                : default;

        public T DisposeAfterBuild<T>(T disposable) where T : IDisposable =>
            disposable;
        public GCHandle DisposeAfterBuild(GCHandle handle) => handle;
        public void DisposeAfterBuild(Action action) => action();
        public ImFontPtr SetFontScaleMode(
            ImFontPtr fontPtr, FontScaleMode mode) => fontPtr;
        public FontScaleMode GetFontScaleMode(ImFontPtr fontPtr) =>
            FontScaleMode.Default;
        public void RegisterPostBuild(Action action) => action();

        public ImFontPtr AddFontFromFile(
            string path, in SafeFontConfig fontConfig)
        {
            var nativeConfig = ImFontConfig_ImFontConfig();
            var config = new ImFontConfigPtr(nativeConfig)
            {
                GlyphOffset = fontConfig.GlyphOffset,
            };
            try
            {
                fixed (ushort* ranges = fontConfig.GlyphRanges ?? GlyphRanges)
                {
                    Font = atlas.AddFontFromFileTTF(
                        path,
                        fontConfig.SizePx,
                        config,
                        ranges);
                }
                return Font;
            }
            finally
            {
                ImFontConfig_destroy(nativeConfig);
            }
        }

        public ImFontPtr AddDalamudDefaultFont(
            float sizePx, ushort[]? glyphRanges = null)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Fonts),
                "segoeui.ttf");
            var config = new SafeFontConfig
            {
                SizePx = sizePx * TtfMetrics.CssScale(path),
            };
            return AddFontFromFile(path, in config);
        }

        public ImFontPtr AddFontFromImGuiHeapAllocatedMemory(
            void* dataPointer,
            int dataSize,
            in SafeFontConfig fontConfig,
            bool freeOnException,
            string debugTag) => throw Unsupported();

        public ImFontPtr AddFontFromStream(
            Stream stream,
            in SafeFontConfig fontConfig,
            bool leaveOpen,
            string debugTag) => throw Unsupported();

        public ImFontPtr AddFontFromMemory(
            ReadOnlySpan<byte> span,
            in SafeFontConfig fontConfig,
            string debugTag) => throw Unsupported();

        public ImFontPtr AddDalamudAssetFont(
            DalamudAsset asset,
            in SafeFontConfig fontConfig) => throw Unsupported();

        public ImFontPtr AddFontAwesomeIconFont(
            in SafeFontConfig fontConfig) => throw Unsupported();

        public ImFontPtr AddGameSymbol(
            in SafeFontConfig fontConfig) => throw Unsupported();

        public ImFontPtr AddGameGlyphs(
            GameFontStyle gameFontStyle,
            ushort[]? glyphRanges,
            ImFontPtr mergeFont) => throw Unsupported();

        public void AttachWindowsDefaultFont(
            CultureInfo cultureInfo,
            in SafeFontConfig fontConfig,
            int weight = 400,
            int stretch = 5,
            int style = 0)
        {
            // Dalamud semantics: merge the culture's Windows default UI
            // font into MergeFont (falling back to the current font);
            // silently do nothing when either the target or the culture
            // is unavailable. The size is fontConfig.SizePx AS GIVEN —
            // re-deriving it from the fallback's own metrics would make
            // the capture host diverge from the in-game font path.
            var target = fontConfig.MergeFont;
            if (target.IsNull)
                target = Font;
            if (target.IsNull)
                return;
            var resolved = WindowsCultureFonts.Resolve(cultureInfo, weight);
            if (resolved is not { } face)
                return;
            var nativeConfig = ImFontConfig_ImFontConfig();
            var config = new ImFontConfigPtr(nativeConfig)
            {
                MergeMode = true,
                DstFont = target,
                FontNo = face.FaceIndex,
                GlyphOffset = fontConfig.GlyphOffset,
            };
            try
            {
                fixed (ushort* ranges = fontConfig.GlyphRanges ?? GlyphRanges)
                {
                    atlas.AddFontFromFileTTF(
                        face.Path,
                        fontConfig.SizePx,
                        config,
                        ranges);
                }
            }
            finally
            {
                ImFontConfig_destroy(nativeConfig);
            }
        }

        public void AttachExtraGlyphsForDalamudLanguage(
            in SafeFontConfig fontConfig) { }

        private static NotSupportedException Unsupported() =>
            new("This font operation is outside the conformance host.");
    }

    private readonly Dx11Renderer _renderer;
    private readonly List<Handle> _handles = [];
    private readonly List<nint> _textures = [];

    public StandaloneFontAtlas(Dx11Renderer renderer)
    {
        _renderer = renderer;
        ImAtlas = ImGui.GetIO().Fonts;
    }

    public string Name => "Crystarium conformance";
    public FontAtlasAutoRebuildMode AutoRebuildMode =>
        FontAtlasAutoRebuildMode.Disable;
    public ImFontAtlasPtr ImAtlas { get; }
    public Task BuildTask => Task.CompletedTask;
    public bool HasBuiltAtlas { get; private set; }
    public bool IsGlobalScaled => true;
    public event FontAtlasBuildStepDelegate? BuildStepChange;
    public event Action? RebuildRecommend
    {
        add { }
        remove { }
    }

    public IFontHandle NewDelegateFontHandle(
        FontAtlasBuildStepDelegate buildStepDelegate)
    {
        var handle = new Handle(buildStepDelegate);
        _handles.Add(handle);
        return handle;
    }

    public unsafe void BuildFontsImmediately()
    {
        ImAtlas.Clear();
        var toolkit = new Toolkit(ImAtlas);
        foreach (var handle in _handles)
            handle.Build(toolkit);
        BuildStepChange?.Invoke(toolkit);
        if (!ImAtlas.Build())
            throw new InvalidOperationException("ImGui font atlas build failed.");

        foreach (nint texture in _textures)
            _renderer.DestroyTexture(texture);
        _textures.Clear();
        for (int i = 0; i < ImAtlas.Textures.Size; i++)
        {
            byte* pixels = null;
            int width = 0;
            int height = 0;
            ImAtlas.GetTexDataAsRGBA32(
                i, &pixels, &width, &height);
            nint texture = _renderer.CreateTexture(
                pixels, width, height);
            _textures.Add(texture);
            ImAtlas.SetTexID(i, new ImTextureID(texture));
        }
        HasBuiltAtlas = true;
    }

    public IFontHandle NewGameFontHandle(GameFontStyle style) =>
        throw new NotSupportedException();
    public IDisposable SuppressAutoRebuild() => new PopScope(false);
    public void BuildFontsOnNextFrame() => BuildFontsImmediately();
    public Task BuildFontsAsync()
    {
        BuildFontsImmediately();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
            handle.Dispose();
        foreach (nint texture in _textures)
            _renderer.DestroyTexture(texture);
        _handles.Clear();
        _textures.Clear();
        BuildStepChange = null;
    }
}
