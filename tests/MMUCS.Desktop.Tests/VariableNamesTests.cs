using MMUCS.Core.Scumm;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Names/variables.json (contributed names for the game's variables) stays usable.</summary>
public class VariableNamesTests
{
    [Fact]
    public void Built_in_map_is_embedded_and_every_entry_is_usable()
    {
        using var stream = typeof(VariableNames).Assembly.GetManifestResourceStream("variables.json");
        Assert.NotNull(stream);
        var json = new System.IO.StreamReader(stream).ReadToEnd();
        Assert.Empty(VariableNames.Validate(json));
    }

    [Fact]
    public void Engine_and_mapped_names_are_used_and_the_rest_stay_numbered()
    {
        Assert.Equal("have-msg", ScummScript.VarName(15));
        Assert.Equal("item-icon", ScummScript.VarName(266));
        Assert.Equal("var99999", ScummScript.VarName(99999));
        Assert.Equal("local3", ScummScript.VarName(0x40000003));
        Assert.True(VariableNames.IsName("item-icon"));
    }

    [Theory]
    [InlineData("""{ "Globals": { "300": { "Name": "Bad Name" } } }""")]
    [InlineData("""{ "Globals": { "300": { "Name": "var12" } } }""")]
    [InlineData("""{ "Globals": { "300": { "Name": "x-1" } } }""")]
    [InlineData("""{ "Globals": { "15": { "Name": "my-msg" } } }""")]
    [InlineData("""{ "Globals": { "300": { "Name": "dup" }, "301": { "Name": "dup" } } }""")]
    [InlineData("""{ "Globals": { "abc": { "Name": "fine" } } }""")]
    public void Unusable_entries_are_reported(string json) => Assert.NotEmpty(VariableNames.Validate(json));

    [Fact]
    public void Unnamed_entries_are_allowed() =>
        Assert.Empty(VariableNames.Validate("""{ "Globals": { "300": { "Name": "", "Note": "tracked" } } }"""));
}
