using Poser.Application.Companions;
using Poser.Domain.Companions;
using Poser.Game.Companions;

namespace Poser.Game.Tests.Companions;

public sealed class OrnamentCatalogRowsTests
{
    [Fact]
    public void Same_row_id_in_other_kinds_does_not_replace_ornament()
    {
        var ornament = OrnamentCatalogRows.Create(7, 123, 456, _ => "Parasol")!;
        var mount = new CompanionEntry(CompanionKind.Mount, 7, "Mount");
        var minion = new CompanionEntry(CompanionKind.Companion, 7, "Minion");
        var catalog = new CompanionCatalog();
        catalog.Publish([minion, mount, ornament]);
        Assert.Same(ornament, catalog.Find(CompanionKind.Ornament, 7));
        Assert.Same(mount, catalog.Find(CompanionKind.Mount, 7));
        Assert.Same(minion, catalog.Find(CompanionKind.Companion, 7));
        Assert.Equal(3, catalog.Search("7").Count);
    }
}
