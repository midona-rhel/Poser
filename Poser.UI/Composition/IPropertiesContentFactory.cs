using System;
using Poser.Application.Selection;

namespace Poser.UI.Composition;

public interface IPropertiesContentFactory
{
    PropertiesContentLease Create(PropertiesContext context);
}

public sealed class PropertiesContentLease(PropertiesContent content, Action dispose) : IDisposable
{
    public PropertiesContent Content { get; } = content;
    public void Dispose() => dispose();
}
