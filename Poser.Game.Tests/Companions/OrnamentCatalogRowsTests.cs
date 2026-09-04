using Poser.Application.Companions;
using Poser.Domain.Companions;
using Poser.Game.Companions;

namespace Poser.Game.Tests.Companions;

public sealed class OrnamentCatalogRowsTests
{
    [Theory]
    [InlineData("Blue parasol")]
    [InlineData("Blauer Sonnenschirm")]
    [InlineData("パラソル")]
    public void Modelled_ornament_uses_localized_action_name_unchanged(string name)
    {
        uint requestedId = 0;
        var entry = OrnamentCatalogRows.Create(7, 123, 456, id =>
        {
            requestedId = id;
            return name;
        });
        Assert.Equal(7u, requestedId);
        Assert.Equal(new CompanionEntry(CompanionKind.Ornament, 7, name, 456, 123), entry);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Unnamed_modelled_ornament_remains_searchable(string name)
    {
        var entry = OrnamentCatalogRows.Create(7, 123, 456, _ => name);
        Assert.NotNull(entry);
        Assert.Equal("Ornament 7", entry.Name);
        var catalog = new CompanionCatalog();
        catalog.Publish([entry]);
        Assert.Same(entry, Assert.Single(catalog.Search("Ornament 7", CompanionKind.Ornament)));
        Assert.Same(entry, Assert.Single(catalog.Search("7", CompanionKind.Ornament)));
    }

    [Theory]
    [InlineData(0u, 123u)]
    [InlineData(7u, 0u)]
    [InlineData(65536u, 123u)]
    public void Invalid_rows_are_excluded_before_name_resolution(uint id, uint model)
    {
        Assert.Null(OrnamentCatalogRows.Create(id, model, 456,
            _ => throw new InvalidOperationException("Invalid rows must not resolve names.")));
    }

    [Fact]
    public void Largest_representable_id_is_preserved()
    {
        var entry = OrnamentCatalogRows.Create(ushort.MaxValue, 123, 456, _ => "");
        Assert.NotNull(entry);
        Assert.Equal(ushort.MaxValue, entry.Id);
        Assert.Equal("Ornament 65535", entry.Name);
    }

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
