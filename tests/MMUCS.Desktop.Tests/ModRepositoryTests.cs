using System.IO.Compression;
using System.Net;
using System.Text;
using MMUCS.Core;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The Mods view's repository (ModRepository): a mods.json listing zips, installed into _mods beside the game
/// without anything landing outside the mod's folder, checked against its SHA-256, and made by mods --pack.
/// </summary>
public class ModRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mmucs-repo-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_dir, "COMI.LA0");
    private string Mods => Path.Combine(_dir, "_mods");

    public ModRepositoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static ZipArchive Zip(params (string Name, string Text)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    [Fact]
    public void A_mods_json_lists_its_mods_with_zips_from_its_own_address_and_leaves_out_ones_that_aren_t_folder_names()
    {
        var index = ModRepository.Parse("""
            { "name": "Ours", "mods": [
              { "id": "Wide", "version": "1.2.0", "zip": "zips/Wide-1.2.0.zip", "sha256": "ABC", "size": 10, "category": "Images" },
              { "id": "../evil", "zip": "x.zip" },
              { "id": "NoZip" } ] }
            """, new Uri("https://someone.github.io/mods/mods.json"));
        Assert.Equal("Ours", index.Name);
        var wide = Assert.Single(index.Mods);
        Assert.Equal(("Wide", "1.2.0", "Images", "abc", 10L), (wide.Id, wide.Version, wide.Category, wide.Sha256, wide.Size));
        Assert.Equal("https://someone.github.io/mods/zips/Wide-1.2.0.zip", wide.Zip.ToString());
    }

    [Fact]
    public void Installing_writes_the_mod_s_folder_from_a_zip_of_it_or_of_its_files_and_an_update_keeps_it_off()
    {
        Directory.CreateDirectory(Mods);
        using (var zip = Zip(("Wide/mod.json", """{ "name": "Wide screen" }"""), ("Wide/rooms/a.png", "png")))
            ModRepository.Install(zip, "Wide", Mods, "1.0.0");
        var mod = Assert.Single(ModRepository.Scan(Game));
        Assert.Equal(("Wide", "Wide screen", "1.0.0", true, "Images"), (mod.Id, mod.Name, mod.Version, mod.Enabled, mod.Category));

        // Switched off, then updated from a zip of just its files: it stays off, and the old files go.
        ModRepository.SetEnabled(mod.Folder, false);
        using (var zip = Zip(("mod.json", """{ "name": "Wide screen", "tracks": {} }""")))
            ModRepository.Install(zip, "Wide", Mods, "1.1.0");
        mod = Assert.Single(ModRepository.Scan(Game));
        Assert.Equal(("1.1.0", false, "Music"), (mod.Version, mod.Enabled, mod.Category));
        Assert.False(File.Exists(Path.Combine(mod.Folder, "rooms", "a.png")));
        Assert.Equal(["Wide"], Directory.GetDirectories(Mods).Select(Path.GetFileName));
    }

    [Fact]
    public void A_zip_with_a_file_that_would_land_outside_the_mod_s_folder_installs_nothing_and_leaves_the_old_one()
    {
        Directory.CreateDirectory(Mods);
        using (var zip = Zip(("mod.json", "{}"))) ModRepository.Install(zip, "Wide", Mods, "1.0.0");
        using (var bad = Zip(("mod.json", "{}"), ("../../escaped.txt", "x")))
            Assert.Throws<InvalidDataException>(() => ModRepository.Install(bad, "Wide", Mods, "2.0.0"));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.Equal("1.0.0", Assert.Single(ModRepository.Scan(Game)).Version);
        using (var none = Zip(("readme.txt", "x")))
            Assert.Throws<InvalidDataException>(() => ModRepository.Install(none, "Other", Mods));
        Assert.Throws<InvalidDataException>(() => ModRepository.Install(Zip(("mod.json", "{}")), "..", Mods));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.2", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "", true)]
    [InlineData("", "1.0.0", false)]
    [InlineData("beta", "alpha", true)]
    public void A_version_is_newer_as_versions_compare(string available, string installed, bool newer) =>
        Assert.Equal(newer, ModRepository.IsNewer(available, installed));

    /// <summary>Serves one byte array for any address.</summary>
    private sealed class Serve(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task A_packed_mod_downloads_and_installs_and_one_whose_sha256_differs_is_refused()
    {
        string source = Path.Combine(_dir, "shared", "Wide");
        Directory.CreateDirectory(Path.Combine(source, "rooms"));
        File.WriteAllText(Path.Combine(source, "mod.json"), """{ "enabled": true, "version": "1.3.0", "description": "Wider" }""");
        File.WriteAllText(Path.Combine(source, "rooms", "a.png"), "png");
        string repo = Path.Combine(_dir, "repo");
        var entry = ModRepository.Pack(source, Path.Combine(repo, "zips", "Wide-1.3.0.zip"), repo);
        Assert.Equal(("Wide", "1.3.0", "Images", "zips/Wide-1.3.0.zip"),
            (entry["id"].ToString(), entry["version"].ToString(), entry["category"].ToString(), entry["zip"].ToString()));

        string json = $$"""{ "mods": [ {{entry.ToJsonString()}} ] }""";
        var index = ModRepository.Parse(json, new Uri("https://example.test/mods.json"));
        byte[] zip = File.ReadAllBytes(Path.Combine(repo, "zips", "Wide-1.3.0.zip"));
        using var http = new HttpClient(new Serve(zip));
        await ModRepository.DownloadAsync(http, index.Mods[0], Game);
        var mod = Assert.Single(ModRepository.Scan(Game));
        Assert.Equal(("1.3.0", "Wider"), (mod.Version, mod.Description));
        Assert.True(File.Exists(Path.Combine(mod.Folder, "rooms", "a.png")));

        var tampered = index.Mods[0] with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => ModRepository.DownloadAsync(http, tampered, Game));
        Assert.Empty(Directory.GetFiles(Mods)); // the download is gone
    }
}
