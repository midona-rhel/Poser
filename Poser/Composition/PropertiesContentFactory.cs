using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.UI;
using Poser.UI.Composition;
using Poser.UI.Controls;

namespace Poser.Composition;

/// <summary>Builds only a fresh presentation graph. All unlisted dependencies
/// come from the plugin's existing application/runtime owners.</summary>
internal sealed class PropertiesContentFactory(IServiceProvider services) : IPropertiesContentFactory
{
    public PropertiesContentLease Create(PropertiesContext context)
    {
        var presentation = new PresentationScope(services, context);
        var inspectorPresentation = new PresentationScope(services, context.PinEntities());
        try
        {
            var content = presentation.Get<PropertiesContent>();
            content.AttachInspector(
                inspectorPresentation.Get<PropertiesContext>(),
                inspectorPresentation.Get<PoseInspectorPane>(),
                inspectorPresentation.Get<PoseRailPane>(),
                inspectorPresentation.Get<CameraPane>());
            return new(content, () =>
            {
                inspectorPresentation.Dispose();
                presentation.Dispose();
            });
        }
        catch
        {
            inspectorPresentation.Dispose();
            presentation.Dispose();
            throw;
        }
    }
}

/// <summary>The main window's presentation graph: the one owner of the main
/// surface's panes, as each pop-out's lease owns its own. The graph is built
/// in the constructor so everything it resolves from the container is
/// disposed after it.</summary>
internal sealed class MainPresentation : IDisposable
{
    private readonly PresentationScope _scope;

    public MainPresentation(IServiceProvider services, SceneSession scene)
    {
        _scope = new PresentationScope(services, new PropertiesContext(scene));
        try
        {
            Content = _scope.Get<PropertiesContent>();
            Rail = _scope.Get<PoseRailPane>();
        }
        catch
        {
            _scope.Dispose();
            throw;
        }
    }

    public PropertiesContent Content { get; }
    public PoseRailPane Rail { get; }

    public void Dispose() => _scope.Dispose();
}

/// <summary>One surface's panes over one properties context. Pane types are
/// never registered in the container: a surface's scope creates and disposes
/// them.</summary>
internal sealed class PresentationScope(IServiceProvider shared, PropertiesContext context) : IServiceProvider, IDisposable
{
    private static readonly HashSet<Type> Local =
    [
        typeof(PropertiesContent), typeof(PoseInspectorPane), typeof(AnimationPane),
        typeof(AppearancePane), typeof(LightPane), typeof(CameraPane), typeof(EnvironmentPane),
        typeof(ScenePane), typeof(PropsPane), typeof(WorldObjectsPane), typeof(OverlayPane),
        typeof(PoseFileInspectorSection), typeof(GraphicalBonePane), typeof(ExpressionInspectorSection),
        typeof(ParentingSection), typeof(CompanionSection), typeof(EntityNameModal), typeof(IdleModExportDialog),
        typeof(PoseRailPane), typeof(SelectionSection),
    ];
    private readonly Dictionary<Type, object> _instances = new();
    private bool _disposed;

    public T Get<T>() where T : class => (T)GetService(typeof(T))!;

    public object? GetService(Type type)
    {
        if (type == typeof(PropertiesContext)) return context;
        if (!Local.Contains(type)) return shared.GetService(type);
        if (_instances.TryGetValue(type, out var existing)) return existing;
        var created = ActivatorUtilities.CreateInstance(this, type);
        _instances.Add(type, created);
        return created;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var instance in _instances.Values.Reverse())
            if (instance is IDisposable disposable) disposable.Dispose();
        _instances.Clear();
        context.Dispose();
    }
}
