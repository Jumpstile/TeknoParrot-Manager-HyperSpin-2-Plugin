using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HyperAI.Plugin.SocketIO;

namespace HyperAI.Plugin.Common
{
    public sealed class HyperImportSyncPayload<TGame>
    {
        [JsonPropertyName("games")]
        public List<TGame> Games { get; set; } = new();

        [JsonPropertyName("database_operations")]
        public HyperImportDatabaseOperations DatabaseOperations { get; set; } = new();

        [JsonPropertyName("sync_metadata")]
        public HyperImportSyncMetadata SyncMetadata { get; set; } = new();
    }

    public sealed class HyperImportDatabaseOperations
    {
        [JsonPropertyName("create_system")]
        public HyperImportSystem? CreateSystem { get; set; }

        [JsonPropertyName("create_emulator")]
        public HyperImportEmulator? CreateEmulator { get; set; }

        [JsonPropertyName("add_games")]
        public HyperImportGamesBatch? AddGames { get; set; }

        [JsonPropertyName("remove_games")]
        public HyperImportRemoveGamesBatch? RemoveGames { get; set; }

        [JsonPropertyName("add_media")]
        public HyperImportMediaBatch? AddMedia { get; set; }

        [JsonPropertyName("remove_media")]
        public HyperImportMediaBatch? RemoveMedia { get; set; }

        [JsonPropertyName("remove_emulator")]
        public HyperImportReference? RemoveEmulator { get; set; }

        [JsonPropertyName("remove_system")]
        public HyperImportReference? RemoveSystem { get; set; }
    }

