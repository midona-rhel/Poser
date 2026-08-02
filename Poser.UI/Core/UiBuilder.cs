namespace Poser.UI;

/// <summary>
/// The build callback for a props-carrying root. The props travel BY
/// REFERENCE: a tree whose inputs change every frame must not have to close
/// over them (a closure allocates per frame) nor copy a wide struct through
/// the call, so <see cref="UiRoot.Render{TProps}"/> takes a static delegate
/// of this shape and hands it the caller's own storage.
/// </summary>
public delegate UiNode UiBuilder<TProps>(in TProps props);
