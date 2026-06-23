using System.Xml.Linq;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class GpuFixTests
{
    private static XDocument BoolFieldProfile(string fieldName, string value) => XDocument.Parse($$"""
        <GameProfile>
          <ConfigValues>
            <FieldInformation>
              <FieldName>{{fieldName}}</FieldName>
              <FieldType>Bool</FieldType>
              <FieldValue>{{value}}</FieldValue>
            </FieldInformation>
          </ConfigValues>
        </GameProfile>
        """);

    private static XDocument DropdownFieldProfile(string fieldName, string value, params string[] options)
    {
        var optionsXml = string.Join(Environment.NewLine, options.Select(o => $"        <string>{o}</string>"));
        return XDocument.Parse($$"""
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <FieldName>{{fieldName}}</FieldName>
                  <FieldType>Dropdown</FieldType>
                  <FieldValue>{{value}}</FieldValue>
                  <FieldOptions>
            {{optionsXml}}
                  </FieldOptions>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);
    }

    [Fact]
    public void EvaluateGpuFix_marks_bool_field_changed_for_amd()
    {
        var doc = BoolFieldProfile("EnableAmdFix", "0");

        var result = TeknoParrotProfileScanner.EvaluateGpuFix(doc, new[] { "EnableAmdFix" }, Array.Empty<string>(), "AMD");

        Assert.True(result.Eligible);
        Assert.False(result.UpToDate);
        var change = Assert.Single(result.Changes);
        Assert.Equal("EnableAmdFix", change.FieldName);
        Assert.Equal("0", change.OldValue);
        Assert.Equal("1", change.NewValue);
    }

    [Fact]
    public void EvaluateGpuFix_bool_field_unchanged_when_already_correct_for_non_amd()
    {
        var doc = BoolFieldProfile("EnableAmdFix", "0");

        var result = TeknoParrotProfileScanner.EvaluateGpuFix(doc, new[] { "EnableAmdFix" }, Array.Empty<string>(), "NVIDIA");

        Assert.True(result.Eligible);
        Assert.True(result.UpToDate);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void EvaluateGpuFix_dropdown_prefers_new_amd_driver_when_available()
    {
        var doc = DropdownFieldProfile("GPU Fix", "None", "None", "AMD", "New AMD Driver", "NVIDIA", "INTEL");

        var result = TeknoParrotProfileScanner.EvaluateGpuFix(doc, Array.Empty<string>(), new[] { "GPU Fix" }, "AMD");

        var change = Assert.Single(result.Changes);
        Assert.Equal("New AMD Driver", change.NewValue);
    }

    [Fact]
    public void EvaluateGpuFix_dropdown_falls_back_to_amd_when_new_amd_driver_unavailable()
    {
        var doc = DropdownFieldProfile("GPU Fix", "None", "None", "AMD", "NVIDIA", "INTEL");

        var result = TeknoParrotProfileScanner.EvaluateGpuFix(doc, Array.Empty<string>(), new[] { "GPU Fix" }, "AMD");

        var change = Assert.Single(result.Changes);
        Assert.Equal("AMD", change.NewValue);
    }

    [Fact]
    public void EvaluateGpuFix_dropdown_falls_back_to_none_for_unsupported_vendor()
    {
        var doc = DropdownFieldProfile("GPU Fix", "AMD", "None", "AMD");

        var result = TeknoParrotProfileScanner.EvaluateGpuFix(doc, Array.Empty<string>(), new[] { "GPU Fix" }, "Intel");

        var change = Assert.Single(result.Changes);
        Assert.Equal("None", change.NewValue);
    }

    [Fact]
    public void EvaluateGpuFix_not_eligible_when_no_matching_fields_present()
    {
        var doc = XDocument.Parse("<GameProfile><ConfigValues></ConfigValues></GameProfile>");

        var result = TeknoParrotProfileScanner.EvaluateGpuFix(doc, new[] { "EnableAmdFix" }, new[] { "GPU Fix" }, "AMD");

        Assert.False(result.Eligible);
        Assert.False(result.UpToDate);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void DiscoverGpuFixFieldNames_seeds_fallback_when_gameprofiles_missing()
    {
        var fields = TeknoParrotProfileScanner.DiscoverGpuFixFieldNames(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));

        Assert.False(fields.GameProfilesFound);
        Assert.Contains("EnableAmdFix", fields.BoolFields);
        Assert.Contains("AMDCrashFix", fields.BoolFields);
        Assert.Contains("AMDFix", fields.BoolFields);
        Assert.Contains("GPU Fix", fields.DropdownFields);
    }

    [Fact]
    public void DiscoverGpuFixFieldNames_adds_fields_discovered_from_game_profiles()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.GameProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <FieldName>EnableAMDCrashWorkaround</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
                <FieldInformation>
                  <FieldName>Graphics Vendor Fix</FieldName>
                  <FieldType>Dropdown</FieldType>
                  <FieldValue>None</FieldValue>
                  <FieldOptions>
                    <string>None</string>
                    <string>AMD</string>
                    <string>NVIDIA</string>
                    <string>INTEL</string>
                  </FieldOptions>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var fields = TeknoParrotProfileScanner.DiscoverGpuFixFieldNames(fixture.GameProfilesPath);

        Assert.True(fields.GameProfilesFound);
        Assert.Contains("EnableAMDCrashWorkaround", fields.BoolFields);
        Assert.Contains("Graphics Vendor Fix", fields.DropdownFields);
        // Fallback seeds are still present alongside discovered fields.
        Assert.Contains("EnableAmdFix", fields.BoolFields);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", "AMD")]
    [InlineData("NVIDIA GeForce RTX 4090", "NVIDIA")]
    [InlineData("Intel(R) UHD Graphics 770", "Intel")]
    [InlineData("Microsoft Basic Render Driver", null)]
    [InlineData("Some Unknown Adapter", null)]
    public void MatchVendorName_recognizes_known_vendor_strings(string adapterName, string? expectedVendor)
    {
        Assert.Equal(expectedVendor, TeknoParrotProfileScanner.MatchVendorName(adapterName));
    }

    [Fact]
    public void ApplyGpuFix_updates_eligible_profiles_and_writes_changes_to_disk()
    {
        using var fixture = new TeknoParrotFixture();
        var profilePath = Path.Combine(fixture.UserProfilesPath, "SomeGame.xml");
        File.WriteAllText(profilePath, """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <FieldName>EnableAmdFix</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var result = TeknoParrotProfileScanner.ApplyGpuFix(fixture.Settings, "AMD", dryRun: false);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Errors);
        Assert.Contains("SomeGame", result.UpdatedProfiles);

        var saved = XDocument.Load(profilePath);
        var value = saved.Descendants("FieldValue").Single().Value;
        Assert.Equal("1", value);
    }

    [Fact]
    public void ApplyGpuFix_dry_run_reports_changes_without_writing_to_disk()
    {
        using var fixture = new TeknoParrotFixture();
        var profilePath = Path.Combine(fixture.UserProfilesPath, "SomeGame.xml");
        const string original = """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <FieldName>EnableAmdFix</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>0</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """;
        File.WriteAllText(profilePath, original);

        var result = TeknoParrotProfileScanner.ApplyGpuFix(fixture.Settings, "AMD", dryRun: true);

        Assert.Equal(1, result.Updated);
        Assert.Equal(original, File.ReadAllText(profilePath));
    }

    [Fact]
    public void ApplyGpuFix_counts_already_correct_profiles_as_unchanged()
    {
        using var fixture = new TeknoParrotFixture();
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), """
            <GameProfile>
              <ConfigValues>
                <FieldInformation>
                  <FieldName>EnableAmdFix</FieldName>
                  <FieldType>Bool</FieldType>
                  <FieldValue>1</FieldValue>
                </FieldInformation>
              </ConfigValues>
            </GameProfile>
            """);

        var result = TeknoParrotProfileScanner.ApplyGpuFix(fixture.Settings, "AMD", dryRun: false);

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Unchanged);
    }
}
