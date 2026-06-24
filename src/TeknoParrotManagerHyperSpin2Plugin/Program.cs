using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HyperAI.Plugin.Common;
using HyperAI.Plugin.SocketIO;

namespace TeknoParrotManagerHyperSpin2Plugin;

public static class TeknoParrotManagerHyperSpin2PluginMain
{
    internal const string PluginId = "teknoparrot-manager-hyperspin2-plugin";
    internal const string PluginName = "TeknoParrot Manager - HyperSpin 2 Plugin";
    internal const string PluginVersion = "0.11.3";
    internal const string WizardId = "teknoparrot-manager-hyperspin2-plugin-setup";
    internal const string TeknoParrotSystemName = "Arcade (TeknoParrot)";
    internal const string TeknoParrotSystemReferenceId = "97d957bb-1490-4c1f-b698-08dd285234a8";
    internal const string TeknoParrotSystemDescription = "TeknoParrot is a software project designed to run select PC-based arcade titles on personal computers, acting as a compatibility or translation layer rather than a traditional hardware emulator. It aims to preserve arcade history and bring the arcade experience to the PC.";
    internal const string TeknoParrotLaunchCommand = "--profile=\"%rom.filename%.xml\" --startMinimized";
    internal const string TeknoParrotAllowedExtensions = "exe|xml|zip";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static TeknoParrotSettings settings = new();
    private static PluginSocketIOClient? socketClient;
    private static bool useSocketIO;
    private static int socketServerPort;
    private static readonly HttpClient ProfileSetHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--version")
            {
                Console.WriteLine(PluginVersion);
                return;
            }

