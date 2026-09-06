using System.IO.Compression;
using System.Text.Json;
using Poser.Application.Diagnostics;

namespace Poser.Application.Tests.Diagnostics;

public class DiagnosticRedactorTests
{
    [Fact]
    public void ConfiguredFolderNamesDoNotRenameReportSchema()
    {
        var redactor = new DiagnosticRedactor();
        redactor.RegisterPathsJson(JsonSerializer.Serialize(@"D:\Private Client\Poser\Library"));
        using var result = JsonDocument.Parse(redactor.ScrubJson("""
            {"Poser":"0.9.6.0","Settings":{"Library":{"Root":"D:\\Private Client\\Poser\\Library"}},"Log":["Private Client"]}
            """));
        Assert.Equal("0.9.6.0", result.RootElement.GetProperty("Poser").GetString());
        Assert.Contains("[path", result.RootElement.GetProperty("Settings").GetProperty("Library").GetProperty("Root").GetString());
        Assert.DoesNotContain("Private Client", result.RootElement.ToString());
    }

    [Fact]
    public void EveryZipMemberUsesDecodedRedactionIncludingFailuresAndDictionaryKeys()
    {
        var redactor = new DiagnosticRedactor();
        redactor.RegisterIdentity("Private Actor", "Actor 1");
        redactor.RegisterIdentity("PrivateUser", "[user]");
        var settings = new { Sources = new[] { @"D:\Private Shoot\Client Name", @"\\server\secret-share\poses" } };
        redactor.RegisterPathsJson(JsonSerializer.Serialize(settings));
        string report = JsonSerializer.Serialize(new
        {
            Settings = settings,
            Log = new[] { "IOException: The log could not be read: 'd:/private shoot/client name/Secret.pose'", "UnauthorizedAccessException: server secret-share Client Name" },
            Actions = new[] { new { Actor = "PRIVATE ACTOR", Error = @"Save failed: '\\SERVER\SECRET-SHARE\poses\Hidden.scene'", Owner = "PRIVATEUSER" } },
            Count = 42, Enabled = true,
        });
        string scene = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [@"D:\Private Shoot\Client Name\Other.pose"] = new { Name = "Private Actor", File = @"\\server\secret-share\poses\Hidden.scene" },
        });
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var member in new[] { ("report.json", report), ("scene.json", scene) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(member.Item1).Open());
                writer.Write(redactor.ScrubJson(member.Item2));
            }
        bytes.Position = 0;
        using var saved = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Equal(2, saved.Entries.Count);
        foreach (var entry in saved.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            using var parsed = JsonDocument.Parse(text);
            foreach (string secret in new[] { "Private Actor", "PrivateUser", "Private Shoot", "Client Name", "server", "secret-share", "Hidden", "Secret.pose", "Other.pose" })
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Actor 1", text);
            Assert.Contains(".scene", text);
            if (entry.Name == "report.json")
            {
                Assert.Equal(42, parsed.RootElement.GetProperty("Count").GetInt32());
                Assert.True(parsed.RootElement.GetProperty("Enabled").GetBoolean());
                Assert.Contains("IOException", text);
                Assert.Contains("UnauthorizedAccessException", text);
                Assert.Contains("Save failed", text);
                Assert.Contains(".pose", text);
            }
        }
    }

    [Theory]
    [InlineData(@"Load failed: 'E:\Unconfigured Private\Client\hidden.pose'")]
    [InlineData("Load failed: 'e:/Unconfigured Private/Client/hidden.pose'")]
    [InlineData(@"Load failed: '\\private-host\hidden-share\Client\hidden.pose'")]
    [InlineData(@"Load failed: 'E:\Client's private shoot\hidden.pose'")]
    public void UnknownAbsolutePathsAreRemovedButOperationAndExtensionRemain(string text)
    {
        string result = new DiagnosticRedactor().ScrubText(text);
        Assert.StartsWith("Load failed:", result);
        Assert.Contains(".pose", result);
        Assert.DoesNotContain("hidden", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Client", result);
    }

    [Fact]
    public void RedactedDictionaryKeysDoNotDiscardValuesAndUsefulRelativeAssetsRemain()
    {
        var redactor = new DiagnosticRedactor();
        redactor.RegisterIdentity("Alice", "Actor 1");
        redactor.RegisterIdentity("Bob", "Actor 1");
        using var result = JsonDocument.Parse(redactor.ScrubJson("""
            {"Alice": 1, "Bob": 2, "Asset":"chara/human/c0101/model.mdl", "Url":"https://github.com/midona-rhel/Poser"}
            """));
        Assert.Equal(1, result.RootElement.GetProperty("Actor 1").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("Actor 1 2").GetInt32());
        Assert.Equal("chara/human/c0101/model.mdl", result.RootElement.GetProperty("Asset").GetString());
        Assert.Equal("https://github.com/midona-rhel/Poser", result.RootElement.GetProperty("Url").GetString());
    }
}
