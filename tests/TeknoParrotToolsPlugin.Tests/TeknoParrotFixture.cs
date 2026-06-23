using TeknoParrotToolsPlugin;

namespace TeknoParrotToolsPlugin.Tests;

internal sealed class TeknoParrotFixture : IDisposable
{
    private readonly string tempRoot;

    public TeknoParrotFixture(bool createUserProfiles = true)
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "teknoparrot-tools-tests", Guid.NewGuid().ToString("N"));
        RootPath = Path.Combine(tempRoot, "TeknoParrot");
        UserProfilesPath = Path.Combine(RootPath, "UserProfiles");
        GameProfilesPath = Path.Combine(RootPath, "GameProfiles");
        IconsPath = Path.Combine(RootPath, "Icons");
        ExecutablePath = Path.Combine(RootPath, "TeknoParrotUi.exe");
        BackupPath = Path.Combine(tempRoot, "Backups");

        Directory.CreateDirectory(RootPath);
        if (createUserProfiles)
        {
            Directory.CreateDirectory(UserProfilesPath);
        }

        Directory.CreateDirectory(GameProfilesPath);
        Directory.CreateDirectory(IconsPath);
        File.WriteAllText(ExecutablePath, string.Empty);

        Settings = new TeknoParrotSettings
        {
            TeknoParrotRootPath = RootPath,
            GamesRootPath = Path.Combine(RootPath, "Games"),
            BackupPath = BackupPath
        };
    }

    public string RootPath { get; }
    public string UserProfilesPath { get; }
    public string GameProfilesPath { get; }
    public string IconsPath { get; }
    public string ExecutablePath { get; }
    public string BackupPath { get; }
    public TeknoParrotSettings Settings { get; }

    public string WriteGameExecutable(string fileName)
    {
        var gamePath = Path.Combine(RootPath, "Games", Path.GetFileNameWithoutExtension(fileName), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
        File.WriteAllText(gamePath, string.Empty);
        return gamePath;
    }

    public string WriteGameExecutable(string folderName, string fileName)
    {
        var gamePath = Path.Combine(RootPath, "Games", folderName, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
        File.WriteAllText(gamePath, string.Empty);
        return gamePath;
    }

    public string WriteProfileTemplate(string profileName, string description, string executableName, string executableName2 = "", bool hasTwoExecutables = false)
    {
        Directory.CreateDirectory(GameProfilesPath);
        var profilePath = Path.Combine(GameProfilesPath, $"{profileName}.xml");
        File.WriteAllText(profilePath, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <GameProfile>
              <Description>{{description}}</Description>
              <ExecutableName>{{executableName}}</ExecutableName>
              <ExecutableName2>{{executableName2}}</ExecutableName2>
              <HasTwoExecutables>{{hasTwoExecutables.ToString().ToLowerInvariant()}}</HasTwoExecutables>
            </GameProfile>
            """);

        return profilePath;
    }

    public TeknoParrotProfileGame WriteProfile(string profileName, string gameName, string gamePath)
    {
        Directory.CreateDirectory(UserProfilesPath);
        var profilePath = Path.Combine(UserProfilesPath, $"{profileName}.xml");
        File.WriteAllText(profilePath, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <GameProfile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <GameName>{{gameName}}</GameName>
              <GamePath>{{gamePath}}</GamePath>
              <ExtraParameters>-fullscreen</ExtraParameters>
              <TestMenuParameter>-t</TestMenuParameter>
              <TestMenuExtraParameters>-t</TestMenuExtraParameters>
              <IconName>Icons\{{profileName}}.png</IconName>
            </GameProfile>
            """);

        return TeknoParrotProfileScanner.ParseProfile(profilePath, RootPath, IconsPath);
    }

    public TeknoParrotProfileGame WriteDescriptionProfile(string profileName, string description, string gamePath, string executableName = "")
    {
        Directory.CreateDirectory(UserProfilesPath);
        var profilePath = Path.Combine(UserProfilesPath, $"{profileName}.xml");
        File.WriteAllText(profilePath, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <GameProfile>
              <Description>{{description}}</Description>
              <ExecutableName>{{executableName}}</ExecutableName>
              <GamePath>{{gamePath}}</GamePath>
            </GameProfile>
            """);

        return TeknoParrotProfileScanner.ParseProfile(profilePath, RootPath, IconsPath);
    }

    public string WriteEggmanDat(params (string GameName, string ProfileCode, string Executable)[] entries)
    {
        var datPath = Path.Combine(tempRoot, "collection.dat");
        var games = string.Join(Environment.NewLine, entries.Select(entry => $$"""
              <game name="{{entry.GameName}}">
                <rom name="disc1.bin" size="1" crc="00000000" />
                <rom name="disc2.bin" size="1" crc="00000000" />
                <GameProfile>{{entry.ProfileCode}}</GameProfile>
                <Executable>{{entry.Executable}}</Executable>
              </game>
            """));
        File.WriteAllText(datPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <datafile>
            {games}
            </datafile>
            """);
        return datPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