            var socketPortArg = args.FirstOrDefault(arg => arg.StartsWith("--socket-port=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(socketPortArg) &&
                int.TryParse(socketPortArg["--socket-port=".Length..], out socketServerPort))
            {
                useSocketIO = true;
            }
            else
            {
                var envPort = Environment.GetEnvironmentVariable("HYPERHQ_SOCKET_PORT") ??
                    Environment.GetEnvironmentVariable("HYPERAI_SOCKET_PORT");

                if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out socketServerPort))
                {
                    useSocketIO = true;
                }
            }

            if (useSocketIO)
            {
                await RunSocketIOMode();
                return;
            }

            await RunStdinStdoutMode();
        }
        catch (Exception ex)
        {
            await LogErrorAsync($"Plugin main error: {ex.Message}");
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
        }
    }

    private static async Task RunSocketIOMode()
    {
        try
        {
            var pluginId = Environment.GetEnvironmentVariable("HYPERHQ_PLUGIN_ID") ?? PluginId;
            var authToken = GenerateAuthToken();
            socketClient = new PluginSocketIOClient(pluginId, authToken, socketServerPort);

            socketClient.EventReceived += async (eventType, data) =>
            {
                if (eventType == "dbConnected" && settings.AutoSyncOnDbConnect)
                {
                    await SyncGames(JsonDocument.Parse("{}").RootElement);
                }
            };

            socketClient.OnEvent("request", async data => await HandleSocketIORequest(data));
            await socketClient.ConnectAsync();

            var timeoutAt = DateTime.UtcNow.AddSeconds(30);
            while (!socketClient.IsAuthenticated && DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(100);
            }

            if (!socketClient.IsAuthenticated)
            {
                throw new InvalidOperationException("Failed to authenticate with HyperHQ within 30 seconds.");
            }

            await socketClient.SubscribeToEventsAsync(new[] { "dbConnected", "systemChanged" });
            await socketClient.UpdateStatusAsync("connected", "TeknoParrot Manager - HyperSpin 2 Plugin connected and ready");

            while (socketClient.IsConnected)
            {
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            await LogErrorAsync($"Socket.IO mode error: {ex.Message}");
            if (socketClient != null)
            {
                await socketClient.UpdateStatusAsync("error", ex.Message);
            }
        }
        finally
        {
            socketClient?.Dispose();
        }
    }

    private static async Task RunStdinStdoutMode()
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await ProcessMessage(line);
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    private static async Task HandleSocketIORequest(JsonElement data)
    {
        if (socketClient == null)
        {
            return;
        }

        if (!data.TryGetProperty("id", out var idElement) ||
            !data.TryGetProperty("method", out var methodElement))
        {
            await LogErrorAsync("Invalid request: missing id or method.");
            return;
        }

        var requestId = idElement.GetString();
        var method = methodElement.GetString();
        var requestData = data.TryGetProperty("data", out var dataElement)
            ? dataElement
            : JsonDocument.Parse("{}").RootElement;

        var response = await DispatchMethod(method, requestData);
        await socketClient.EmitAsync("response", new
        {
            id = requestId,
            type = "response",
            data = response,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    internal static async Task<object> ProcessMessage(string input)
    {
        try
        {
            input = input.TrimStart('\uFEFF');
            var message = JsonSerializer.Deserialize<PluginMessage>(input, JsonOptions);
            if (message == null || string.IsNullOrWhiteSpace(message.Method))
            {
                return new { error = "Invalid plugin message." };
            }

            return await DispatchMethod(message.Method, message.Data ?? JsonDocument.Parse("{}").RootElement);
        }
        catch (Exception ex)
        {
            await LogErrorAsync($"Message processing error: {ex.Message}");
            return new { error = ex.Message };
        }
    }

    private static Task<object> DispatchMethod(string? method, JsonElement data)
    {
        return method switch
        {
            "initialize" => Initialize(data),
            "updateSettings" => Initialize(data),
            "update_settings" => Initialize(data),
            "execute" => Execute(data),
            "getStatus" => GetStatus(),
            "get_status" => GetStatus(),
            "onboardingStepExecute" => OnboardingStepExecute(data),
            "onboarding/step-execute" => OnboardingStepExecute(data),
            "shutdown" => Shutdown(),
            _ => Task.FromResult<object>(new { error = $"Unknown method: {method}" })
        };
    }

    private static Task<object> Initialize(JsonElement data)
    {
        if (data.TryGetProperty("settings", out var settingsElement))
        {
            settings = MergeSettings(settings, settingsElement);
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            settings = MergeSettings(settings, data);
        }

        return GetStatus();
    }

    private static async Task<object> PreviewRegisterGames(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var aids = await TeknoParrotProfileScanner.BuildRegistrationAidsAsync(settings, ProfileSetHttpClient, LogAsyncSink).ConfigureAwait(false);
        return TeknoParrotProfileScanner.RegisterGames(settings, dryRun: true, aids.DatIndex, aids.ProfileSet);
    }

    private static async Task<object> RegisterGames(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var aids = await TeknoParrotProfileScanner.BuildRegistrationAidsAsync(settings, ProfileSetHttpClient, LogAsyncSink).ConfigureAwait(false);
        var result = TeknoParrotProfileScanner.RegisterGames(settings, dryRun, aids.DatIndex, aids.ProfileSet);
        return result with { BackupPath = backup?.BackupPath };
    }

    private static async Task<object> RegisterGamesForWizard(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var gamesRootPath = TeknoParrotProfileScanner.ResolveGamesRootPathForSettings(settings);
        if (string.IsNullOrWhiteSpace(gamesRootPath) || !Directory.Exists(gamesRootPath))
        {
            return new
            {
                success = true,
                skipped = true,
                statusMessage = "No games root folder configured; existing UserProfiles will be scanned next."
            };
        }

        return await RegisterGames(data).ConfigureAwait(false);
    }

    private static void LogAsyncSink(string message) => Console.Error.WriteLine($"[{PluginId}] INFO: {message}");

    private static object PreviewControlPropagation(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var overrides = TeknoParrotProfileScanner.LoadControlOverrides(settings.ControlOverridesPath, LogAsyncSink);
        return TeknoParrotProfileScanner.PropagateControls(settings, overrides, dryRun: true);
    }

    private static object PropagateControls(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var overrides = TeknoParrotProfileScanner.LoadControlOverrides(settings.ControlOverridesPath, LogAsyncSink);
        var result = TeknoParrotProfileScanner.PropagateControls(settings, overrides, dryRun);
        return result with { BackupPath = backup?.BackupPath };
    }

    private static object RunDeviceSurvey(JsonElement data)
    {
        var answers = new DeviceSurveyAnswers(
            HasXbox: GetBool(data, "hasXbox"),
            HasArcade: GetBool(data, "hasArcade"),
            HasTrackball: GetBool(data, "hasTrackball"),
            HasSpinner: GetBool(data, "hasSpinner"),
            HasWheel: GetBool(data, "hasWheel"),
            HasGun: GetBool(data, "hasGun"),
            HasKeyboard: GetBool(data, "hasKeyboard"));

        return TeknoParrotProfileScanner.RunDeviceSurvey(answers);
    }

    private static object PreviewCrosshairs(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        return TeknoParrotProfileScanner.PreviewCrosshairs(settings);
    }

    private static object DeployCrosshairs(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var p1Name = GetString(data, "p1Name");
        var p2Name = GetString(data, "p2Name");
        if (string.IsNullOrWhiteSpace(p1Name) || string.IsNullOrWhiteSpace(p2Name))
        {
            return new { success = false, error = "p1Name and p2Name are required." };
        }

        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var hideCursor = GetBool(data, "hideCursor");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.DeployCrosshairs(settings, p1Name, p2Name, hideCursor, dryRun, LogAsyncSink);
        return new { result, backup_path = backup?.BackupPath };
    }

    private static object HideCursor(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.HideCursorForLightgunGames(settings, dryRun);
        return new { result, backup_path = backup?.BackupPath };
    }

    // Resolves the GPU vendor to use for GpuFix: an explicit "vendor"
    // override in the action payload takes precedence, otherwise auto-detect
    // via WMI (Windows-only; returns null off-Windows or if detection
    // fails). Returns an error response in place of a vendor when neither
    // source produces one, or when an override doesn't match a known
    // vendor name -- this plugin never guesses a GPU vendor.
    private static (string? Vendor, GpuVendorDetection? Detection, object? Error) ResolveGpuVendor(JsonElement data)
    {
        var vendorOverride = GetString(data, "vendor");
        if (!string.IsNullOrWhiteSpace(vendorOverride))
        {
            if (vendorOverride is not ("AMD" or "NVIDIA" or "Intel"))
            {
                return (null, null, new { success = false, error = $"Unrecognized vendor '{vendorOverride}'. Use AMD, NVIDIA, or Intel." });
            }

            return (vendorOverride, null, null);
        }

        var detection = TeknoParrotProfileScanner.DetectGpuVendor(LogAsyncSink);
        if (string.IsNullOrWhiteSpace(detection.Vendor))
        {
            return (null, detection, new
            {
                success = false,
                error = "Could not auto-detect your GPU vendor. Pass \"vendor\": \"AMD\", \"NVIDIA\", or \"Intel\" explicitly.",
                detected_name = detection.Name
            });
        }

        return (detection.Vendor, detection, null);
    }

    private static object PreviewGpuFix(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var (vendor, detection, error) = ResolveGpuVendor(data);
        if (vendor is null)
        {
            return error!;
        }

        var result = TeknoParrotProfileScanner.ApplyGpuFix(settings, vendor, dryRun: true, LogAsyncSink);
        return new { success = true, result, detected_name = detection?.Name };
    }

    private static object ApplyGpuFix(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var (vendor, detection, error) = ResolveGpuVendor(data);
        if (vendor is null)
        {
            return error!;
        }

        var backup = TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.ApplyGpuFix(settings, vendor, dryRun: false, LogAsyncSink);
        return new { success = true, result, detected_name = detection?.Name, backup_path = backup?.BackupPath };
    }

    // Read-only: checks the configured ReShade DLL's version against
    // reshade.me's latest release, and reports its Authenticode signature
    // status. Does not deploy anything.
    private static async Task<object> CheckReShadeUpdate(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        if (string.IsNullOrWhiteSpace(settings.ReShadeSourceDllPath) || !File.Exists(settings.ReShadeSourceDllPath))
        {
            return new { success = false, error = "Set the \"ReShade DLL (64-bit)\" setting to a ReShade DLL you already have first." };
        }

        string? currentVersion = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(settings.ReShadeSourceDllPath);
            currentVersion = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            LogAsyncSink($"ReShade: could not read version info -- {ex.Message}");
        }

        var signature = TeknoParrotProfileScanner.CheckReShadeDllSignature(settings.ReShadeSourceDllPath);
        var latest = await TeknoParrotProfileScanner.GetReShadeLatestVersionAsync(ProfileSetHttpClient, LogAsyncSink).ConfigureAwait(false);

        var upToDate = latest is not null && currentVersion is not null &&
                       Version.TryParse(currentVersion, out var current) && Version.TryParse(latest, out var latestVersion) &&
                       current >= latestVersion;

        return new
        {
            success = true,
            current_version = currentVersion,
            latest_version = latest,
            up_to_date = latest is null ? (bool?)null : upToDate,
            signature_status = signature.Status,
            signature_status_text = TeknoParrotProfileScanner.GetSignatureStatusText(signature.Status),
            signature_signer = signature.Signer
        };
    }

    private static object PreviewReShadeSetup(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        if (string.IsNullOrWhiteSpace(settings.ReShadeSourceDllPath) || !File.Exists(settings.ReShadeSourceDllPath))
        {
            return new { success = false, error = "Set the \"ReShade DLL (64-bit)\" setting to a ReShade DLL you already have first." };
        }

        var gameCodes = GetStringArray(data, "gameCodes");
        var result = TeknoParrotProfileScanner.ApplyReShadeSetup(settings, gameCodes, dryRun: true, LogAsyncSink);
        return new { success = true, result };
    }

    private static object ApplyReShadeSetup(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        if (string.IsNullOrWhiteSpace(settings.ReShadeSourceDllPath) || !File.Exists(settings.ReShadeSourceDllPath))
        {
            return new { success = false, error = "Set the \"ReShade DLL (64-bit)\" setting to a ReShade DLL you already have first." };
        }

        var gameCodes = GetStringArray(data, "gameCodes");
        var backup = TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.ApplyReShadeSetup(settings, gameCodes, dryRun: false, LogAsyncSink);
        return new { success = true, result, backup_path = backup?.BackupPath };
    }

    private static object PreviewDgVoodoo2Setup(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        if (string.IsNullOrWhiteSpace(settings.DgVoodoo2SourcePath) || !Directory.Exists(settings.DgVoodoo2SourcePath))
        {
            return new { success = false, error = "Set the \"dgVoodoo2 Folder\" setting to a folder containing your dgVoodoo2 DLLs first." };
        }

        if (!TeknoParrotProfileScanner.AllDgVoodoo2Dlls.Any(dll => File.Exists(Path.Combine(settings.DgVoodoo2SourcePath, dll))))
        {
            return new { success = false, error = "No dgVoodoo2 DLLs found in the configured folder. Expected one or more of: D3D8.dll, DDraw.dll, D3DImm.dll, Glide2x.dll, Glide3x.dll." };
        }

        var gameCodes = GetStringArray(data, "gameCodes");
        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(settings, gameCodes, dryRun: true, LogAsyncSink);
        return new { success = true, result };
    }

    private static object ApplyDgVoodoo2Setup(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        if (string.IsNullOrWhiteSpace(settings.DgVoodoo2SourcePath) || !Directory.Exists(settings.DgVoodoo2SourcePath))
        {
            return new { success = false, error = "Set the \"dgVoodoo2 Folder\" setting to a folder containing your dgVoodoo2 DLLs first." };
        }

        if (!TeknoParrotProfileScanner.AllDgVoodoo2Dlls.Any(dll => File.Exists(Path.Combine(settings.DgVoodoo2SourcePath, dll))))
        {
            return new { success = false, error = "No dgVoodoo2 DLLs found in the configured folder. Expected one or more of: D3D8.dll, DDraw.dll, D3DImm.dll, Glide2x.dll, Glide3x.dll." };
        }

        var gameCodes = GetStringArray(data, "gameCodes");
        var backup = TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(settings, gameCodes, dryRun: false, LogAsyncSink);
        return new { success = true, result, backup_path = backup?.BackupPath };
    }

    private static object RepairGamePaths(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.RepairGamePaths(settings, dryRun);
        return result with { BackupPath = backup?.BackupPath };
    }

    private static async Task<object> Execute(JsonElement data)
    {
        var action = GetString(data, "action") ?? "get_status";
        var previousSettings = settings;
        settings = MergeSettings(settings, data);

        var response = action switch
        {
            "run_setup_wizard" => new { success = true, wizard_id = WizardId },
            "scan_profiles" => BuildScanResponse(TeknoParrotProfileScanner.Scan(settings)),
            "scan_games" => BuildScanResponse(TeknoParrotProfileScanner.Scan(settings)),
            "health_check" => BuildScanResponse(TeknoParrotProfileScanner.Scan(settings)),
            "get_status" => await GetStatus(),
            "status" => await GetStatus(),
            "preview_registration" => await PreviewRegisterGames(data),
            "register_games" => await RegisterGames(data),
            "repair_game_paths" => RepairGamePaths(data),
            "preview_control_propagation" => PreviewControlPropagation(data),
            "propagate_controls" => PropagateControls(data),
            "device_survey" => RunDeviceSurvey(data),
            "preview_crosshairs" => PreviewCrosshairs(data),
            "deploy_crosshairs" => DeployCrosshairs(data),
            "hide_cursor" => HideCursor(data),
            "preview_gpu_fix" => PreviewGpuFix(data),
            "apply_gpu_fix" => ApplyGpuFix(data),
            "check_reshade_update" => await CheckReShadeUpdate(data),
            "preview_reshade_setup" => PreviewReShadeSetup(data),
            "apply_reshade_setup" => ApplyReShadeSetup(data),
            "preview_dgvoodoo2_setup" => PreviewDgVoodoo2Setup(data),
            "apply_dgvoodoo2_setup" => ApplyDgVoodoo2Setup(data),
            "preview_sync" => await SyncGames(SetDryRun(data)),
            "sync_games" => await SyncGames(data),
            "backup_profiles" => BackupProfiles(settings),
            "restore_backup" => RestoreBackup(settings, data),
            "check_eggman_dat_update" => await CheckEggmanDatUpdate(),
            "download_eggman_dat" => await DownloadEggmanDat(),
            "onboardingStepExecute" => await OnboardingStepExecute(data),
            _ => new { error = $"Unsupported action: {action}" }
        };

        settings = previousSettings.MergeWith(settings);
        return response;
    }

    private static Task<object> GetStatus()
    {
        var scan = TeknoParrotProfileScanner.Scan(settings);
        return Task.FromResult<object>(new
        {
            success = scan.Errors.Count == 0,
            plugin_id = PluginId,
            status = "ready",
            system = new
            {
                name = TeknoParrotSystemName,
                referenceId = TeknoParrotSystemReferenceId,
                category = "Arcade"
            },
            paths = new
            {
                root = scan.RootPath,
                executable = scan.ExecutablePath,
                userProfiles = scan.UserProfilesPath,
                gameProfiles = scan.GameProfilesPath,
                gamesRoot = scan.GamesRootPath,
                icons = scan.IconsPath
            },
            profiles_count = scan.Games.Count,
            profile_health = BuildProfileHealth(scan),
            warnings = scan.Warnings,
            errors = scan.Errors
        });
    }

    private static async Task<object> SyncGames(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var scan = TeknoParrotProfileScanner.Scan(settings);
        var payload = BuildTeknoParrotImportPayload(scan.Games, settings, scan);

        if (scan.Errors.Count > 0)
        {
            return new
            {
                success = false,
                dry_run = true,
                games_found = scan.Games.Count,
                errors = scan.Errors,
                warnings = scan.Warnings,
                payload
            };
        }

        if (dryRun || !useSocketIO || socketClient?.IsAuthenticated != true)
        {
            return new
            {
                success = true,
                dry_run = true,
                games_found = scan.Games.Count,
                warnings = scan.Warnings,
                payload
            };
        }

        var result = await HyperImportSyncClient.SendAsync(
            socketClient,
            useSocketIO,
            "teknoparrot:sync_games",
            payload,
            LogAsync,
            LogWarningAsync,
            LogErrorAsync);

        return new
        {
            success = result.Success,
            dry_run = false,
            games_synced = payload.DatabaseOperations.AddGames?.Games.Count ?? 0,
            completed_operations = result.CompletedOperations,
            failed_operations = result.FailedOperations,
            warnings = scan.Warnings
        };
    }

    internal static HyperImportSyncPayload<TeknoParrotProfileGame> BuildTeknoParrotImportPayload(
        IEnumerable<TeknoParrotProfileGame> games,
        TeknoParrotSettings payloadSettings,
        TeknoParrotScanResult? scan = null)
    {
        var gameList = games.OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var executable = FirstNonEmpty(scan?.ExecutablePath, payloadSettings.ExecutablePath, "TeknoParrotUi.exe");
        var userProfilesPath = FirstNonEmpty(scan?.UserProfilesPath, payloadSettings.UserProfilesPath, string.Empty);
        var downloadMedia = payloadSettings.DownloadMedia;

        return new HyperImportSyncPayload<TeknoParrotProfileGame>
        {
            Games = gameList,
            DatabaseOperations = new HyperImportDatabaseOperations
            {
                CreateSystem = new HyperImportSystem
                {
                    Name = TeknoParrotSystemName,
                    DisplayName = TeknoParrotSystemName,
                    ReferenceId = TeknoParrotSystemReferenceId,
                    Description = TeknoParrotSystemDescription,
                    Platform = "Arcade",
                    RomsPaths = userProfilesPath,
                    AllowedExtensions = TeknoParrotAllowedExtensions,
                    SearchSubfolders = false,
                    GamesCount = gameList.Count,
                    MediaOptions = BuildTeknoParrotMediaOptions(downloadMedia),
                    MediaFolders = BuildTeknoParrotMediaFolders(downloadMedia),
                    Metadata = new
                    {
                        source = "teknoparrot_tools_plugin",
                        hyperhqSystemId = TeknoParrotSystemReferenceId,
                        category = "Arcade",
                        developer = "TeknoGods",
                        manufacturer = "Various",
                        media = "HDD",
                        maxControllers = "4",
                        emulated = true
                    }
                },
                CreateEmulator = new HyperImportEmulator
                {
                    Name = "TeknoParrot",
                    DisplayName = "TeknoParrot",
                    CommandLine = TeknoParrotLaunchCommand,
                    Command = TeknoParrotLaunchCommand,
                    Description = "TeknoParrot UI direct profile launcher using UserProfiles XML files as ROM targets",
                    Executable = executable,
                    Platform = "Arcade",
                    Type = "Arcade",
                    SystemName = TeknoParrotSystemName,
                    SupportedExtensions = new[] { ".xml" },
                    LaunchMethod = "command_line",
                    LinkEmulator = new
                    {
                        command = TeknoParrotLaunchCommand,
                        description = "Launch TeknoParrot profiles through TeknoParrotUi.exe"
                    },
                    Metadata = new
                    {
                        source = "teknoparrot_tools_plugin",
                        launcher = "TeknoParrot",
                        requires_client = true,
                        profileFolder = userProfilesPath
                    }
                },
                AddGames = new HyperImportGamesBatch
                {
                    SystemName = TeknoParrotSystemName,
                    Games = gameList.Select(BuildHyperImportGame).ToList()
                }
            },
            SyncMetadata = new HyperImportSyncMetadata
            {
                PluginId = PluginId,
                SyncTime = DateTime.UtcNow,
                GamesCount = gameList.Count
            }
        };
    }

    internal static HyperImportGame BuildHyperImportGame(TeknoParrotProfileGame game)
    {
        var title = FirstNonEmpty(game.Title, game.ProfileName, "TeknoParrot Game");
        var profileName = FirstNonEmpty(game.ProfileName, NormalizeImportId(title));
        var id = $"teknoparrot-{NormalizeImportId(profileName)}";
        var installPath = !string.IsNullOrWhiteSpace(game.GamePath)
            ? Path.GetDirectoryName(game.GamePath) ?? string.Empty
            : string.Empty;

        return new HyperImportGame
        {
            Id = id,
            Title = title,
            Name = title,
            FileName = Path.GetFileName(game.ProfilePath),
            RomPath = game.ProfilePath,
            GameReferenceId = profileName,
            Description = $"TeknoParrot user profile for {title}.",
            Developer = string.Empty,
            Publisher = string.Empty,
            DisplayName = title,
            IsInstalled = !string.IsNullOrWhiteSpace(game.GamePath) && File.Exists(game.GamePath),
            InstallPath = installPath,
            Platform = "Arcade",
            Source = TeknoParrotSystemName,
            LaunchCommandType = 0,
            LaunchCommandFilePath = string.Empty,
            LaunchCommandExtraParams = game.ExtraParameters,
            TitleId = profileName,
            Metadata = new
            {
                source = "teknoparrot_tools_plugin",
                profileName,
                profilePath = game.ProfilePath,
                gamePath = game.GamePath,
                gamePath2 = game.GamePath2,
                executableName = game.ExecutableName,
                executableName2 = game.ExecutableName2,
                hasTwoExecutables = game.HasTwoExecutables,
                iconPath = game.IconPath,
                extraParameters = game.ExtraParameters,
                testMenuParameter = game.TestMenuParameter,
                testMenuExtraParameters = game.TestMenuExtraParameters,
                hyperhqMetadataSystemId = TeknoParrotSystemReferenceId,
                hyperhqSearchName = title,
                warnings = game.Warnings
            }
        };
    }

    internal static object BuildTeknoParrotMediaOptions(bool enabled)
    {
        return new
        {
            themes = false,
            boxart = enabled,
            wheels = enabled,
            banners = enabled,
            carts = false,
            boxbacks = false,
            pointers = false,
            bgImages = enabled,
            marquees = enabled,
            bezels = false,
            videos = false
        };
    }

    internal static object BuildTeknoParrotMediaFolders(bool enabled)
    {
        return new
        {
            downloadBoxes2D = enabled,
            downloadBackgroundsGame = enabled,
            downloadLogosGame = enabled,
            downloadGameMedias2D = false,
            downloadGameThemes = false,
            downloadMarqueesGame = enabled,
            downloadVideoSnaps = false
        };
    }

    private static Task<object> OnboardingStepExecute(JsonElement data)
    {
        var stepId = GetString(data, "stepId") ?? GetString(data, "step_id") ?? "welcome";
        var stepData = data.TryGetProperty("data", out var dataElement) ? dataElement : data;
        settings = MergeSettings(settings, stepData);

        return stepId switch
        {
            "welcome" => Task.FromResult<object>(new { success = true, nextStepId = "paths" }),
            "paths" => GetStatus(),
            "import_options" => GetStatus(),
            "register_games" => RegisterGamesForWizard(stepData),
            "scan_profiles" => Task.FromResult<object>(BuildScanResponse(TeknoParrotProfileScanner.Scan(settings))),
            "preview_sync" => SyncGames(SetDryRun(stepData)),
            "sync_games" => SyncGames(stepData),
            "backup_profiles" => Task.FromResult<object>(BackupProfiles(settings)),
            "finish" => GetStatus(),
            _ => Task.FromResult<object>(new { success = false, error = $"Unknown onboarding step: {stepId}" })
        };
    }

    private static Task<object> Shutdown()
    {
        socketClient?.Dispose();
        return Task.FromResult<object>(new { status = "shutdown" });
    }

    private static object BuildScanResponse(TeknoParrotScanResult scan)
    {
        return new
        {
            success = scan.Errors.Count == 0,
            system = TeknoParrotSystemName,
            system_reference_id = TeknoParrotSystemReferenceId,
            root_path = scan.RootPath,
            executable_path = scan.ExecutablePath,
            user_profiles_path = scan.UserProfilesPath,
            game_profiles_path = scan.GameProfilesPath,
            games_root_path = scan.GamesRootPath,
            icons_path = scan.IconsPath,
            profiles_count = scan.Games.Count,
            games = scan.Games,
            profile_health = BuildProfileHealth(scan),
            warnings = scan.Warnings,
            errors = scan.Errors
        };
    }

    private static object BuildProfileHealth(TeknoParrotScanResult scan)
    {
        var valid = scan.Games
            .Where(game => !string.IsNullOrWhiteSpace(game.GamePath) && File.Exists(game.GamePath))
            .Select(game => game.ProfileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var empty = scan.Games
            .Where(game => string.IsNullOrWhiteSpace(game.GamePath))
            .Select(game => game.ProfileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var broken = scan.Games
            .Where(game => !string.IsNullOrWhiteSpace(game.GamePath) && !File.Exists(game.GamePath))
            .Select(game => game.ProfileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            registered_profiles = scan.Games.Count,
            valid_game_paths = valid.Length,
            broken_game_paths = broken.Length,
            empty_game_paths = empty.Length,
            valid,
            broken,
            empty
        };
    }

    internal static object BackupProfiles(TeknoParrotSettings backupSettings)
    {
        var scan = TeknoParrotProfileScanner.Scan(backupSettings);
        if (scan.Errors.Count > 0)
        {
            return new { success = false, errors = scan.Errors };
        }

        var backupRoot = FirstNonEmpty(
            backupSettings.BackupPath,
            !string.IsNullOrWhiteSpace(scan.RootPath) ? Path.Combine(scan.RootPath, "Backups", "HyperHQ") : string.Empty,
            Path.Combine(AppContext.BaseDirectory, "Backups", "HyperHQ"));

        var backupPath = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupPath);

        var copied = 0;
        foreach (var file in Directory.GetFiles(scan.UserProfilesPath!, "*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(backupPath, Path.GetFileName(file)), overwrite: false);
            copied++;
        }

        return new { success = true, backup_path = backupPath, profiles_backed_up = copied };
    }

    private static ProfileBackupResult TryBackupProfilesForMutation(TeknoParrotSettings backupSettings)
    {
        var scan = TeknoParrotProfileScanner.Scan(backupSettings);
        if (string.IsNullOrWhiteSpace(scan.UserProfilesPath) || !Directory.Exists(scan.UserProfilesPath))
        {
            return new ProfileBackupResult(true, null, null);
        }

        var profileFiles = Directory.GetFiles(scan.UserProfilesPath, "*.xml", SearchOption.TopDirectoryOnly);
        if (profileFiles.Length == 0)
        {
            return new ProfileBackupResult(true, null, null);
        }

        var response = BackupProfiles(backupSettings);
        var responseElement = JsonSerializer.SerializeToElement(response, JsonOptions);
        var success = responseElement.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind == JsonValueKind.True;
        if (!success)
        {
            var error = responseElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : "Profile backup failed before mutation.";
            if (responseElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array)
            {
                error = string.Join("; ", errorsElement.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString()));
            }

            return new ProfileBackupResult(false, string.IsNullOrWhiteSpace(error) ? "Profile backup failed before mutation." : error, null);
        }

        var backupPath = responseElement.TryGetProperty("backup_path", out var pathElement) &&
            pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
        return new ProfileBackupResult(true, null, backupPath);
    }

    private static object RestoreBackup(TeknoParrotSettings restoreSettings, JsonElement data)
    {
        var backupPath = GetString(data, "backupPath") ?? GetString(data, "backup_path");
        if (string.IsNullOrWhiteSpace(backupPath) || !Directory.Exists(backupPath))
        {
            return new { success = false, error = "A valid backupPath is required." };
        }

        var scan = TeknoParrotProfileScanner.Scan(restoreSettings);
        if (string.IsNullOrWhiteSpace(scan.UserProfilesPath))
        {
            return new { success = false, error = "UserProfiles path is not configured." };
        }

        Directory.CreateDirectory(scan.UserProfilesPath);

        var preRestoreBackupPath = string.Empty;
        var currentProfiles = Directory.GetFiles(scan.UserProfilesPath, "*.xml", SearchOption.TopDirectoryOnly);
        if (currentProfiles.Length > 0)
        {
            var backupRoot = FirstNonEmpty(
                restoreSettings.BackupPath,
                !string.IsNullOrWhiteSpace(scan.RootPath) ? Path.Combine(scan.RootPath, "Backups", "HyperHQ") : string.Empty,
                Path.Combine(AppContext.BaseDirectory, "Backups", "HyperHQ"));

            preRestoreBackupPath = Path.Combine(backupRoot, "pre-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(preRestoreBackupPath);

            foreach (var file in currentProfiles)
            {
                File.Copy(file, Path.Combine(preRestoreBackupPath, Path.GetFileName(file)), overwrite: false);
            }
        }

        var restored = 0;
        foreach (var file in Directory.GetFiles(backupPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(scan.UserProfilesPath, Path.GetFileName(file)), overwrite: true);
            restored++;
        }

        return new
        {
            success = true,
            restored_profiles = restored,
            user_profiles_path = scan.UserProfilesPath,
            pre_restore_backup_path = preRestoreBackupPath
        };
    }

    // Read-only: checks whether a newer Eggman/RomVault collection dat
    // release is available, without downloading anything. Ported from the
    // PowerShell tool's "check for a newer release" prompt on later runs.
    private static async Task<object> CheckEggmanDatUpdate()
    {
        var release = await TeknoParrotProfileScanner.GetEggmanDatReleaseAsync(ProfileSetHttpClient, LogAsyncSink).ConfigureAwait(false);
        if (release is null)
        {
            return new { success = false, error = "Could not find a collection dat release on GitHub. See the log for details." };
        }

        var haveSameFile = !string.IsNullOrWhiteSpace(settings.EggmanDatPath) &&
                            string.Equals(Path.GetFileName(settings.EggmanDatPath), release.FileName, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(settings.EggmanDatPath);

        return new
        {
            success = true,
            file_name = release.FileName,
            size_mb = Math.Round(release.SizeBytes / 1024d / 1024d, 1),
            download_url = release.DownloadUrl,
            already_have_this_release = haveSameFile
        };
    }

    // Downloads the latest Eggman/RomVault collection dat ZIP into this
    // plugin's own folder and reports the saved path. Ported from the
    // PowerShell tool's Invoke-EggmanDatDownloadInteractive, minus the
    // interactive save-location prompt (HyperHQ plugins are non-interactive
    // here -- the save location is always this plugin's own folder). Does
    // NOT update the eggmanDatPath setting itself; copy the returned path
    // into the "Collection Dat File" setting (or re-run Setup Wizard) to
    // start using it.
    private static async Task<object> DownloadEggmanDat()
    {
        var release = await TeknoParrotProfileScanner.GetEggmanDatReleaseAsync(ProfileSetHttpClient, LogAsyncSink).ConfigureAwait(false);
        if (release is null)
        {
            return new { success = false, error = "Could not find a collection dat release on GitHub. See the log for details." };
        }

        var destinationDir = Path.Combine(AppContext.BaseDirectory, "EggmanDat");
        var savedPath = await TeknoParrotProfileScanner.DownloadEggmanDatAsync(ProfileSetHttpClient, release, destinationDir, LogAsyncSink).ConfigureAwait(false);
        if (savedPath is null)
        {
            return new { success = false, error = "Download failed. See the log for details." };
        }

        return new
        {
            success = true,
            eggman_dat_path = savedPath,
            file_name = release.FileName,
            size_mb = Math.Round(release.SizeBytes / 1024d / 1024d, 1),
            message = "Downloaded. Paste this path into the \"Collection Dat File\" setting to start using it."
        };
    }

    private static TeknoParrotSettings MergeSettings(TeknoParrotSettings current, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return current;
        }

        TeknoParrotSettings? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<TeknoParrotSettings>(data.GetRawText(), JsonOptions);
        }
        catch
        {
            return current;
        }

        var merged = current.MergeWith(parsed ?? new TeknoParrotSettings());
        if (!HasProperty(data, nameof(TeknoParrotSettings.DownloadMedia)))
        {
            merged.DownloadMedia = current.DownloadMedia;
        }

        if (!HasProperty(data, nameof(TeknoParrotSettings.AutoSyncOnDbConnect)))
        {
            merged.AutoSyncOnDbConnect = current.AutoSyncOnDbConnect;
        }

        return merged;
    }

    private static JsonElement SetDryRun(JsonElement data)
    {
        var map = data.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(data.GetRawText(), JsonOptions) ?? new Dictionary<string, object?>()
            : new Dictionary<string, object?>();
        map["dryRun"] = true;
        return JsonSerializer.SerializeToElement(map, JsonOptions);
    }

    internal static string NormalizeImportId(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized.Trim('-');
    }

    internal static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string? GetString(JsonElement data, string propertyName)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty(propertyName, out var value) &&
            value.ValueKind != JsonValueKind.Null &&
            value.ValueKind != JsonValueKind.Undefined)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool GetBool(JsonElement data, string propertyName)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static List<string>? GetStringArray(JsonElement data, string propertyName)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()!)
            .ToList();
    }

    private static bool HasProperty(JsonElement data, string propertyName)
    {
        return data.ValueKind == JsonValueKind.Object &&
            data.EnumerateObject().Any(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateAuthToken()
    {
        return Environment.GetEnvironmentVariable("HYPERHQ_AUTH_CHALLENGE") ??
            Environment.GetEnvironmentVariable("HYPERAI_PLUGIN_CHALLENGE") ??
            $"standalone-{Guid.NewGuid():N}";
    }

    private static Task LogAsync(string message)
    {
        Console.Error.WriteLine($"[{PluginId}] INFO: {message}");
        return Task.CompletedTask;
    }

    private static Task LogWarningAsync(string message)
    {
        Console.Error.WriteLine($"[{PluginId}] WARN: {message}");
        return Task.CompletedTask;
    }

    private static Task LogErrorAsync(string message)
    {
        Console.Error.WriteLine($"[{PluginId}] ERROR: {message}");
        return Task.CompletedTask;
    }
}

internal sealed class PluginMessage
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

internal sealed record ProfileBackupResult(bool Success, string? Error, string? BackupPath);

internal sealed record TeknoParrotProfileTemplate(string Code, string TemplatePath, string ExecutableName);

public sealed record TeknoParrotRegistrationItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("game_path")] string GamePath,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("match_type")] string MatchType);

