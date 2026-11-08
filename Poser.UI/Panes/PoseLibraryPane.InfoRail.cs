using System.Numerics;

namespace Poser.UI;

public sealed partial class PoseLibraryPane
{
    public float DrawInfoRail(Vector2 origin, Vector2 size)
    {
        int selected = _vm.Selected;
        bool hasTile = selected >= 0 && selected < _vm.Tiles.Count;
        return _details.DrawInfoRail(origin, size, hasTile ? _vm.Tiles[selected] : null);
    }

    public void DrawObjectsRail(Vector2 origin, Vector2 size)
    {
        int selected = _vm.Selected;
        if (selected >= 0 && selected < _vm.Tiles.Count && selected < _tileKinds.Count)
            _details.DrawObjectsRail(origin, size, _vm.Tiles[selected], _tileKinds[selected]);
    }
}
