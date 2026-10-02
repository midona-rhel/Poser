using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
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
        var presentation = new PresentationServices(services, context);
        try
        {
            var content = (PropertiesContent)presentation.GetService(typeof(PropertiesContent))!;
            return new(content, presentation.Dispose);
        }
        catch
        {
            presentation.Dispose();
            throw;
        }
    }

    private sealed class PresentationServices(IServiceProvider shared, PropertiesContext context) : IServiceProvider, IDisposable
    {
        private static readonly HashSet<Type> Local =
        [
            typeof(PropertiesContent), typeof(PoseInspectorPane), typeof(AnimationPane),
            typeof(AppearancePane), typeof(LightPane), typeof(CameraPane), typeof(EnvironmentPane),
            typeof(ScenePane), typeof(PropsPane), typeof(WorldObjectsPane), typeof(OverlayPane),
            typeof(PoseFileInspectorSection), typeof(GraphicalBonePane), typeof(ExpressionInspectorSection),
            typeof(ParentingSection), typeof(CompanionSection), typeof(EntityNameModal), typeof(IdleModExportDialog),
        ];
        private readonly Dictionary<Type, object> _instances = new();
        private bool _disposed;

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
}