public sealed record TeknoParrotRegistrationIssue(
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("codes")] string[] Codes,
    [property: JsonPropertyName("best_guess")] string? BestGuess,
    [property: JsonPropertyName("best_score")] double BestScore,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record TeknoParrotRegistrationResult(
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("dry_run")]
    bool DryRun,
    [property: JsonPropertyName("errors")]
    IReadOnlyList<string> Errors,
    [property: JsonPropertyName("registered")]
    IReadOnlyList<TeknoParrotRegistrationItem> Registered,
    [property: JsonPropertyName("already_registered")]
    IReadOnlyList<string> AlreadyRegistered,
    [property: JsonPropertyName("ambiguous")]
    IReadOnlyList<TeknoParrotRegistrationIssue> Ambiguous,
    [property: JsonPropertyName("unmatched")]
    IReadOnlyList<string> Unmatched,
    [property: JsonPropertyName("backup_path")]
    string? BackupPath);

public sealed record TeknoParrotRepairItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exe")] string? Exe,
    [property: JsonPropertyName("new_path")] string? NewPath);

public sealed record TeknoParrotRepairResult(
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("dry_run")]
    bool DryRun,
    [property: JsonPropertyName("errors")]
    IReadOnlyList<string> Errors,
    [property: JsonPropertyName("repairs")]
    IReadOnlyList<TeknoParrotRepairItem> Repairs,
    [property: JsonPropertyName("backup_path")]
    string? BackupPath);

