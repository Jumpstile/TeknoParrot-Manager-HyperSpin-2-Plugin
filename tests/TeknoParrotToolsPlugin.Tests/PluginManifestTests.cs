using System.Text.Json;
using Xunit;

namespace TeknoParrotToolsPlugin.Tests;

public class PluginManifestTests
{
    [Fact]
    public void Manifest_declares_optional_wizard_and_import_actions()
    {
        var manifestPath = FindManifestPath();
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.Equal("teknoparrot-tools", root.GetProperty("id").GetString());
        Assert.Equal("TeknoParrot Tools", root.GetProperty("name").GetString());
        var wizard = root.GetProperty("onboarding").GetProperty("wizards")[0];
        Assert.Equal("teknoparrot-tools-setup", wizard.GetProperty("id").GetString());
        Assert.Equal("first-run", wizard.GetProperty("autoStart").GetString());
        Assert.Equal("teknoparrot-tools-onboarding", wizard.GetProperty("theme").GetProperty("panelClass").GetString());

        var actions = root.GetProperty("actions").EnumerateArray()
            .Select(action => action.GetProperty("id").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("run_setup_wizard", actions);
        Assert.Contains("health_check", actions);
        Assert.Contains("scan_profiles", actions);
        Assert.Contains("preview_registration", actions);
        Assert.Contains("register_games", actions);
        Assert.Contains("repair_game_paths", actions);
        Assert.Contains("preview_sync", actions);
        Assert.Contains("sync_games", actions);
        Assert.Contains("backup_profiles", actions);
        Assert.Contains("restore_backup", actions);

        var runWizardAction = root.GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("id").GetString() == "run_setup_wizard");
        Assert.Equal("wizard", runWizardAction.GetProperty("type").GetString());
        Assert.Equal("teknoparrot-tools-setup", runWizardAction.GetProperty("wizard_id").GetString());

        var steps = wizard.GetProperty("steps").EnumerateArray()
            .ToDictionary(step => step.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("Start setup", steps["welcome"].GetProperty("buttonText").GetString());
        Assert.Equal("form", steps["paths"].GetProperty("type").GetString());
        Assert.Equal("Validate paths", steps["paths"].GetProperty("buttonText").GetString());
        Assert.True(steps["paths"].GetProperty("executeOnNext").GetBoolean());
        Assert.True(steps["paths"].GetProperty("form").GetArrayLength() >= 4);
        Assert.Equal("form", steps["import_options"].GetProperty("type").GetString());
        Assert.Equal("async-action", steps["register_games"].GetProperty("type").GetString());
        Assert.True(steps["register_games"].GetProperty("optional").GetBoolean());
        Assert.Equal("async-action", steps["scan_profiles"].GetProperty("type").GetString());
        Assert.True(steps["scan_profiles"].GetProperty("executeOnLoad").GetBoolean());
        Assert.Equal("async-action", steps["preview_sync"].GetProperty("type").GetString());
        Assert.Equal("async-action", steps["sync_games"].GetProperty("type").GetString());
        Assert.Equal("Finish", steps["finish"].GetProperty("buttonText").GetString());
    }

    private static string FindManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plugin.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find plugin.json from the test output directory.");
    }
}
