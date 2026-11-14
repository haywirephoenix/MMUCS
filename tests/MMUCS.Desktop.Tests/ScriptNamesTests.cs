using MMUCS.Core.Scumm;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Names/scripts.json (contributed names for global and local scripts) stays usable.</summary>
public class ScriptNamesTests
{
    [Fact]
    public void Built_in_map_is_embedded_and_every_entry_is_usable()
    {
        using var stream = typeof(ScriptNames).Assembly.GetManifestResourceStream("scripts.json");
        Assert.NotNull(stream);
        var json = new System.IO.StreamReader(stream).ReadToEnd();
        Assert.Empty(ScriptNames.Validate(json));
    }

    [Theory]
    [InlineData("""{ "Globals": { "88": { "Name": "Dress Chicken" } } }""")]
    [InlineData("""{ "Globals": { "x": { "Name": "dress-chicken" } } }""")]
    [InlineData("""{ "Globals": { "1": { "Name": "boot" }, "2": { "Name": "boot" } } }""")]
    [InlineData("""{ "Locals": { "2013": { "Name": "place-chicken-flock" } } }""")]
    [InlineData("""{ "Locals": { "14:2013": { "Name": "a" }, "14:2007": { "Name": "a" } } }""")]
    public void Unusable_entries_are_reported(string json) => Assert.NotEmpty(ScriptNames.Validate(json));

    [Fact]
    public void A_local_name_may_repeat_in_another_room()
        => Assert.Empty(ScriptNames.Validate("""{ "Locals": { "14:2013": { "Name": "setup" }, "21:2013": { "Name": "setup" } } }"""));
}