public sealed class TeknoParrotSettings
{
    public string TeknoParrotRootPath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string UserProfilesPath { get; set; } = string.Empty;
    public string GameProfilesPath { get; set; } = string.Empty;
    public string GamesRootPath { get; set; } = string.Empty;
    public string IconsPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string EggmanDatPath { get; set; } = string.Empty;
    public string ControlOverridesPath { get; set; } = string.Empty;
    public int MinBoundForArchetype { get; set; } = 5;
    public string CrosshairsPath { get; set; } = string.Empty;
    public string ReShadeSourceDllPath { get; set; } = string.Empty;
    public string ReShadeSourceDll32Path { get; set; } = string.Empty;
    public string ReShadePresetPath { get; set; } = string.Empty;
    public string ReShadePresetsPath { get; set; } = string.Empty;
    public string DgVoodoo2SourcePath { get; set; } = string.Empty;
    public string DgVoodoo2PresetsPath { get; set; } = string.Empty;
    public bool DownloadMedia { get; set; } = true;
    public bool AutoSyncOnDbConnect { get; set; } = false;

    public TeknoParrotSettings MergeWith(TeknoParrotSettings other)
    {
        return new TeknoParrotSettings
        {
            TeknoParrotRootPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.TeknoParrotRootPath, TeknoParrotRootPath),
            ExecutablePath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.ExecutablePath, ExecutablePath),
            UserProfilesPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.UserProfilesPath, UserProfilesPath),
            GameProfilesPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.GameProfilesPath, GameProfilesPath),
            GamesRootPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.GamesRootPath, GamesRootPath),
            IconsPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.IconsPath, IconsPath),
            BackupPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.BackupPath, BackupPath),
            EggmanDatPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.EggmanDatPath, EggmanDatPath),
            ControlOverridesPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.ControlOverridesPath, ControlOverridesPath),
            MinBoundForArchetype = other.MinBoundForArchetype > 0 ? other.MinBoundForArchetype : MinBoundForArchetype,
            CrosshairsPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.CrosshairsPath, CrosshairsPath),
            ReShadeSourceDllPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.ReShadeSourceDllPath, ReShadeSourceDllPath),
            ReShadeSourceDll32Path = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.ReShadeSourceDll32Path, ReShadeSourceDll32Path),
            ReShadePresetPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.ReShadePresetPath, ReShadePresetPath),
            ReShadePresetsPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.ReShadePresetsPath, ReShadePresetsPath),
            DgVoodoo2SourcePath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.DgVoodoo2SourcePath, DgVoodoo2SourcePath),
            DgVoodoo2PresetsPath = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(other.DgVoodoo2PresetsPath, DgVoodoo2PresetsPath),
            DownloadMedia = other.DownloadMedia,
            AutoSyncOnDbConnect = other.AutoSyncOnDbConnect
        };
    }
}

