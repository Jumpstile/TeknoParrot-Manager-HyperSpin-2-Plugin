using Xunit;
using TeknoParrotManagerHyperSpin2Plugin;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class ProfileSchemaTests
{
    [Fact]
    public void CheckProfileSchemaDrift_reports_unknown_fields_without_writing()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "Drift.xml"), """
            <GameProfile>
              <EmulationProfile>test</EmulationProfile>
              <ConfigValues><FieldInformation><FieldType>FutureType</FieldType></FieldInformation></ConfigValues>
              <FutureProfileField>value</FutureProfileField>
            </GameProfile>
            """);

        var result = TeknoParrotProfileScanner.CheckProfileSchemaDrift(fixture.GameProfilesPath);

        Assert.Equal(1, result.ScannedProfiles);
        var report = Assert.Single(result.Reports);
        Assert.Contains("FutureProfileField", report.UnknownElements);
        Assert.Contains("FutureType", report.UnknownFieldTypes);
    }

    [Fact]
    public void CheckProfileSchemaDrift_accepts_known_rc4_profile_fields()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "Known.xml"), """
            <GameProfile>
              <JoystickButtons>1</JoystickButtons>
              <ConfigValues><FieldInformation><FieldType>Numeric</FieldType></FieldInformation></ConfigValues>
            </GameProfile>
            """);

        var result = TeknoParrotProfileScanner.CheckProfileSchemaDrift(fixture.GameProfilesPath);

        Assert.Empty(result.Reports);
    }
}
