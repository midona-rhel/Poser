using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using NSubstitute;
using Poser.Application.Appearance;
using Poser.Domain.Appearance;
using Poser.Game.Appearance;

namespace Poser.ContractTests;

public sealed class ModelCatalogPublicationTests
{
    [Fact]
    public void Publish_freezes_rows_and_advances_the_version()
    {
        var catalog = new ModelCatalog();
        var source = new List<ModelCatalogEntry>
        {
            new ModelCatalogEntry(ModelCatalogKind.Minion, 1, "One", 0, 10),
        };

        Assert.Equal(0, catalog.PublicationVersion);
        Assert.False(catalog.IsLoaded);

        catalog.Publish(source);
        source[0] = new ModelCatalogEntry(
            ModelCatalogKind.Mount, 2, "Two", 0, 20);
        source.Add(new ModelCatalogEntry(
            ModelCatalogKind.Ornament, 3, "Three", 0, 30));

        Assert.Equal(1, catalog.PublicationVersion);
        Assert.Single(catalog.Entries);
        Assert.Equal("One", catalog.Entries[0].Name);
        Assert.Single(catalog.Search("One"));
        Assert.Empty(catalog.Search("Two"));
        var published = Assert.IsAssignableFrom<IList<ModelCatalogEntry>>(
            catalog.Entries);
        Assert.Throws<NotSupportedException>(() => published[0] = source[0]);

        catalog.Publish(source);
        Assert.Equal(2, catalog.PublicationVersion);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Single(catalog.Search("Two"));
    }

    [Fact]
    public void Loader_is_single_flight_and_reuses_a_successful_build()
    {
        var data = Substitute.For<IDataManager>();
        var log = Substitute.For<IPluginLog>();
        var catalog = new ModelCatalog();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int attempts = 0;
        data.GetExcelSheet<ENpcBase>().Returns(_ =>
        {
            Interlocked.Increment(ref attempts);
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return null!;
        });
        var loader = new ModelCatalogLoader(data, catalog, log);

        loader.EnsureLoaded();
        Assert.True(entered.Wait(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        loader.EnsureLoaded();
        Assert.True(loader.IsBuilding);
        Assert.Equal(1, Volatile.Read(ref attempts));

        release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => catalog.IsLoaded && !loader.IsBuilding,
            TimeSpan.FromSeconds(5)));
        loader.EnsureLoaded();
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public void Loader_keeps_failure_until_explicit_retry()
    {
        var data = Substitute.For<IDataManager>();
        var log = Substitute.For<IPluginLog>();
        var catalog = new ModelCatalog();
        int attempts = 0;
        data.GetExcelSheet<ENpcBase>().Returns(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("catalog unavailable");
            return null!;
        });
        var loader = new ModelCatalogLoader(data, catalog, log);

        loader.EnsureLoaded();
        Assert.True(SpinWait.SpinUntil(
            () => !loader.IsBuilding && loader.LastError is not null,
            TimeSpan.FromSeconds(5)));
        loader.EnsureLoaded();
        loader.EnsureLoaded();
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.False(catalog.IsLoaded);

        loader.Retry();
        Assert.True(SpinWait.SpinUntil(
            () => catalog.IsLoaded && !loader.IsBuilding,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Null(loader.LastError);
    }
}