public sealed class TeknoParrotProfileGame
{
    public string ProfileName { get; init; } = string.Empty;
    public string ProfilePath { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string GamePath { get; init; } = string.Empty;
    public string GamePath2 { get; init; } = string.Empty;
    public string ExecutableName { get; init; } = string.Empty;
    public string ExecutableName2 { get; init; } = string.Empty;
    public bool HasTwoExecutables { get; init; }
    public string IconPath { get; init; } = string.Empty;
    public string ExtraParameters { get; init; } = string.Empty;
    public string TestMenuParameter { get; init; } = string.Empty;
    public string TestMenuExtraParameters { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = new();
}

public sealed class TeknoParrotScanResult
{
    public string RootPath { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string UserProfilesPath { get; init; } = string.Empty;
    public string GameProfilesPath { get; init; } = string.Empty;
    public string GamesRootPath { get; init; } = string.Empty;
    public string IconsPath { get; init; } = string.Empty;
    public List<TeknoParrotProfileGame> Games { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

public static partial class TeknoParrotProfileScanner
{
    private const double FuzzyAutoThreshold = 0.72;

    // Minimum score gap required between the best and runner-up candidate
    // for the best one to be trusted as an auto-register decision. Without
    // this, two different profiles that both happen to score at or above
    // FuzzyAutoThreshold against the same folder name were resolved purely
    // by which one the candidate loop iterated to last -- no actual signal
    // preferred one over the other. Set to 0.1 (not a tighter value like
    // 0.05) per the original PowerShell tool's own audit: a real near-miss
    // example (a folder one character off from the real title vs. one
    // character over) produced a gap of ~0.083, which a tighter margin
    // would not have caught. Ported from teknoparrot-manager v0.99.19,
    // issue #15.
    private const double FuzzyTieMargin = 0.1;

    private static readonly string[] GameFileExtensions = { ".exe", ".elf", ".iso", ".gcm", ".gcz", ".bin", ".e4", ".zip", ".xbe", ".dll" };

    public static TeknoParrotScanResult Scan(TeknoParrotSettings settings)
    {
        var rootPath = ResolveRootPath(settings);
        var executablePath = ResolvePath(settings.ExecutablePath, rootPath, "TeknoParrotUi.exe");
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolvePath(settings.GameProfilesPath, rootPath, "GameProfiles");
        var gamesRootPath = ResolveGamesRootPath(settings);
        var iconsPath = ResolvePath(settings.IconsPath, rootPath, "Icons");

        var warnings = new List<string>();
        var errors = new List<string>();
        var games = new List<TeknoParrotProfileGame>();

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            warnings.Add("TeknoParrot root path is not configured.");
        }
        else if (!Directory.Exists(rootPath))
        {
            warnings.Add($"TeknoParrot root path was not found: {rootPath}");
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            warnings.Add("TeknoParrotUi.exe was not found. Set executablePath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            errors.Add("UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath.");
            return new TeknoParrotScanResult
            {
                RootPath = rootPath,
                ExecutablePath = executablePath,
                UserProfilesPath = userProfilesPath,
                GameProfilesPath = gameProfilesPath,
                GamesRootPath = gamesRootPath,
                IconsPath = iconsPath,
                Warnings = warnings,
                Errors = errors
            };
        }

        foreach (var profilePath in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                games.Add(ParseProfile(profilePath, rootPath, iconsPath));
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not parse {Path.GetFileName(profilePath)}: {ex.Message}");
            }
        }

        foreach (var duplicateTitle in games
            .GroupBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key))
        {
            warnings.Add($"Duplicate TeknoParrot profile title detected: {duplicateTitle}");
        }

        return new TeknoParrotScanResult
        {
            RootPath = rootPath,
            ExecutablePath = executablePath,
            UserProfilesPath = userProfilesPath,
            GameProfilesPath = gameProfilesPath,
            GamesRootPath = gamesRootPath,
            IconsPath = iconsPath,
            Games = games,
            Warnings = warnings,
            Errors = errors
        };
    }

    internal static string ResolveGamesRootPathForSettings(TeknoParrotSettings settings)
    {
        return ResolveGamesRootPath(settings);
    }

    internal static string ResolveGameProfilesPathForSettings(TeknoParrotSettings settings)
    {
        return ResolvePath(settings.GameProfilesPath, ResolveRootPath(settings), "GameProfiles");
    }

    // Builds the optional dat-index/profile-set registration aids from the
    // current settings. Fails soft: a missing/invalid EggmanDatPath yields
    // an empty dat index, and a failed GitHub fetch yields the local
    // GameProfiles listing instead (see FetchProfileCodeSetAsync). Callers
    // get plain registration behavior (matching the 2-arg RegisterGames
    // overload) when neither aid is available.
    internal static async Task<(IReadOnlyDictionary<string, TeknoParrotDatEntry> DatIndex, HashSet<string> ProfileSet)> BuildRegistrationAidsAsync(
        TeknoParrotSettings settings, HttpClient http, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var gameProfilesPath = ResolveGameProfilesPathForSettings(settings);

        var datIndex = !string.IsNullOrWhiteSpace(settings.EggmanDatPath) && File.Exists(settings.EggmanDatPath)
            ? BuildDatIndex(settings.EggmanDatPath, log)
            : new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase);

        var profileSet = await FetchProfileCodeSetAsync(http, gameProfilesPath, log, cancellationToken).ConfigureAwait(false);

        return (datIndex, profileSet);
    }

    internal static TeknoParrotProfileGame ParseProfile(string profilePath, string rootPath, string iconsPath)
    {
        var document = XDocument.Load(profilePath);
        var profileName = Path.GetFileNameWithoutExtension(profilePath);
        var title = TeknoParrotManagerHyperSpin2PluginMain.FirstNonEmpty(
            FirstElementValue(document, "Description"),
            FirstElementValue(document, "GameName"));
        var gamePath = FirstElementValue(document, "GamePath");
        var gamePath2 = FirstElementValue(document, "GamePath2");
        var executableName = FirstElementValue(document, "ExecutableName");
        var executableName2 = FirstElementValue(document, "ExecutableName2");
        var hasTwoExecutables = string.Equals(FirstElementValue(document, "HasTwoExecutables"), "true", StringComparison.OrdinalIgnoreCase);
        var iconName = FirstElementValue(document, "IconName");
        var extraParameters = FirstElementValue(document, "ExtraParameters");
        var testMenuParameter = FirstElementValue(document, "TestMenuParameter");
        var testMenuExtraParameters = FirstElementValue(document, "TestMenuExtraParameters");
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(title))
        {
            title = HumanizeProfileName(profileName);
            warnings.Add("Description and GameName were missing; using the profile filename.");
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            warnings.Add("GamePath is empty.");
        }
        else if (!File.Exists(gamePath))
        {
            warnings.Add($"GamePath does not exist: {gamePath}");
        }

        if (hasTwoExecutables && !string.IsNullOrWhiteSpace(executableName2))
        {
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                warnings.Add("GamePath2 could not be checked because GamePath is empty.");
            }
            else
            {
                var expectedGamePath2 = Path.Combine(Path.GetDirectoryName(gamePath) ?? string.Empty, executableName2.Trim());
                if (string.IsNullOrWhiteSpace(gamePath2) && File.Exists(expectedGamePath2))
                {
                    warnings.Add($"GamePath2 is empty; expected companion executable at {expectedGamePath2}");
                }
                else if (!string.IsNullOrWhiteSpace(gamePath2) && !File.Exists(gamePath2))
                {
                    warnings.Add($"GamePath2 does not exist: {gamePath2}");
                }
            }
        }

        var iconPath = ResolveIconPath(iconName, rootPath, iconsPath);

        return new TeknoParrotProfileGame
        {
            ProfileName = profileName,
            ProfilePath = profilePath,
            Title = title.Trim(),
            GamePath = gamePath.Trim(),
            GamePath2 = gamePath2.Trim(),
            ExecutableName = executableName.Trim(),
            ExecutableName2 = executableName2.Trim(),
            HasTwoExecutables = hasTwoExecutables,
            IconPath = iconPath,
            ExtraParameters = extraParameters.Trim(),
            TestMenuParameter = testMenuParameter.Trim(),
            TestMenuExtraParameters = testMenuExtraParameters.Trim(),
            Warnings = warnings
        };
    }

