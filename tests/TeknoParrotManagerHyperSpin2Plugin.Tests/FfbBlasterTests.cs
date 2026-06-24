using System.Xml.Linq;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class FfbBlasterTests
{
    [Fact]
    public void DiscoverFfbBlasterFieldNames_returns_empty_when_gameprofiles_missing()
    {
        var fields = TeknoParrotProfileScanner.DiscoverFfbBlasterFieldNames(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));

        Assert.Empty(fields);
    }

    [Fact]
    public void DiscoverFfbBlasterFieldNames_matches_via_category_name_on_newer_builds()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var fields = TeknoParrotProfileScanner.DiscoverFfbBlasterFieldNames(fixture.GameProfilesPath);

        Assert.Contains("FFB Blaster", fields);
    }

    [Fact]
    public void DiscoverFfbBlasterFieldNames_falls_back_to_field_name_on_older_builds()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <FieldName>BlasterFfbEnable</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var fields = TeknoParrotProfileScanner.DiscoverFfbBlasterFieldNames(fixture.GameProfilesPath);

        Assert.Contains("BlasterFfbEnable", fields);
    }

    [Fact]
    public void DiscoverFfbBlasterFieldNames_ignores_unrelated_bool_fields()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>Some Other Setting</CategoryName>
                  <FieldName>SomeOtherField</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var fields = TeknoParrotProfileScanner.DiscoverFfbBlasterFieldNames(fixture.GameProfilesPath);

        Assert.Empty(fields);
    }

    [Fact]
    public void EvaluateFfbBlaster_reports_eligible_and_a_pending_change_when_not_set()
    {
        var doc = XDocument.Parse("""
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var result = TeknoParrotProfileScanner.EvaluateFfbBlaster(doc, new[] { "FFB Blaster" });

        Assert.True(result.Eligible);
        Assert.False(result.UpToDate);
        var change = Assert.Single(result.Changes);
        Assert.Equal("EnableFfb", change.FieldName);
        Assert.Equal("1", change.NewValue);
    }

    [Fact]
    public void EvaluateFfbBlaster_reports_up_to_date_when_already_enabled()
    {
        var doc = XDocument.Parse("""
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

        var result = TeknoParrotProfileScanner.EvaluateFfbBlaster(doc, new[] { "FFB Blaster" });

        Assert.True(result.Eligible);
        Assert.True(result.UpToDate);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void EvaluateFfbBlaster_reports_not_eligible_when_field_is_absent()
    {
        var doc = XDocument.Parse("<GameProfile><ConfigValues></ConfigValues></GameProfile>");

        var result = TeknoParrotProfileScanner.EvaluateFfbBlaster(doc, new[] { "FFB Blaster" });

        Assert.False(result.Eligible);
        Assert.False(result.UpToDate);
    }

    [Fact]
    public void ApplyFfbBlasterSetup_enables_eligible_profiles_and_writes_changes_to_disk()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);
        var profilePath = Path.Combine(fixture.UserProfilesPath, "SomeGame.xml");
        File.WriteAllText(profilePath, """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var result = TeknoParrotProfileScanner.ApplyFfbBlasterSetup(fixture.Settings, dryRun: false);

        Assert.Equal(1, result.Updated);
        var updatedCode = Assert.Single(result.UpdatedProfiles);
        Assert.Equal("SomeGame", updatedCode);
        Assert.Contains("<FieldValue>1</FieldValue>", File.ReadAllText(profilePath));
    }

    [Fact]
    public void ApplyFfbBlasterSetup_does_not_write_when_dry_run()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);
        var profilePath = Path.Combine(fixture.UserProfilesPath, "SomeGame.xml");
        var original = """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <CategoryName>FFB Blaster</CategoryName>
                  <FieldName>EnableFfb</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """;
        File.WriteAllText(profilePath, original);

        var result = TeknoParrotProfileScanner.ApplyFfbBlasterSetup(fixture.Settings, dryRun: true);

        Assert.Equal(1, result.Updated);
        Assert.Equal(original, File.ReadAllText(profilePath));
    }
}
