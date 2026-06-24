using System.Net;
using System.Text;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class FfbPluginTests
{
    private const string SampleAutoSetupCmd = """
        @echo off
        cd "Daytona 3"
        rename dinput8.dll MAME64.dll
        cd..
        cd "Sega Rally"
        rename dinput8.dll MAME32.dll
        cd..
        """;

    [Fact]
    public async Task GetFfbPluginGameMapAsync_parses_autosetup_cmd_blocks()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleAutoSetupCmd, Encoding.UTF8, "text/plain")
        }));

        var map = await TeknoParrotProfileScanner.GetFfbPluginGameMapAsync(http);

        Assert.Equal(2, map.Count);
        Assert.Equal("MAME64.dll", map["Daytona 3"]);
        Assert.Equal("MAME32.dll", map["Sega Rally"]);
    }

    [Fact]
    public async Task GetFfbPluginGameMapAsync_returns_empty_map_on_4xx_without_retry()
    {
        var callCount = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var map = await TeknoParrotProfileScanner.GetFfbPluginGameMapAsync(http);

        Assert.Empty(map);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void CheckFfbPluginSetup_matches_a_registered_game_against_the_live_table()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Daytona3", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        var gameMap = new Dictionary<string, string> { ["Daytona 3"] = "MAME64.dll" };

        var result = TeknoParrotProfileScanner.CheckFfbPluginSetup(fixture.Settings, gameMap, gameCodes: null);

        var match = Assert.Single(result.MatchedGames);
        Assert.Equal("Daytona3", match.ProfileCode);
        Assert.Equal("MAME64.dll", match.DestDll);
    }

    [Fact]
    public void CheckFfbPluginSetup_rejects_an_unsafe_destination_filename()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Daytona3", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        var gameMap = new Dictionary<string, string> { ["Daytona 3"] = @"..\..\evil.dll" };

        var result = TeknoParrotProfileScanner.CheckFfbPluginSetup(fixture.Settings, gameMap, gameCodes: null);

        Assert.Empty(result.MatchedGames);
        var error = Assert.Single(result.ErrorProfiles);
        Assert.Equal("Daytona3", error);
    }

    [Fact]
    public void CheckFfbPluginSetup_skips_a_collision_where_destination_already_exists()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Daytona3", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(gamePath)!, "MAME64.dll"), "already here");
        var gameMap = new Dictionary<string, string> { ["Daytona 3"] = "MAME64.dll" };

        var result = TeknoParrotProfileScanner.CheckFfbPluginSetup(fixture.Settings, gameMap, gameCodes: null);

        Assert.Empty(result.MatchedGames);
        Assert.Equal(1, result.SkippedCollisionCount);
    }

    [Fact]
    public void CheckFfbPluginSetup_skips_a_game_already_covered_by_native_ffb_blaster_by_default()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Daytona3", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "Daytona3.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>1</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "Daytona3.xml"), """
            <GameProfile>
              <GamePath>placeholder</GamePath>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>1</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """.Replace("placeholder", gamePath));
        var gameMap = new Dictionary<string, string> { ["Daytona 3"] = "MAME64.dll" };

        var defaultResult = TeknoParrotProfileScanner.CheckFfbPluginSetup(fixture.Settings, gameMap, gameCodes: null);
        Assert.Empty(defaultResult.MatchedGames);
        Assert.Equal(1, defaultResult.SkippedNativeCount);

        var overrideResult = TeknoParrotProfileScanner.CheckFfbPluginSetup(fixture.Settings, gameMap, gameCodes: new[] { "Daytona3" });
        var match = Assert.Single(overrideResult.MatchedGames);
        Assert.Equal("Daytona3", match.ProfileCode);
    }

    [Fact]
    public async Task ApplyFfbPluginSetup_deploys_the_matching_architecture_dll()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Daytona3", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        var gameMap = new Dictionary<string, string> { ["Daytona 3"] = "MAME64.dll" };

        using var http = new HttpClient(new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"fake-dll:{req.RequestUri!.Segments[^1]}"))
        }));

        var result = await TeknoParrotProfileScanner.ApplyFfbPluginSetup(http, fixture.Settings, gameMap, gameCodes: null);

        var deployed = Assert.Single(result.DeployedGames);
        Assert.Equal("Daytona3", deployed.ProfileCode);
        Assert.Equal("MAME64.dll", deployed.DestDll);
        var destPath = Path.Combine(Path.GetDirectoryName(gamePath)!, "MAME64.dll");
        Assert.True(File.Exists(destPath));
    }

    [Fact]
    public async Task ApplyFfbPluginSetup_never_overwrites_an_existing_destination_file()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Daytona3", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        var destPath = Path.Combine(Path.GetDirectoryName(gamePath)!, "MAME64.dll");
        File.WriteAllText(destPath, "pre-existing content");
        var gameMap = new Dictionary<string, string> { ["Daytona 3"] = "MAME64.dll" };

        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("new-dll-bytes"))
        }));

        var result = await TeknoParrotProfileScanner.ApplyFfbPluginSetup(http, fixture.Settings, gameMap, gameCodes: null);

        Assert.Empty(result.DeployedGames);
        Assert.Equal("pre-existing content", File.ReadAllText(destPath));
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