    internal static TeknoParrotRegistrationResult RegisterGames(
        TeknoParrotSettings settings,
        bool dryRun,
        IReadOnlyDictionary<string, TeknoParrotDatEntry>? datIndex = null,
        HashSet<string>? profileSet = null)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolvePath(settings.GameProfilesPath, rootPath, "GameProfiles");
        var gamesRootPath = ResolveGamesRootPath(settings);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(gameProfilesPath) || !Directory.Exists(gameProfilesPath))
        {
            errors.Add("GameProfiles folder was not found. Set gameProfilesPath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(gamesRootPath) || !Directory.Exists(gamesRootPath))
        {
            errors.Add("Games root folder was not found. Set gamesRootPath before registering profiles.");
        }

        if (string.IsNullOrWhiteSpace(userProfilesPath))
        {
            errors.Add("UserProfiles path could not be resolved.");
        }

        if (errors.Count > 0)
        {
            return new TeknoParrotRegistrationResult(false, dryRun, errors, Array.Empty<TeknoParrotRegistrationItem>(), Array.Empty<string>(), Array.Empty<TeknoParrotRegistrationIssue>(), Array.Empty<string>(), null);
        }

        if (!dryRun)
        {
            Directory.CreateDirectory(userProfilesPath);
        }

        var profileIndex = BuildProfileIndex(gameProfilesPath);
        var profileCodes = Directory.GetFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Where(IsSafeProfileCode)
            .ToArray();
        var registered = new List<TeknoParrotRegistrationItem>();
        var already = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new List<TeknoParrotRegistrationIssue>();
        var matchedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in GetGameFiles(gamesRootPath))
        {
            var relative = Path.GetRelativePath(gamesRootPath, file);
            var folderName = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file);
            var folderKey = StripGameFolderSuffix(folderName);
            allFolders[folderKey] = folderName;
            var fileName = Path.GetFileName(file);
            if (!profileIndex.TryGetValue(fileName, out var templates))
            {
                continue;
            }

            var selected = SelectRegistrationTemplate(folderKey, templates);
            if (selected.Template is null)
            {
                ambiguous.Add(new TeknoParrotRegistrationIssue(file, templates.Select(template => template.Code).ToArray(), selected.BestGuess, selected.Score, "shared-executable"));
                continue;
            }

            var code = selected.Template.Code;
            matchedFolders.Add(folderKey);
            var destination = Path.Combine(userProfilesPath, $"{code}.xml");
            if (File.Exists(destination))
            {
                already.Add(code);
                if (!dryRun)
                {
                    BackfillSecondaryExecutablePath(destination);
                }

                continue;
            }

            if (!IsSafeProfileCode(code))
            {
                ambiguous.Add(new TeknoParrotRegistrationIssue(file, new[] { code }, code, selected.Score, "invalid-profile-code"));
                continue;
            }

            if (!dryRun)
            {
                CopyTemplateWithGamePath(selected.Template.TemplatePath, destination, file);
            }

            registered.Add(new TeknoParrotRegistrationItem(code, file, selected.Score, selected.MatchType));
        }

        // Dat-based disambiguation: for folders whose executable matched no
        // template at all (or only ones that didn't fuzzy-match well enough),
        // the Eggman/RomVault collection dat's own name -> ProfileCode mapping
        // is authoritative. Targets shared-executable platforms where no
        // candidate profile code resembles the folder name (e.g. NESiCAxLive).
        if (datIndex is { Count: > 0 })
        {
            foreach (var folder in allFolders.Where(pair => !matchedFolders.Contains(pair.Key))
                         .Select(pair => pair.Value)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                         .ToArray())
            {
                var folderKey = StripGameFolderSuffix(folder);
                var normFolder = NormalizeGameKey(folderKey);
                var datScore = 1.0;

                if (!datIndex.TryGetValue(normFolder, out var datEntry))
                {
                    var bestScore = 0.0;
                    string? bestKey = null;
                    foreach (var key in datIndex.Keys)
                    {
                        var score = GetDiceSimilarity(normFolder, key);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestKey = key;
                        }
                    }

                    if (bestScore >= FuzzyAutoThreshold && bestKey is not null)
                    {
                        datEntry = datIndex[bestKey];
                        datScore = bestScore;
                    }
                    else
                    {
                        continue;
                    }
                }

                var datCode = datEntry.ProfileCode;
                if (!IsSafeProfileCode(datCode))
                {
                    continue;
                }

                var resolvedCode = ResolveProfileCode(datCode, gameProfilesPath, profileSet);
                var datDestination = Path.Combine(userProfilesPath, $"{resolvedCode}.xml");
                if (File.Exists(datDestination))
                {
                    already.Add(resolvedCode);
                    matchedFolders.Add(folderKey);
                    if (!dryRun)
                    {
                        BackfillSecondaryExecutablePath(datDestination);
                    }

                    continue;
                }

                var datTemplatePath = Path.Combine(gameProfilesPath, $"{resolvedCode}.xml");
                if (!File.Exists(datTemplatePath))
                {
                    continue;
                }

                var datFolderPath = Path.Combine(gamesRootPath, folder);
                var exeToUse = ResolveDatExecutable(datEntry.Executable, datFolderPath)
                    ?? GetGameFiles(datFolderPath).OrderBy(GetGameFilePriority).FirstOrDefault();
                if (exeToUse is null)
                {
                    continue;
                }

                if (!dryRun)
                {
                    CopyTemplateWithGamePath(datTemplatePath, datDestination, exeToUse);
                }

                registered.Add(new TeknoParrotRegistrationItem(resolvedCode, exeToUse, Math.Round(datScore, 2), datScore < 1.0 ? "dat-fuzzy" : "dat"));
                matchedFolders.Add(folderKey);
            }
        }

        var fallbackProfileCodes = profileSet is { Count: > 0 }
            ? profileCodes.Concat(profileSet).Distinct(StringComparer.OrdinalIgnoreCase)
            : profileCodes;

        foreach (var folder in allFolders.Where(pair => !matchedFolders.Contains(pair.Key)).Select(pair => pair.Value).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var selected = SelectProfileCodeByFolderName(folder, fallbackProfileCodes);
            if (selected.Code is null)
            {
                continue;
            }

            var destination = Path.Combine(userProfilesPath, $"{selected.Code}.xml");
            if (File.Exists(destination))
            {
                already.Add(selected.Code);
                continue;
            }

            var templatePath = Path.Combine(gameProfilesPath, $"{selected.Code}.xml");
            if (!File.Exists(templatePath))
            {
                continue;
            }

            var folderPath = Path.Combine(gamesRootPath, folder);
            var file = GetGameFiles(folderPath).OrderBy(GetGameFilePriority).FirstOrDefault();
            if (file is null)
            {
                continue;
            }

            if (!dryRun)
            {
                CopyTemplateWithGamePath(templatePath, destination, file);
            }

            registered.Add(new TeknoParrotRegistrationItem(selected.Code, file, selected.Score, "profile-code-fuzzy"));
            matchedFolders.Add(StripGameFolderSuffix(folder));
        }

        // A folder can contain more than one exe-like file -- e.g. a real
        // launcher (cleanly matched to its own profile by one of the passes
        // above) sitting alongside an unrelated generic/shared stub that
        // only collided with OTHER profiles' ExecutableName and got recorded
        // as ambiguous during the main pass. Drop any ambiguous entry whose
        // folder is in matchedFolders by now -- the folder is accounted for
        // regardless of what that particular exe resolved to.
        var resolvedAmbiguous = ambiguous.Where(issue =>
        {
            var relative = Path.GetRelativePath(gamesRootPath, issue.Exe);
            var folderName = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? relative;
            return !matchedFolders.Contains(StripGameFolderSuffix(folderName));
        }).ToArray();

        var unmatched = allFolders
            .Where(pair => !matchedFolders.Contains(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TeknoParrotRegistrationResult(true, dryRun, Array.Empty<string>(), registered.ToArray(), already.ToArray(), resolvedAmbiguous, unmatched, null);
    }

    internal static TeknoParrotRepairResult RepairGamePaths(TeknoParrotSettings settings, bool dryRun)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolvePath(settings.GameProfilesPath, rootPath, "GameProfiles");
        var gamesRootPath = ResolveGamesRootPath(settings);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            errors.Add("UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(gameProfilesPath) || !Directory.Exists(gameProfilesPath))
        {
            errors.Add("GameProfiles folder was not found. Set gameProfilesPath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(gamesRootPath) || !Directory.Exists(gamesRootPath))
        {
            errors.Add("Games root folder was not found. Set gamesRootPath before repairing profiles.");
        }

        if (errors.Count > 0)
        {
            return new TeknoParrotRepairResult(false, dryRun, errors, Array.Empty<TeknoParrotRepairItem>(), null);
        }

        var profileIndex = BuildProfileIndex(gameProfilesPath);
        var gameFiles = GetGameFiles(gamesRootPath)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key ?? string.Empty, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var reports = new List<TeknoParrotRepairItem>();

        foreach (var profilePath in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(profilePath);
            }
            catch
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "parse-failed", null, null));
                continue;
            }

            var gamePath = FirstElementValue(document, "GamePath");
            if (!string.IsNullOrWhiteSpace(gamePath) && File.Exists(gamePath))
            {
                continue;
            }

            var executableName = FirstElementValue(document, "ExecutableName");
            if (string.IsNullOrWhiteSpace(executableName))
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "no-executable-name", null, null));
                continue;
            }

            var alternatives = GetExecutableAlternatives(executableName).ToArray();

            // If any alternative name is shared by more than one profile in the
            // library, the executable is inherently ambiguous -- never
            // auto-assign it regardless of what's on disk.
            var ambiguousProfile = alternatives.Any(alternative =>
                profileIndex.TryGetValue(alternative, out var templates) && templates.Count > 1);
            if (ambiguousProfile)
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "ambiguous", executableName, null));
                continue;
            }

            // Try each alternative name against what's on disk, in order, and
            // stop at the first one with any match -- mirrors the original
            // tool exactly. Do NOT pool matches across every alternative: a
            // profile listing "a.exe;b.exe" only ever has one of those two
            // present on a given install, so accumulating both would falsely
            // flag a clean single-file match as ambiguous if an unrelated
            // file elsewhere in the games root happens to share the other
            // alternative's name.
            string[]? candidates = null;
            foreach (var alternative in alternatives)
            {
                if (gameFiles.TryGetValue(alternative, out var files))
                {
                    candidates = files;
                    break;
                }
            }

            if (candidates is null)
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "not-found", executableName, null));
                continue;
            }

            if (candidates.Length > 1)
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "ambiguous", executableName, null));
                continue;
            }

            var newPath = candidates[0];
            if (!dryRun)
            {
                SetElementValue(document, "GamePath", newPath);
                SetSecondaryExecutablePath(document, newPath);
                SaveProfileDocument(document, profilePath);
            }

            reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "fixed", executableName, newPath));
        }

        return new TeknoParrotRepairResult(true, dryRun, Array.Empty<string>(), reports.ToArray(), null);
    }

    private static string ResolveRootPath(TeknoParrotSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TeknoParrotRootPath))
        {
            return Path.GetFullPath(settings.TeknoParrotRootPath);
        }

        if (!string.IsNullOrWhiteSpace(settings.ExecutablePath))
        {
            var executableDirectory = Path.GetDirectoryName(settings.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(executableDirectory))
            {
                return Path.GetFullPath(executableDirectory);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.UserProfilesPath))
        {
            var userProfilesDirectory = Path.GetDirectoryName(settings.UserProfilesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(userProfilesDirectory))
            {
                return Path.GetFullPath(userProfilesDirectory);
            }
        }

        return string.Empty;
    }

    private static string ResolveGamesRootPath(TeknoParrotSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GamesRootPath))
        {
            return Path.GetFullPath(settings.GamesRootPath);
        }

        var rootPath = ResolveRootPath(settings);
        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(rootPath, "Games"));
    }

    private static string ResolvePath(string explicitPath, string rootPath, string childName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(rootPath, childName));
    }

    private static string ResolveIconPath(string iconName, string rootPath, string iconsPath)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return string.Empty;
        }

        if (Path.IsPathFullyQualified(iconName))
        {
            return iconName;
        }

        var rootCandidate = !string.IsNullOrWhiteSpace(rootPath) ? Path.GetFullPath(Path.Combine(rootPath, iconName)) : string.Empty;
        if (!string.IsNullOrWhiteSpace(rootCandidate) && File.Exists(rootCandidate))
        {
            return rootCandidate;
        }

        return !string.IsNullOrWhiteSpace(iconsPath)
            ? Path.GetFullPath(Path.Combine(iconsPath, Path.GetFileName(iconName)))
            : iconName;
    }

    private static Dictionary<string, List<TeknoParrotProfileTemplate>> BuildProfileIndex(string gameProfilesPath)
    {
        var index = new Dictionary<string, List<TeknoParrotProfileTemplate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var templatePath in Directory.GetFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var executableName = GetPrimaryExecutableName(templatePath);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                continue;
            }

            var code = Path.GetFileNameWithoutExtension(templatePath);
            foreach (var alternative in GetExecutableAlternatives(executableName))
            {
                if (!index.TryGetValue(alternative, out var templates))
                {
                    templates = new List<TeknoParrotProfileTemplate>();
                    index[alternative] = templates;
                }

                templates.Add(new TeknoParrotProfileTemplate(code, templatePath, executableName));
            }
        }

        return index;
    }

    private static string GetPrimaryExecutableName(string templatePath)
    {
        try
        {
            var document = XDocument.Load(templatePath);
            return FirstElementValue(document, "ExecutableName");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> GetExecutableAlternatives(string executableName)
    {
        return executableName
            .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static (TeknoParrotProfileTemplate? Template, string? BestGuess, double Score, string MatchType) SelectRegistrationTemplate(
        string folderName,
        List<TeknoParrotProfileTemplate> templates)
    {
        if (templates.Count == 1)
        {
            return (templates[0], templates[0].Code, 1.0, "executable");
        }

        var selected = SelectProfileCodeByFolderName(folderName, templates.Select(template => template.Code));
        if (selected.Code is null || selected.Score < FuzzyAutoThreshold)
        {
            return (null, selected.Code, selected.Score, "shared-executable");
        }

        return (templates.First(template => string.Equals(template.Code, selected.Code, StringComparison.OrdinalIgnoreCase)), selected.Code, selected.Score, "fuzzy");
    }

    internal static (string? Code, double Score) SelectProfileCodeByFolderName(string folderName, IEnumerable<string> profileCodes)
    {
        var normalizedFolder = NormalizeGameKey(StripGameFolderSuffix(folderName));
        if (normalizedFolder.Length < 2)
        {
            return (null, 0);
        }

        string? bestCode = null;
        var bestScore = 0.0;
        var secondScore = 0.0;
        foreach (var code in profileCodes)
        {
            var score = GetDiceSimilarity(normalizedFolder, NormalizeGameKey(code));
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestCode = code;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        // A near-tie with the runner-up means no real signal preferred this
        // candidate over the other -- treat it the same as "below threshold"
        // rather than guessing. See FuzzyTieMargin.
        var isTooCloseToCall = secondScore > 0 && (bestScore - secondScore) < FuzzyTieMargin;
        return bestScore >= FuzzyAutoThreshold && !isTooCloseToCall ? (bestCode, bestScore) : (null, bestScore);
    }

    private static IEnumerable<string> GetGameFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        var baseDepth = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Length;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                if (GameFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return false;
                }

                var depth = Path.GetFullPath(path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Length - baseDepth;
                return depth <= 6;
            });
    }

    private static int GetGameFilePriority(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".exe" => 0,
            ".elf" => 1,
            "" => 2,
            ".xbe" => 3,
            ".dll" => 4,
            _ => 5
        };
    }

    private static void CopyTemplateWithGamePath(string templatePath, string destinationPath, string gamePath)
    {
        var document = XDocument.Load(templatePath);
        SetElementValue(document, "GamePath", gamePath);
        SetSecondaryExecutablePath(document, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        SaveProfileDocument(document, destinationPath);
    }

    private static void BackfillSecondaryExecutablePath(string userProfilePath)
    {
        try
        {
            var document = XDocument.Load(userProfilePath);
            var gamePath = FirstElementValue(document, "GamePath");
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                return;
            }

            if (SetSecondaryExecutablePath(document, gamePath))
            {
                SaveProfileDocument(document, userProfilePath);
            }
        }
        catch
        {
            // Existing malformed profiles are reported by Scan; registration should keep moving.
        }
    }

    private static bool SetSecondaryExecutablePath(XDocument document, string primaryGamePath)
    {
        var hasTwoExecutables = string.Equals(FirstElementValue(document, "HasTwoExecutables"), "true", StringComparison.OrdinalIgnoreCase);
        var executableName2 = FirstElementValue(document, "ExecutableName2");
        if (!hasTwoExecutables || string.IsNullOrWhiteSpace(executableName2) || string.IsNullOrWhiteSpace(primaryGamePath))
        {
            return false;
        }

        var secondaryPath = Path.Combine(Path.GetDirectoryName(primaryGamePath) ?? string.Empty, executableName2.Trim());
        if (!File.Exists(secondaryPath))
        {
            return false;
        }

        if (string.Equals(FirstElementValue(document, "GamePath2"), secondaryPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SetElementValue(document, "GamePath2", secondaryPath);
        return true;
    }

    private static void SetElementValue(XDocument document, string localName, string value)
    {
        var root = document.Root ?? throw new InvalidOperationException("Profile XML has no root element.");
        var existing = root.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new XElement(root.GetDefaultNamespace() + localName);
            root.AddFirst(existing);
        }

        existing.Value = value;
    }

    private static void SaveProfileDocument(XDocument document, string profilePath)
    {
        var tempPath = profilePath + ".tmp";
        document.Save(tempPath);
        if (!File.Exists(profilePath))
        {
            File.Move(tempPath, profilePath);
            return;
        }

        try
        {
            File.Replace(tempPath, profilePath, null);
        }
        catch
        {
            File.Delete(profilePath);
            File.Move(tempPath, profilePath);
        }
    }

    private static string StripGameFolderSuffix(string folderName)
    {
        return Regex.Replace(folderName, @"\.(teknoparrot|parrot|game)$", string.Empty, RegexOptions.IgnoreCase);
    }

    private static bool IsSafeProfileCode(string code)
    {
        return Regex.IsMatch(code, @"^[\w]+$");
    }

    internal static string NormalizeGameKey(string value)
    {
        var normalized = Regex.Replace(value, "(?<=[a-z])(?=[A-Z])", " ");
        normalized = Regex.Replace(normalized, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
        normalized = Regex.Replace(normalized, @"\(\d{4}-\d{2}-\d{2}\)", string.Empty);
        normalized = Regex.Replace(normalized, @"\(\d{4}\)", string.Empty);
        normalized = Regex.Replace(normalized, @"\[[^\]]*\]", string.Empty);
        normalized = Regex.Replace(normalized, @"\(\d+\.\d[\d\.]*\)", string.Empty);
        normalized = Regex.Replace(normalized, @"\((JPN|USA|EUR|EXP|JP|US|KOR|AUS|ASI|INTL|ARC|UNK)\)", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\((ver\.?|rev\.?|v)\s*[\d\.]+[a-z]?\)", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\(\d+\)", string.Empty);
        normalized = Regex.Replace(normalized, @"[^\p{L}0-9]", string.Empty);
        return normalized.ToLowerInvariant();
    }

    private static double GetDiceSimilarity(string first, string second)
    {
        if (first.Length < 2 || second.Length < 2)
        {
            return 0;
        }

        var firstBigrams = BuildBigrams(first);
        var secondBigrams = BuildBigrams(second);
        var intersection = 0;
        foreach (var pair in firstBigrams)
        {
            if (secondBigrams.TryGetValue(pair.Key, out var count))
            {
                intersection += Math.Min(pair.Value, count);
            }
        }

        var total = firstBigrams.Values.Sum() + secondBigrams.Values.Sum();
        return total == 0 ? 0 : (2.0 * intersection) / total;
    }

    private static Dictionary<string, int> BuildBigrams(string value)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < value.Length - 1; index++)
        {
            var bigram = value.Substring(index, 2);
            result[bigram] = result.GetValueOrDefault(bigram) + 1;
        }

        return result;
    }

    private static string FirstElementValue(XDocument document, string localName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim() ?? string.Empty;
    }

    private static string HumanizeProfileName(string profileName)
    {
        var chars = new List<char>();
        for (var index = 0; index < profileName.Length; index++)
        {
            var current = profileName[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(profileName[index - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(current);
        }

        return new string(chars.ToArray()).Replace('_', ' ').Replace('-', ' ').Trim();
    }

    // ---------------------------------------------------------------------
    // Dat-index disambiguation and GitHub profile-set fuzzy fallback.
    //
    // RegisterGames above only resolves a folder when its executable name
    // maps to exactly one GameProfiles template, or fuzzy-matches a shared
    // template's own code closely enough. Some platforms have neither: the
    // executable is shared by dozens of unrelated titles AND none of the
    // candidate profile codes resemble the folder name (e.g. NESiCAxLive's
    // game.exe). The Eggman/RomVault collection dat ships an authoritative
    // game-name -> ProfileCode mapping for exactly this case. The user can
    // point EggmanDatPath at a copy they already have, or use the
    // "Download Collection Dat" action below to fetch the latest one from
    // Eggmansworld/TeknoParrot's GitHub releases -- ported from the
    // original PowerShell tool's Get-EggmanDatRelease/Invoke-EggmanDatDownload.
    // The dat is data, never executed, but it's still untrusted external
    // content: the release filename is sanitized via ResolveEggmanDatSavePath
    // before it's ever joined into a save path, and the download URL is
    // checked against SafeGitHubDownloadHost before being fetched.
    // ---------------------------------------------------------------------

    private const string TeknoParrotUiRepo = "teknogods/TeknoParrotUI";
    private const string EggmanDatRepo = "Eggmansworld/TeknoParrot";
    private const string GameProfilesTreePrefix = "TeknoParrotUi.Common/GameProfiles/";
    private static readonly Regex SafeStemPattern = new(@"^[\w]+$", RegexOptions.Compiled);

    // Mirrors the PowerShell tool's -like 'TeknoParrot*Collection*RomVault*.zip'.
    private static readonly Regex EggmanDatAssetPattern = new(
        @"^TeknoParrot.*Collection.*RomVault.*\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Mirrors the PowerShell tool's download-URL safety check: the asset's
    // browser_download_url must actually point at GitHub, guarding against
    // a compromised/crafted API response redirecting the download elsewhere.
    private static readonly Regex SafeGitHubDownloadHost = new(
        @"^https://[a-zA-Z0-9._-]*(github\.com|githubusercontent\.com)/", RegexOptions.Compiled);

    // Reads a No-Intro/Eggman style Logiqx dat (<game name="..."><GameProfile>
    // ...<Executable>...) without loading the whole document -- the
    // collection dat can run into the hundreds of MB. <rom> nodes (hash
    // tables, hundreds per game) are skipped for performance. Accepts
    // either a raw .dat XML file or a .zip containing one.
    public static Dictionary<string, TeknoParrotDatEntry> BuildDatIndex(string datPath, Action<string>? log = null)
    {
        try
        {
            if (string.Equals(Path.GetExtension(datPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(datPath);
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    log?.Invoke($"DatIndex: no .dat entry found inside '{datPath}'.");
                    return new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase);
                }

                using var entryStream = entry.Open();
                return BuildDatIndexFromStream(entryStream, log);
            }

            using var fileStream = File.OpenRead(datPath);
            return BuildDatIndexFromStream(fileStream, log);
        }
        catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException or UnauthorizedAccessException)
        {
            log?.Invoke($"DatIndex: could not read '{datPath}' -- {ex.Message}");
            return new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, TeknoParrotDatEntry> BuildDatIndexFromStream(Stream stream, Action<string>? log)
    {
        var index = new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase);
        var readerSettings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
        };

        using var reader = XmlReader.Create(stream, readerSettings);

        var gameName = "";
        var profileCode = "";
        var exePath = "";
        var insideGame = false;
        var currentElement = "";

        // Skip() already advances onto the next sibling (the following
        // <rom>, or the closing </game> if that was the last one) -- it
        // must NOT be followed by an unconditional Read() next iteration,
        // or every Skip() silently discards one real node. With hundreds of
        // <rom> entries per game, that drops roughly half of all <game>
        // entries once the cumulative drift lands exactly on a game's own
        // </game> node.
        var advance = true;
        while (true)
        {
            if (advance && !reader.Read())
            {
                break;
            }
            advance = true;

            if (reader.NodeType == XmlNodeType.Element)
            {
                currentElement = reader.Name;
                if (currentElement == "game")
                {
                    gameName = reader.GetAttribute("name") ?? "";
                    profileCode = "";
                    exePath = "";
                    insideGame = true;
                }
                else if (currentElement == "rom" && insideGame)
                {
                    reader.Skip();
                    currentElement = "";
                    advance = false;
                }
            }
            else if (reader.NodeType == XmlNodeType.Text && insideGame)
            {
                if (currentElement == "GameProfile")
                {
                    profileCode = reader.Value;
                }
                else if (currentElement == "Executable" && exePath.Length == 0)
                {
                    exePath = reader.Value;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "game")
            {
                insideGame = false;
                if (profileCode.Length > 0 && gameName.Length > 0)
                {
                    var normName = NormalizeGameKey(gameName);
                    if (normName.Length > 0 && !index.ContainsKey(normName))
                    {
                        index[normName] = new TeknoParrotDatEntry(profileCode.Trim(), exePath.Trim());
                    }
                }
            }
        }

        return index;
    }

    // Picks the collection dat ZIP asset out of a GitHub releases-API
    // response for Eggmansworld/TeknoParrot, mirroring the PowerShell
    // tool's Get-EggmanDatRelease. Returns null if no asset matches the
    // naming convention, or if its download URL doesn't look like a real
    // GitHub asset URL (defense against a crafted/compromised response --
    // same convention as the original script's regex check).
    internal static EggmanDatRelease? SelectEggmanDatAsset(JsonElement releaseJson)
    {
        if (!releaseJson.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !EggmanDatAssetPattern.IsMatch(name))
            {
                continue;
            }

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(downloadUrl) || !SafeGitHubDownloadHost.IsMatch(downloadUrl))
            {
                return null;
            }

            var sizeBytes = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : 0L;
            return new EggmanDatRelease(downloadUrl, name, sizeBytes);
        }

        return null;
    }

    // Queries GitHub for the latest Eggmansworld/TeknoParrot release and
    // returns its collection dat ZIP asset, or null if unavailable. Same
    // retry/timeout/User-Agent shape as FetchProfileCodeSetAsync below.
    public static async Task<EggmanDatRelease?> GetEggmanDatReleaseAsync(
        HttpClient http, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{EggmanDatRepo}/releases/latest");
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: attemptCts.Token).ConfigureAwait(false);
                var release = SelectEggmanDatAsset(body);
                if (release is null)
                {
                    log?.Invoke("EggmanDat: no matching collection dat asset found in the latest release.");
                }
                return release;
            }
            catch (HttpRequestException ex)
            {
                var status = (int?)ex.StatusCode ?? 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"EggmanDat: GitHub release query failed -- {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException or JsonException)
            {
                if (attempt >= 3)
                {
                    log?.Invoke($"EggmanDat: GitHub release query failed -- {ex.Message}");
                    return null;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // Resolves a safe destination path for a downloaded Eggman dat release
    // inside destinationDir: strips any path components from the
    // GitHub-supplied filename via Path.GetFileName, then confirms the
    // result still resolves inside destinationDir before it's ever used in
    // a file write. Returns null if the name is empty or would escape
    // destinationDir.
    internal static string? ResolveEggmanDatSavePath(string destinationDir, string releaseFileName)
    {
        var safeName = Path.GetFileName(releaseFileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return null;
        }

        var candidate = Path.Combine(destinationDir, safeName);
        return IsPathInside(candidate, destinationDir) ? candidate : null;
    }

    // Downloads an Eggman dat release ZIP into destinationDir. Streams to a
    // ".tmp" file first and only moves it to the final name on a fully
    // successful download, so an interrupted download never leaves a
    // half-written file at the name BuildDatIndex would otherwise read from
    // on the next run. Mirrors the PowerShell tool's
    // Invoke-EggmanDatDownload (retry-with-backoff, delete-partial-on-failure).
    public static async Task<string?> DownloadEggmanDatAsync(
        HttpClient http, EggmanDatRelease release, string destinationDir, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);

        var savePath = ResolveEggmanDatSavePath(destinationDir, release.FileName);
        if (savePath is null)
        {
            log?.Invoke($"EggmanDat: SECURITY -- unsafe release filename '{release.FileName}', skipped.");
            return null;
        }

        Directory.CreateDirectory(destinationDir);
        var tempPath = savePath + ".tmp";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                }
                File.Move(tempPath, savePath);
                log?.Invoke($"EggmanDat: downloaded '{release.FileName}' ({Math.Round(release.SizeBytes / 1024d / 1024d, 1)} MB) to '{savePath}'.");
                return savePath;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException cleanupEx)
                {
                    log?.Invoke($"EggmanDat: could not remove partial download '{tempPath}' -- {cleanupEx.Message}");
                }

                var status = ex is HttpRequestException httpEx ? (int?)httpEx.StatusCode ?? 0 : 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"EggmanDat: download failed -- {ex.Message}");
                    return null;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    // Fetches the live set of profile codes teknogods/TeknoParrotUI ships,
    // falling back to the local GameProfiles folder listing if GitHub can't
    // be reached. Read-only metadata call (a directory listing, not a
    // binary download); fails soft on any error so a flaky connection never
    // blocks registration.
    public static async Task<HashSet<string>> FetchProfileCodeSetAsync(
        HttpClient http, string gameProfilesPath, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = false;
        var branch = "master";

        try
        {
            using var branchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            branchCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var repoRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{TeknoParrotUiRepo}");
            repoRequest.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
            using var repoResponse = await http.SendAsync(repoRequest, branchCts.Token).ConfigureAwait(false);
            repoResponse.EnsureSuccessStatusCode();
            var repoInfo = await repoResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: branchCts.Token).ConfigureAwait(false);
            if (repoInfo.TryGetProperty("default_branch", out var branchProp) && branchProp.GetString() is { Length: > 0 } resolvedBranch)
            {
                branch = resolvedBranch;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"ProfileSet (GitHub): could not resolve default branch, falling back to 'master' -- {ex.Message}");
        }

        var apiUri = $"https://api.github.com/repos/{TeknoParrotUiRepo}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: attemptCts.Token).ConfigureAwait(false);

                if (body.TryGetProperty("tree", out var tree))
                {
                    foreach (var node in tree.EnumerateArray())
                    {
                        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
                        var path = node.TryGetProperty("path", out var p) ? p.GetString() : null;
                        if (type == "blob" && path is not null &&
                            path.StartsWith(GameProfilesTreePrefix, StringComparison.Ordinal) &&
                            path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            var stem = Path.GetFileNameWithoutExtension(path[GameProfilesTreePrefix.Length..]);
                            if (SafeStemPattern.IsMatch(stem))
                            {
                                result.Add(stem);
                            }
                        }
                    }
                }

                if (result.Count > 0)
                {
                    log?.Invoke($"ProfileSet (GitHub): {result.Count} profiles from {TeknoParrotUiRepo}.");
                    loaded = true;
                }
                break;
            }
            catch (HttpRequestException ex)
            {
                var status = (int?)ex.StatusCode ?? 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"ProfileSet (GitHub): query failed -- {ex.Message}");
                    break;
                }
                log?.Invoke($"ProfileSet (GitHub): attempt {attempt} failed, retrying in 5s -- {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"ProfileSet (GitHub): query failed -- {ex.Message}");
                break;
            }
        }

        if (!loaded && Directory.Exists(gameProfilesPath))
        {
            foreach (var file in Directory.EnumerateFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (SafeStemPattern.IsMatch(stem))
                {
                    result.Add(stem);
                }
            }
            log?.Invoke($"ProfileSet (local fallback): {result.Count} profiles from {gameProfilesPath}");
        }

        return result;
    }

    // Resolves a dat ProfileCode to the correct template filename stem.
    // Priority: (1) exact local template; (2) code in the fetched profile
    // set; (3) fuzzy match against the profile set above the auto
    // threshold; (4) the original code unchanged -- callers must still
    // verify a local template exists before registering against it.
    public static string ResolveProfileCode(string code, string gameProfilesPath, HashSet<string>? profileSet, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }

        if (File.Exists(Path.Combine(gameProfilesPath, code + ".xml")))
        {
            return code;
        }

        if (profileSet is null || profileSet.Count == 0 || profileSet.Contains(code))
        {
            return code;
        }

        var normCode = NormalizeGameKey(code);
        var bestScore = 0.0;
        string? bestMatch = null;
        foreach (var candidate in profileSet)
        {
            var score = GetDiceSimilarity(normCode, NormalizeGameKey(candidate));
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;
            }
        }

        if (bestScore >= FuzzyAutoThreshold && bestMatch is not null && SafeStemPattern.IsMatch(bestMatch))
        {
            log?.Invoke($"ResolveProfileCode: '{code}' -> '{bestMatch}' (score {Math.Round(bestScore, 2)})");
            return bestMatch;
        }

        return code;
    }

    // Resolves the dat's <Executable> hint (a path relative to the game
    // folder) to an actual file, trying the literal path, then +.exe,
    // then +.elf. Returns null if the hint is empty, escapes the game
    // folder, or doesn't exist under any of those names.
    private static string? ResolveDatExecutable(string? relativeExecutable, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(relativeExecutable))
        {
            return null;
        }

        var relative = relativeExecutable.TrimStart('\\', '/');
        if (relative.Length == 0)
        {
            return null;
        }

        var candidatePath = Path.Combine(folderPath, relative);
        if (!IsPathInside(candidatePath, folderPath))
        {
            return null;
        }

        if (File.Exists(candidatePath))
        {
            return candidatePath;
        }
        if (File.Exists(candidatePath + ".exe"))
        {
            return candidatePath + ".exe";
        }
        if (File.Exists(candidatePath + ".elf"))
        {
            return candidatePath + ".elf";
        }
        return null;
    }

    // True if child is the same folder as, or inside, parent -- guards a
    // dat-supplied relative executable path against escaping the game
    // folder via ".." or an absolute path.
    private static bool IsPathInside(string child, string parent)
    {
        string c, p;
        try
        {
            c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TeknoParrotDatEntry(string ProfileCode, string Executable);

public sealed record EggmanDatRelease(string DownloadUrl, string FileName, long SizeBytes);
