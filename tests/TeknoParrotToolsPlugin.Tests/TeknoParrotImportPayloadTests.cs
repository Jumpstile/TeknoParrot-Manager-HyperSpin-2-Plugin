using System.Text.Json;
using TeknoParrotToolsPlugin;
using Xunit;

namespace TeknoParrotToolsPlugin.Tests;

public class TeknoParrotImportPayloadTests
{
    [Fact]
    public void BuildTeknoParrotImportPayload_creates_canonical_system_and_emulator()
    {
        using var fixture = new TeknoParrotFixture();
        var game = fixture.WriteProfile("ID8", "Initial D8 Infinity", fixture.WriteGameExecutable("InitialD8.exe"));
        var scan = TeknoParrotProfileScanner.Scan(fixture.Settings);

        var payload = TeknoParrotToolsPluginMain.BuildTeknoParrotImportPayload(new[] { game }, fixture.Settings, scan);

        Assert.Equal(TeknoParrotToolsPluginMain.TeknoParrotSystemName, payload.DatabaseOperations.CreateSystem?.Name);
        Assert.Equal(TeknoParrotToolsPluginMain.TeknoParrotSystemReferenceId, payload.DatabaseOperations.CreateSystem?.ReferenceId);
        Assert.Equal(TeknoParrotToolsPluginMain.TeknoParrotAllowedExtensions, payload.DatabaseOperations.CreateSystem?.AllowedExtensions);
        Assert.Equal(fixture.UserProfilesPath, payload.DatabaseOperations.CreateSystem?.RomsPaths);
        Assert.False(payload.DatabaseOperations.CreateSystem?.SearchSubfolders);
        Assert.Equal("TeknoParrot", payload.DatabaseOperations.CreateEmulator?.Name);
        Assert.Equal("--profile=\"%rom.filename%.xml\" --startMinimized", TeknoParrotToolsPluginMain.TeknoParrotLaunchCommand);
        Assert.Equal(TeknoParrotToolsPluginMain.TeknoParrotLaunchCommand, payload.DatabaseOperations.CreateEmulator?.CommandLine);
        Assert.Equal(new[] { ".xml" }, payload.DatabaseOperations.CreateEmulator?.SupportedExtensions);
        Assert.Equal(TeknoParrotToolsPluginMain.TeknoParrotSystemName, payload.DatabaseOperations.AddGames?.SystemName);
    }

    [Fact]
    public void BuildHyperImportGame_uses_profile_xml_as_launch_rom()
    {
        using var fixture = new TeknoParrotFixture();
        var profile = fixture.WriteProfile("MarioKartDX", "Mario Kart Arcade GP DX", fixture.WriteGameExecutable("MKDX.exe"));

        var game = TeknoParrotToolsPluginMain.BuildHyperImportGame(profile);

        Assert.Equal("teknoparrot-mariokartdx", game.Id);
        Assert.Equal("Mario Kart Arcade GP DX", game.Title);
        Assert.Equal("MarioKartDX.xml", game.FileName);
        Assert.Equal(profile.ProfilePath, game.RomPath);
        Assert.Equal("MarioKartDX", game.GameReferenceId);
        Assert.Equal("MarioKartDX", game.TitleId);
        Assert.Equal(TeknoParrotToolsPluginMain.TeknoParrotSystemName, game.Source);
    }

    [Fact]
    public void Payload_serializes_system_metadata_fields_consumed_by_hyperhq()
    {
        using var fixture = new TeknoParrotFixture();
        var game = fixture.WriteProfile("abc", "After Burner Climax", fixture.WriteGameExecutable("abc.exe"));
        var scan = TeknoParrotProfileScanner.Scan(fixture.Settings);

        var payload = TeknoParrotToolsPluginMain.BuildTeknoParrotImportPayload(new[] { game }, fixture.Settings, scan);
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"referenceId\":\"97d957bb-1490-4c1f-b698-08dd285234a8\"", json);
        Assert.Contains("\"romsPaths\":", json);
        Assert.Contains("\"allowedExtensions\":\"exe|xml|zip\"", json);
        Assert.Contains("\"metadata\":", json);
    }
}