    public sealed class HyperImportSystem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("referenceId")]
        public string? ReferenceId { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("romsPaths")]
        public string? RomsPaths { get; set; }

        [JsonPropertyName("allowedExtensions")]
        public string? AllowedExtensions { get; set; }

        [JsonPropertyName("searchSubfolders")]
        public bool? SearchSubfolders { get; set; }

        [JsonPropertyName("gamesCount")]
        public int GamesCount { get; set; }

        [JsonPropertyName("mediaOptions")]
        public object? MediaOptions { get; set; }

        [JsonPropertyName("mediaFolders")]
        public object? MediaFolders { get; set; }

        [JsonPropertyName("metadata")]
        public object? Metadata { get; set; }
    }

    public sealed class HyperImportEmulator
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("commandLine")]
        public string? CommandLine { get; set; }

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("executable")]
        public string Executable { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("systemName")]
        public string? SystemName { get; set; }

        [JsonPropertyName("supportedExtensions")]
        public string[] SupportedExtensions { get; set; } = Array.Empty<string>();

        [JsonPropertyName("launchMethod")]
        public string? LaunchMethod { get; set; }

        [JsonPropertyName("link_emulator")]
        public object? LinkEmulator { get; set; }

        [JsonPropertyName("metadata")]
        public object? Metadata { get; set; }
    }

    public sealed class HyperImportGamesBatch
    {
        [JsonPropertyName("systemName")]
        public string? SystemName { get; set; }

        [JsonPropertyName("systemId")]
        public string? SystemId { get; set; }

        [JsonPropertyName("games")]
        public List<HyperImportGame> Games { get; set; } = new();
    }

    public sealed class HyperImportRemoveGamesBatch
    {
        [JsonPropertyName("game_ids")]
        public List<string> GameIds { get; set; } = new();

        [JsonPropertyName("reference_ids")]
        public List<string> ReferenceIds { get; set; } = new();

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }
    }

    public sealed class HyperImportGame
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("romPath")]
        public string? RomPath { get; set; }

        [JsonPropertyName("gameReferenceId")]
        public string? GameReferenceId { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("developer")]
        public string? Developer { get; set; }

        [JsonPropertyName("publisher")]
        public string? Publisher { get; set; }

        [JsonPropertyName("releaseYear")]
        public string? ReleaseYear { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("genres")]
        public string? Genres { get; set; }

        [JsonPropertyName("isInstalled")]
        public bool? IsInstalled { get; set; }

        [JsonPropertyName("installPath")]
        public string? InstallPath { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("launchCommandType")]
        public int? LaunchCommandType { get; set; }

        [JsonPropertyName("launchCommandFilePath")]
        public string? LaunchCommandFilePath { get; set; }

        [JsonPropertyName("launchCommandExtraParams")]
        public string? LaunchCommandExtraParams { get; set; }

        [JsonPropertyName("titleId")]
        public string? TitleId { get; set; }

        [JsonPropertyName("metadata")]
        public object? Metadata { get; set; }
    }

    public sealed class HyperImportMediaBatch
    {
        [JsonPropertyName("media")]
        public List<HyperImportMediaAsset> Media { get; set; } = new();
    }

    public sealed class HyperImportMediaAsset
    {
        [JsonPropertyName("gameId")]
        public string? GameId { get; set; }

        [JsonPropertyName("gameReferenceId")]
        public string? GameReferenceId { get; set; }

        [JsonPropertyName("systemReferenceId")]
        public string? SystemReferenceId { get; set; }

        [JsonPropertyName("mediaType")]
        public string MediaType { get; set; } = string.Empty;

        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("metadata")]
        public object? Metadata { get; set; }
    }

    public sealed class HyperImportReference
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("referenceId")]
        public string? ReferenceId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    public sealed class HyperImportSyncMetadata
    {
        [JsonPropertyName("plugin_id")]
        public string PluginId { get; set; } = string.Empty;

        [JsonPropertyName("sync_time")]
        public DateTime SyncTime { get; set; }

        [JsonPropertyName("games_count")]
        public int GamesCount { get; set; }
    }

    public static class HyperImportPayloadSerializer
    {
        public static JsonElement ToElement<T>(T value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        public static IEnumerable<HyperImportGamesBatch> BatchGames(IEnumerable<HyperImportGame> games, int batchSize)
        {
            return games
                .Chunk(batchSize)
                .Select(batch => new HyperImportGamesBatch { Games = batch.ToList() });
        }

        public static IEnumerable<HyperImportRemoveGamesBatch> BatchRemovedGames(HyperImportRemoveGamesBatch removedGames, int batchSize)
        {
            var gameIdBatches = removedGames.GameIds.Chunk(batchSize).Select(batch => batch.ToList()).ToList();
            var referenceIdBatches = removedGames.ReferenceIds.Chunk(batchSize).Select(batch => batch.ToList()).ToList();
            var maxBatchCount = Math.Max(gameIdBatches.Count, referenceIdBatches.Count);

            for (var index = 0; index < maxBatchCount; index++)
            {
                yield return new HyperImportRemoveGamesBatch
                {
                    GameIds = index < gameIdBatches.Count ? gameIdBatches[index] : new List<string>(),
                    ReferenceIds = index < referenceIdBatches.Count ? referenceIdBatches[index] : new List<string>(),
                    Source = removedGames.Source,
                    Platform = removedGames.Platform
                };
            }
        }

        public static IEnumerable<HyperImportMediaBatch> BatchMedia(IEnumerable<HyperImportMediaAsset> media, int batchSize)
        {
            return media
                .Chunk(batchSize)
                .Select(batch => new HyperImportMediaBatch { Media = batch.ToList() });
        }
    }

    public sealed class HyperImportSyncResult
    {
        public bool Success { get; set; }
        public List<string> CompletedOperations { get; } = new();
        public List<string> FailedOperations { get; } = new();
    }

    public sealed class HyperImportSyncOptions
    {
        public int GameBatchSize { get; set; } = 50;
        public int MediaBatchSize { get; set; } = 50;
        public bool TreatCreateFailuresAsNonFatal { get; set; } = true;
    }

    public static class HyperImportSyncClient
    {
        public static async Task<HyperImportSyncResult> SendAsync<TGame>(
            PluginSocketIOClient? socketClient,
            bool useSocketIO,
            string eventType,
            HyperImportSyncPayload<TGame> payload,
            Func<string, Task> logAsync,
            Func<string, Task> warnAsync,
            Func<string, Task> errorAsync,
            HyperImportSyncOptions? options = null)
        {
            var result = new HyperImportSyncResult();
            options ??= new HyperImportSyncOptions();

            if (!useSocketIO || socketClient?.IsAuthenticated != true)
            {
                await logAsync($"Data ready for HyperHQ: {eventType} (Socket.IO not available, skipping push)");
                return result;
            }

            await logAsync($"Sending {eventType} data to HyperHQ via standardized import API requests...");

            await RunMediaOperation("removeMedia", payload.DatabaseOperations.RemoveMedia);
            await RunRemoveGames(payload.DatabaseOperations.RemoveGames);
            await RunOperation("removeEmulator", payload.DatabaseOperations.RemoveEmulator, fatal: true);
            await RunOperation("removeSystem", payload.DatabaseOperations.RemoveSystem, fatal: true);
            await RunOperation("createSystem", payload.DatabaseOperations.CreateSystem, fatal: !options.TreatCreateFailuresAsNonFatal);
            await RunOperation("createEmulator", payload.DatabaseOperations.CreateEmulator, fatal: !options.TreatCreateFailuresAsNonFatal);
            await RunAddGames(payload.DatabaseOperations.AddGames);
            await RunMediaOperation("addMedia", payload.DatabaseOperations.AddMedia);

            result.Success = result.FailedOperations.Count == 0 ||
                (options.TreatCreateFailuresAsNonFatal &&
                 result.FailedOperations.All(operation => operation == "createSystem" || operation == "createEmulator"));

            if (result.Success)
            {
                await logAsync($"Successfully sent {eventType} data to HyperHQ");
            }
            else
            {
                await errorAsync($"Failed to complete {eventType}. Failed operations: {string.Join(", ", result.FailedOperations)}");
            }

            return result;

            async Task RunOperation<T>(string method, T? data, bool fatal)
                where T : class
            {
                if (data == null)
                {
                    return;
                }

                try
                {
                    await logAsync($"HyperHQ import operation: {method}");
                    var response = await socketClient.RequestDataAsync<JsonElement>(method, data);
                    await logAsync($"{method} response: {JsonSerializer.Serialize(response)}");
                    result.CompletedOperations.Add(method);
                }
                catch (Exception ex)
                {
                    result.FailedOperations.Add(method);
                    var message = $"{method} failed: {ex.Message}";
                    if (fatal)
                    {
                        await errorAsync(message);
                    }
                    else
                    {
                        await warnAsync($"{message} (continuing)");
                    }
                }
            }

            async Task RunAddGames(HyperImportGamesBatch? addGames)
            {
                if (addGames == null)
                {
                    return;
                }

                var batches = HyperImportPayloadSerializer.BatchGames(addGames.Games, options.GameBatchSize).ToList();
                await logAsync($"HyperHQ import operation: addGames ({addGames.Games.Count} games, {batches.Count} batch(es))");

                foreach (var batch in batches)
                {
                    batch.SystemName = addGames.SystemName;
                    batch.SystemId = addGames.SystemId;

                    try
                    {
                        await socketClient.RequestDataAsync<JsonElement>("addGames", batch);
                        result.CompletedOperations.Add("addGames");
                    }
                    catch (Exception ex)
                    {
                        result.FailedOperations.Add("addGames");
                        await errorAsync($"addGames failed: {ex.Message}");
                        break;
                    }
                }
            }

            async Task RunRemoveGames(HyperImportRemoveGamesBatch? removeGames)
            {
                if (removeGames == null)
                {
                    return;
                }

                var batches = HyperImportPayloadSerializer.BatchRemovedGames(removeGames, options.GameBatchSize).ToList();
                await logAsync($"HyperHQ import operation: removeGames ({removeGames.GameIds.Count + removeGames.ReferenceIds.Count} games, {batches.Count} batch(es))");

                foreach (var batch in batches)
                {
                    try
                    {
                        await socketClient.RequestDataAsync<JsonElement>("removeGames", batch);
                        result.CompletedOperations.Add("removeGames");
                    }
                    catch (Exception ex)
                    {
                        result.FailedOperations.Add("removeGames");
                        await errorAsync($"removeGames failed: {ex.Message}");
                        break;
                    }
                }
            }

            async Task RunMediaOperation(string method, HyperImportMediaBatch? media)
            {
                if (media == null)
                {
                    return;
                }

                var batches = HyperImportPayloadSerializer.BatchMedia(media.Media, options.MediaBatchSize).ToList();
                await logAsync($"HyperHQ import operation: {method} ({media.Media.Count} assets, {batches.Count} batch(es))");

                foreach (var batch in batches)
                {
                    try
                    {
                        await socketClient.RequestDataAsync<JsonElement>(method, batch);
                        result.CompletedOperations.Add(method);
                    }
                    catch (Exception ex)
                    {
                        result.FailedOperations.Add(method);
                        await errorAsync($"{method} failed: {ex.Message}");
                        break;
                    }
                }
            }
        }
    }
}
