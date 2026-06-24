using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class BepInExTests
{
    // -- GetBepInExInstalledVersion -------------------------------------

    [Fact]
    public void GetBepInExInstalledVersion_returns_null_when_dll_is_missing()
    {
        var exeDir = Path.Combine(Path.GetTempPath(), "tpm-bepinex-test-" + Guid.NewGuid());
        Directory.CreateDirectory(exeDir);
        try
        {
            Assert.Null(TeknoParrotProfileScanner.GetBepInExInstalledVersion(exeDir));
        }
        finally
        {
            Directory.Delete(exeDir, recursive: true);
        }
    }

    [Fact]
    public void GetBepInExInstalledVersion_reads_real_file_version_info()
    {
        // Building a fake PE with an embedded version resource isn't
        // practical from a byte array -- copy this plugin's own compiled
        // DLL (which has a real FileVersionInfo) into the expected
        // location instead, and assert the parsed value matches what
        // FileVersionInfo itself reports directly on the source file.
        var sourceDll = typeof(TeknoParrotProfileScanner).Assembly.Location;
        var exeDir = Path.Combine(Path.GetTempPath(), "tpm-bepinex-test-" + Guid.NewGuid());
        var destDir = Path.Combine(exeDir, "BepInEx", "core");
        Directory.CreateDirectory(destDir);
        try
        {
            File.Copy(sourceDll, Path.Combine(destDir, "BepInEx.dll"));
            var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(sourceDll);
            var expectedVersion = $"{expected.FileMajorPart}.{expected.FileMinorPart}.{expected.FileBuildPart}";

            Assert.Equal(expectedVersion, TeknoParrotProfileScanner.GetBepInExInstalledVersion(exeDir));
        }
        finally
        {
            Directory.Delete(exeDir, recursive: true);
        }
    }

    // -- SelectBepInExAsset / GetBepInExLatestReleaseAsync ---------------

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void SelectBepInExAsset_skips_prerelease_and_picks_first_non_prerelease_match()
    {
        const string json = """
        [
          {
            "tag_name": "v6.0.0-pre.1",
            "prerelease": true,
            "assets": [
              { "name": "BepInEx_win_x64_6.0.0.zip", "browser_download_url": "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.1/BepInEx_win_x64_6.0.0.zip" }
            ]
          },
          {
            "tag_name": "v5.4.22",
            "prerelease": false,
            "assets": [
              { "name": "BepInEx_win_x86_5.4.22.zip", "browser_download_url": "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_win_x86_5.4.22.zip" },
              { "name": "BepInEx_win_x64_5.4.22.zip", "browser_download_url": "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_win_x64_5.4.22.zip" }
            ]
          }
        ]
        """;

        var release = TeknoParrotProfileScanner.SelectBepInExAsset(Parse(json));

        Assert.NotNull(release);
        Assert.Equal("5.4.22", release!.Version);
        Assert.Equal("BepInEx_win_x64_5.4.22.zip", release.FileName);
    }

    [Fact]
    public void SelectBepInExAsset_rejects_an_unsafe_download_host()
    {
        const string json = """
        [
          {
            "tag_name": "v5.4.22",
            "prerelease": false,
            "assets": [
              { "name": "BepInEx_win_x64_5.4.22.zip", "browser_download_url": "https://evil.example.com/BepInEx_win_x64_5.4.22.zip" }
            ]
          }
        ]
        """;

        Assert.Null(TeknoParrotProfileScanner.SelectBepInExAsset(Parse(json)));
    }

    [Fact]
    public void SelectBepInExAsset_returns_null_when_no_release_has_a_matching_asset()
    {
        const string json = """
        [
          { "tag_name": "v5.4.22", "prerelease": false, "assets": [ { "name": "other-file.txt", "browser_download_url": "https://github.com/x/y/other-file.txt" } ] }
        ]
        """;

        Assert.Null(TeknoParrotProfileScanner.SelectBepInExAsset(Parse(json)));
    }

    [Fact]
    public async Task GetBepInExLatestReleaseAsync_parses_release_list_response()
    {
        const string json = """
        [
          { "tag_name": "v5.4.22", "prerelease": false, "assets": [ { "name": "BepInEx_win_x64_5.4.22.zip", "browser_download_url": "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_win_x64_5.4.22.zip" } ] }
        ]
        """;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

        var release = await TeknoParrotProfileScanner.GetBepInExLatestReleaseAsync(http);

        Assert.NotNull(release);
        Assert.Equal("5.4.22", release!.Version);
    }

    [Fact]
    public async Task GetBepInExLatestReleaseAsync_aborts_immediately_on_a_4xx_response()
    {
        var callCount = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var release = await TeknoParrotProfileScanner.GetBepInExLatestReleaseAsync(http);

        Assert.Null(release);
        Assert.Equal(1, callCount);
    }

    // -- ExtractZipSafe ---------------------------------------------------

    private static string BuildZipWithEntries(params (string Name, string Content)[] entries)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "tpm-bepinex-zip-" + Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    [Fact]
    public void ExtractZipSafe_extracts_normal_entries_correctly()
    {
        var zipPath = BuildZipWithEntries(("file1.txt", "hello"), ("sub/file2.txt", "world"));
        var destDir = Path.Combine(Path.GetTempPath(), "tpm-bepinex-extract-" + Guid.NewGuid());
        try
        {
            TeknoParrotProfileScanner.ExtractZipSafe(zipPath, destDir);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(destDir, "file1.txt")));
            Assert.Equal("world", File.ReadAllText(Path.Combine(destDir, "sub", "file2.txt")));
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractZipSafe_rejects_a_traversal_entry_and_extracts_nothing()
    {
        var zipPath = BuildZipWithEntries(("safe.txt", "ok"), ("../../evil.txt", "pwned"));
        var destDir = Path.Combine(Path.GetTempPath(), "tpm-bepinex-extract-" + Guid.NewGuid());
        try
        {
            Assert.Throws<InvalidDataException>(() => TeknoParrotProfileScanner.ExtractZipSafe(zipPath, destDir));
            Assert.False(File.Exists(Path.Combine(destDir, "safe.txt")));
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractZipSafe_rejects_a_rooted_entry_and_extracts_nothing()
    {
        var zipPath = BuildZipWithEntries(("safe.txt", "ok"), ("C:/evil.txt", "pwned"));
        var destDir = Path.Combine(Path.GetTempPath(), "tpm-bepinex-extract-" + Guid.NewGuid());
        try
        {
            Assert.Throws<InvalidDataException>(() => TeknoParrotProfileScanner.ExtractZipSafe(zipPath, destDir));
            Assert.False(File.Exists(Path.Combine(destDir, "safe.txt")));
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    // -- Integration: CheckBepInExUpdates / ApplyBepInExUpdates ----------

    private static byte[] BuildFakeExe(ushort machine)
    {
        const int peOffset = 0x80;
        var buffer = new byte[peOffset + 6];
        buffer[0] = 0x4D; // 'M'
        buffer[1] = 0x5A; // 'Z'
        BitConverter.GetBytes(peOffset).CopyTo(buffer, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(buffer, peOffset);
        BitConverter.GetBytes(machine).CopyTo(buffer, peOffset + 4);
        return buffer;
    }

    private static void WriteFakeWinHttp(string exeDir, ushort machine)
    {
        File.WriteAllBytes(Path.Combine(exeDir, "winhttp.dll"), BuildFakeExe(machine));
    }

    private static void WriteFakeBepInEx(string exeDir, string version)
    {
        var sourceDll = typeof(TeknoParrotProfileScanner).Assembly.Location;
        var destDir = Path.Combine(exeDir, "BepInEx", "core");
        Directory.CreateDirectory(destDir);
        // The version string itself isn't actually embedded by this copy
        // (see GetBepInExInstalledVersion_reads_real_file_version_info for
        // why) -- these integration tests only need "BepInEx is installed
        // here at SOME version older/newer than latest," which the
        // [Version.TryParse]-based comparison in CheckBepInExUpdates
        // handles via the real copied assembly's own version, compared
        // against a synthetic "latest" version chosen to be newer.
        File.Copy(sourceDll, Path.Combine(destDir, "BepInEx.dll"), overwrite: true);
        _ = version;
    }

    [Fact]
    public void CheckBepInExUpdates_flags_an_x64_game_with_an_older_installed_version()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Game1", "game.exe");
        fixture.WriteProfile("Game1", "Game One", gamePath);
        var exeDir = Path.GetDirectoryName(gamePath)!;
        WriteFakeBepInEx(exeDir, "1.0.0");
        WriteFakeWinHttp(exeDir, 0x8664); // IMAGE_FILE_MACHINE_AMD64

        var installedInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(TeknoParrotProfileScanner).Assembly.Location);
        var futureVersion = $"{installedInfo.FileMajorPart + 1}.0.0";
        var latest = new BepInExRelease(futureVersion, "https://github.com/BepInEx/BepInEx/releases/download/vX/BepInEx_win_x64_X.zip", "BepInEx_win_x64_X.zip");

        var result = TeknoParrotProfileScanner.CheckBepInExUpdates(fixture.Settings, latest);

        var outdated = Assert.Single(result.OutdatedGames);
        Assert.Equal("Game1", outdated.ProfileCode);
        Assert.Equal(futureVersion, result.LatestVersion);
    }

    [Fact]
    public void CheckBepInExUpdates_skips_an_x86_install_without_touching_it()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Game1", "game.exe");
        fixture.WriteProfile("Game1", "Game One", gamePath);
        var exeDir = Path.GetDirectoryName(gamePath)!;
        WriteFakeBepInEx(exeDir, "1.0.0");
        WriteFakeWinHttp(exeDir, 0x014c); // IMAGE_FILE_MACHINE_I386

        var latest = new BepInExRelease("99.0.0", "https://github.com/BepInEx/BepInEx/releases/download/vX/BepInEx_win_x64_X.zip", "BepInEx_win_x64_X.zip");

        var result = TeknoParrotProfileScanner.CheckBepInExUpdates(fixture.Settings, latest);

        Assert.Empty(result.OutdatedGames);
        Assert.Equal(1, result.SkippedX86Count);
    }

    [Fact]
    public void CheckBepInExUpdates_ignores_a_game_with_no_bepinex_installed()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Game1", "game.exe");
        fixture.WriteProfile("Game1", "Game One", gamePath);

        var latest = new BepInExRelease("99.0.0", "https://github.com/BepInEx/BepInEx/releases/download/vX/BepInEx_win_x64_X.zip", "BepInEx_win_x64_X.zip");

        var result = TeknoParrotProfileScanner.CheckBepInExUpdates(fixture.Settings, latest);

        Assert.Empty(result.OutdatedGames);
        Assert.Equal(0, result.UpToDateCount);
        Assert.Equal(0, result.SkippedX86Count);
    }

    [Fact]
    public async Task ApplyBepInExUpdates_backs_up_and_extracts_into_the_outdated_games_folder()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Game1", "game.exe");
        fixture.WriteProfile("Game1", "Game One", gamePath);
        var exeDir = Path.GetDirectoryName(gamePath)!;
        WriteFakeBepInEx(exeDir, "1.0.0");
        WriteFakeWinHttp(exeDir, 0x8664);

        var zipBytes = BuildZipBytes(("BepInEx/core/BepInEx.dll", "new-version-payload"));
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var latest = new BepInExRelease("99.0.0", "https://github.com/BepInEx/BepInEx/releases/download/vX/BepInEx_win_x64_99.0.0.zip", "BepInEx_win_x64_99.0.0.zip");
        var outdated = new[] { new BepInExGameStatus("Game1", exeDir, "1.0.0", "99.0.0") };

        var result = await TeknoParrotProfileScanner.ApplyBepInExUpdates(http, latest, outdated);

        var updated = Assert.Single(result.UpdatedGames);
        Assert.Equal("Game1", updated.ProfileCode);
        Assert.Equal("99.0.0", updated.NewVersion);
        Assert.True(Directory.Exists(updated.BackupPath));
        Assert.True(File.Exists(Path.Combine(updated.BackupPath, "BepInEx", "core", "BepInEx.dll")));
        Assert.Equal("new-version-payload", File.ReadAllText(Path.Combine(exeDir, "BepInEx", "core", "BepInEx.dll")));
    }

    private static byte[] BuildZipBytes(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
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
