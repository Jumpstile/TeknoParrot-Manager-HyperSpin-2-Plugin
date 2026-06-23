using System.Net;
using System.Text;
using System.Text.Json;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class EggmanDatTests
{
    private static JsonElement ParseAssets(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void SelectEggmanDatAsset_picks_matching_zip_asset()
    {
        const string json = """
        {
          "assets": [
            { "name": "source-code.zip", "browser_download_url": "https://github.com/Eggmansworld/TeknoParrot/archive/refs/tags/2026-06-17.zip", "size": 100 },
            { "name": "TeknoParrot (2026-06-17) Collection (RomVault).zip", "browser_download_url": "https://github.com/Eggmansworld/TeknoParrot/releases/download/2026-06-17/TeknoParrot.Collection.RomVault.zip", "size": 150000000 }
          ]
        }
        """;

        var release = TeknoParrotProfileScanner.SelectEggmanDatAsset(ParseAssets(json));

        Assert.NotNull(release);
        Assert.Equal("TeknoParrot (2026-06-17) Collection (RomVault).zip", release!.FileName);
        Assert.Equal(150000000, release.SizeBytes);
    }

    [Fact]
    public void SelectEggmanDatAsset_returns_null_when_no_asset_matches()
    {
        const string json = """{ "assets": [ { "name": "readme.txt", "browser_download_url": "https://github.com/x/y/readme.txt", "size": 10 } ] }""";

        var release = TeknoParrotProfileScanner.SelectEggmanDatAsset(ParseAssets(json));

        Assert.Null(release);
    }

    [Fact]
    public void SelectEggmanDatAsset_rejects_non_github_download_url()
    {
        const string json = """
        {
          "assets": [
            { "name": "TeknoParrot Collection RomVault.zip", "browser_download_url": "https://evil.example.com/payload.zip", "size": 100 }
          ]
        }
        """;

        var release = TeknoParrotProfileScanner.SelectEggmanDatAsset(ParseAssets(json));

        Assert.Null(release);
    }

    [Fact]
    public void ResolveEggmanDatSavePath_strips_path_components_and_stays_contained()
    {
        var destinationDir = Path.Combine(Path.GetTempPath(), "tpm-eggman-test-" + Guid.NewGuid());

        var savePath = TeknoParrotProfileScanner.ResolveEggmanDatSavePath(destinationDir, "../../evil.zip");

        Assert.Equal(Path.Combine(destinationDir, "evil.zip"), savePath);
    }

    [Fact]
    public void ResolveEggmanDatSavePath_returns_null_for_empty_filename()
    {
        var savePath = TeknoParrotProfileScanner.ResolveEggmanDatSavePath(Path.GetTempPath(), "");

        Assert.Null(savePath);
    }

    [Fact]
    public async Task GetEggmanDatReleaseAsync_parses_release_response()
    {
        const string json = """
        {
          "assets": [
            { "name": "TeknoParrot Collection RomVault.zip", "browser_download_url": "https://github.com/Eggmansworld/TeknoParrot/releases/download/2026-06-17/TeknoParrot.Collection.RomVault.zip", "size": 42 }
          ]
        }
        """;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

        var release = await TeknoParrotProfileScanner.GetEggmanDatReleaseAsync(http);

        Assert.NotNull(release);
        Assert.Equal(42, release!.SizeBytes);
    }

    [Fact]
    public async Task DownloadEggmanDatAsync_writes_file_to_destination_and_cleans_up_temp_file()
    {
        var destinationDir = Path.Combine(Path.GetTempPath(), "tpm-eggman-test-" + Guid.NewGuid());
        try
        {
            var payload = Encoding.UTF8.GetBytes("fake zip contents");
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
            var release = new EggmanDatRelease(
                "https://github.com/Eggmansworld/TeknoParrot/releases/download/2026-06-17/TeknoParrot.Collection.RomVault.zip",
                "TeknoParrot Collection RomVault.zip",
                payload.Length);

            var savedPath = await TeknoParrotProfileScanner.DownloadEggmanDatAsync(http, release, destinationDir);

            Assert.NotNull(savedPath);
            Assert.True(File.Exists(savedPath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(savedPath!));
            Assert.False(File.Exists(savedPath + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(destinationDir))
            {
                Directory.Delete(destinationDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadEggmanDatAsync_returns_null_and_leaves_no_partial_file_on_http_error()
    {
        var destinationDir = Path.Combine(Path.GetTempPath(), "tpm-eggman-test-" + Guid.NewGuid());
        try
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            var release = new EggmanDatRelease(
                "https://github.com/Eggmansworld/TeknoParrot/releases/download/2026-06-17/TeknoParrot.Collection.RomVault.zip",
                "TeknoParrot Collection RomVault.zip",
                0);

            var savedPath = await TeknoParrotProfileScanner.DownloadEggmanDatAsync(http, release, destinationDir);

            Assert.Null(savedPath);
            Assert.False(File.Exists(Path.Combine(destinationDir, "TeknoParrot Collection RomVault.zip") + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(destinationDir))
            {
                Directory.Delete(destinationDir, recursive: true);
            }
        }
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
