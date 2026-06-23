using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HyperAI.Plugin.SocketIO
{
    public class PluginSocketIOClient : IDisposable
    {
        private ClientWebSocket webSocket;
        private readonly string pluginId;
        private readonly string authToken;
        private readonly int serverPort;
        private bool isConnected = false;
        private bool isAuthenticated = false;
        private string? sessionToken = null; // Session token received after authentication
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly Dictionary<string, Action<JsonElement>> eventHandlers;
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pendingResponses;
        private int messageIdCounter = 0;
        private readonly string logPrefix;

        // Timeout constants
        private static readonly TimeSpan RequestDataTimeout = TimeSpan.FromMinutes(1);

        public event Action<bool> ConnectionChanged = _ => { };
        public event Action<bool> AuthenticationChanged = _ => { };
        public event Action<string, JsonElement> EventReceived = (_, _) => { };

        public bool IsConnected => isConnected;
        public bool IsAuthenticated => isAuthenticated;

        public PluginSocketIOClient(string pluginId, string authToken, int serverPort)
        {
            this.pluginId = pluginId;
            this.authToken = authToken;
            this.serverPort = serverPort;
            this.logPrefix = pluginId.ToUpper();
            this.cancellationTokenSource = new CancellationTokenSource();
            this.eventHandlers = new Dictionary<string, Action<JsonElement>>();
            this.pendingResponses = new Dictionary<string, TaskCompletionSource<JsonElement>>();
            this.webSocket = new ClientWebSocket();
        }

        public async Task ConnectAsync()
        {
            try
            {
                await LogAsync($"Connecting to Socket.IO server on port {serverPort}...");

                var uri = new Uri($"ws://localhost:{serverPort}/socket.io/?EIO=4&transport=websocket");
                await LogAsync($"Connection URL: {uri}");

                await webSocket.ConnectAsync(uri, cancellationTokenSource.Token);

                isConnected = true;
                ConnectionChanged?.Invoke(true);
                await LogAsync("Connected to Socket.IO server");

                // Start listening for messages
                _ = Task.Run(ReceiveLoop);

                // Send Socket.IO handshake
                await LogAsync("Sending Engine.IO open message");
                await SendSocketIOMessage("0", ""); // Engine.IO open message

                await LogAsync("Sending Socket.IO connect message");
                await SendSocketIOMessage("40", ""); // Socket.IO connect message

                // Authenticate
                await AuthenticateAsync();
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Failed to connect to Socket.IO server: {ex.Message}");
                throw;
            }
        }

        private async Task AuthenticateAsync()
        {
            try
            {
                await LogAsync("Authenticating with HyperAI...");
                await LogAsync($"Plugin ID: {this.pluginId}");
                await LogAsync($"Auth Token: {this.authToken.Substring(0, Math.Min(8, this.authToken.Length))}...");

                var authData = new
                {
                    pluginId = this.pluginId,
                    token = this.authToken,
                    challenge = this.authToken
                };

                await EmitAsync("authenticate", authData);
                await LogAsync("Authentication request sent, waiting for response...");
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Authentication failed: {ex.Message}");
                throw;
            }
        }

        public async Task SubscribeToEventsAsync(string[] events)
        {
            if (!isAuthenticated)
            {
                throw new InvalidOperationException("Must be authenticated before subscribing to events");
            }

            try
            {
                await EmitAsync("subscribeEvents", events);
                await LogAsync($"Subscribed to events: {string.Join(", ", events)}");
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Failed to subscribe to events: {ex.Message}");
                throw;
            }
        }

        public async Task<byte[]> RequestFileAsync(string filePath)
        {
            if (!isAuthenticated)
            {
                throw new InvalidOperationException("Must be authenticated before requesting files");
            }

            var requestId = GenerateRequestId();
            var tcs = new TaskCompletionSource<JsonElement>();
            pendingResponses[requestId] = tcs;

            try
            {
                await EmitAsync("requestFile", new { filePath, requestId });

                // Wait for response with timeout
                var response = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5));

                if (response.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var dataBase64 = response.GetProperty("data").GetString();
                    return Convert.FromBase64String(dataBase64 ?? "");
                }
                else
                {
                    var error = response.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString()
                        : "Unknown error";
                    throw new Exception($"File request failed: {error}");
                }
            }
            finally
            {
                pendingResponses.Remove(requestId);
            }
        }

        public async Task<T> RequestDataAsync<T>(string method, object? parameters = null)
        {
            if (!isAuthenticated)
            {
                throw new InvalidOperationException("Must be authenticated before requesting data");
            }

            var requestId = GenerateRequestId();
            var tcs = new TaskCompletionSource<JsonElement>();
            pendingResponses[requestId] = tcs;

            try
            {
                // Include session token in all authenticated requests (per official spec)
                await EmitAsync("requestData", new { method, @params = parameters, requestId, sessionToken });

                var response = await tcs.Task.WaitAsync(RequestDataTimeout);

                if (response.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var data = response.GetProperty("data");
                    return JsonSerializer.Deserialize<T>(data.GetRawText()) ?? default(T)!;
                }
                else
                {
                    var error = response.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString()
                        : "Unknown error";
                    throw new Exception($"Data request failed: {error}");
                }
            }
            finally
            {
                pendingResponses.Remove(requestId);
            }
        }

        public async Task UpdateStatusAsync(string status, string? message = null)
        {
            if (!isAuthenticated) return;

            try
            {
                await EmitAsync("statusUpdate", new { status, message });
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Failed to update status: {ex.Message}");
            }
        }

        public async Task SendLogAsync(string level, string message)
        {
            if (!isAuthenticated) return;

            try
            {
                await EmitAsync("pluginLog", new
                {
                    pluginId,
                    level,
                    message,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            catch
            {
                // Don't log errors about logging - avoid infinite loops
            }
        }

        private static string DescribeForLog(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "<empty>";
            }

            var hasSensitivePayload =
                value.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("\"user\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("\"username\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("cheevos_password", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasSensitivePayload || value.Length > 1000)
            {
                return $"<redacted {value.Length} chars>";
            }

            return value;
        }

        public void OnEvent(string eventType, Action<JsonElement> handler)
        {
            eventHandlers[eventType] = handler;
        }

        public async Task EmitAsync(string eventName, object data)
        {
            await LogAsync($"Emitting event: {eventName}");
            await LogAsync($"Event data: {DescribeForLog(JsonSerializer.Serialize(data))}");

            var message = new object[] { eventName, data };
            var json = JsonSerializer.Serialize(message);

            await LogAsync($"Final message array: {DescribeForLog(json)}");

            await SendSocketIOMessage("42", json);
        }

        private async Task SendSocketIOMessage(string type, string data)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not connected");
            }

            var message = $"{type}{data}";

            await LogAsync($"SENDING MESSAGE: {DescribeForLog(message)}");
            await LogAsync($"Message Type: {type}, Data Length: {data.Length}");

            var buffer = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationTokenSource.Token);
        }

        private async Task ReceiveLoop()
        {
            // Increased buffer size from 4KB to 64KB to handle large payloads
            var buffer = new byte[65536];
            var messageBuilder = new StringBuilder();

            try
            {
                while (webSocket.State == WebSocketState.Open && !cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationTokenSource.Token);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Accumulate message chunks until EndOfMessage is true
                        // This handles fragmented WebSocket messages properly
                        var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(chunk);

                        // Only process when we have the complete message
                        if (result.EndOfMessage)
                        {
                            var message = messageBuilder.ToString();
                            messageBuilder.Clear();
                            await ProcessMessage(message);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await LogAsync("WebSocket connection closed by server");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await LogAsync("WebSocket receive loop cancelled");
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Error in receive loop: {ex.Message}");
            }
            finally
            {
                isConnected = false;
                isAuthenticated = false;
                ConnectionChanged?.Invoke(false);
                AuthenticationChanged?.Invoke(false);
            }
        }

        private async Task ProcessMessage(string message)
        {
            try
            {
                await LogAsync($"RECEIVED MESSAGE: {DescribeForLog(message)}");

                // Parse Socket.IO message format
                if (message.Length < 1)
                {
                    await LogAsync("Received empty message, ignoring");
                    return;
                }

                // Handle single-character messages (like heartbeat "2")
                if (message.Length == 1)
                {
                    switch (message)
                    {
                        case "2": // Engine.IO ping
                            await LogAsync("Received ping, sending pong");
                            await SendSocketIOMessage("3", ""); // Pong
                            break;
                        case "3": // Engine.IO pong
                            await LogAsync("Received pong");
                            break;
                        default:
                            await LogAsync($"Unknown single-char message: {message}");
                            break;
                    }
                    return;
                }

                var messageType = message.Substring(0, 2);
                var payload = message.Length > 2 ? message.Substring(2) : "";

                await LogAsync($"Message Type: {messageType}, Payload Length: {payload.Length}");

                switch (messageType)
                {
                    case "40": // Socket.IO connect
                        await LogAsync("Socket.IO connected");
                        break;

                    case "42": // Socket.IO event
                        await LogAsync($"Processing Socket.IO event with payload: {DescribeForLog(payload)}");
                        await ProcessEvent(payload);
                        break;

                    case "3": // Engine.IO heartbeat (shouldn't reach here with length check above)
                        await LogAsync("Received heartbeat, sending pong");
                        await SendSocketIOMessage("3", ""); // Pong
                        break;

                    default:
                        await LogAsync($"Unknown message type '{messageType}' with payload: {payload}");
                        break;
                }
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Error processing message: {ex.Message}");
            }
        }

        private async Task ProcessEvent(string payload)
        {
            try
            {
                await LogAsync($"Parsing event payload: {DescribeForLog(payload)}");

                var eventData = JsonSerializer.Deserialize<JsonElement[]>(payload);
                if (eventData == null || eventData.Length < 1)
                {
                    await LogAsync("Event data array is empty, ignoring");
                    return;
                }

                var eventName = eventData[0].GetString();
                var eventPayload = eventData.Length > 1 ? eventData[1] : new JsonElement();

                await LogAsync($"Received event: {eventName}");
                await LogAsync($"Event payload: {DescribeForLog(eventPayload.GetRawText())}");

                switch (eventName)
                {
                    case "authenticated":
                        await LogAsync("Processing authenticated event");
                        await HandleAuthenticated(eventPayload);
                        break;

                    case "eventsSubscribed":
                        await LogAsync("Processing eventsSubscribed event");
                        await HandleEventsSubscribed(eventPayload);
                        break;

                    case "hyperHqEvent":
                        await LogAsync("Processing hyperHqEvent");
                        await HandleHyperAIEvent(eventPayload);
                        break;

                    case "fileData":
                        await LogAsync("Processing fileData event");
                        await HandleFileData(eventPayload);
                        break;

                    case "dataResponse":
                        await LogAsync("Processing dataResponse event");
                        await HandleDataResponse(eventPayload);
                        break;

                    case "data_response": // Handle alternate naming convention
                        await LogAsync("Processing data_response event");
                        await HandleDataResponse(eventPayload);
                        break;

                    case "request":
                        await LogAsync("Processing API request event");
                        break; // Will be handled by custom event handlers

                    case "response":
                        await LogAsync("Processing API response event");
                        break; // Will be handled by custom event handlers

                    case "error":
                        await LogAsync("Processing error event");
                        await HandleError(eventPayload);
                        break;

                    default:
                        await LogAsync($"Unknown event type: {eventName} with payload: {eventPayload.GetRawText()}");
                        break;
                }

                // Call custom event handlers
                if (eventHandlers.ContainsKey(eventName ?? ""))
                {
                    await LogAsync($"Calling custom event handler for: {eventName}");
                    eventHandlers[eventName!](eventPayload);
                }
                else
                {
                    await LogAsync($"No custom event handler registered for: {eventName}");
                }
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Error processing event: {ex.Message}");
                await LogErrorAsync($"Failed payload was: {payload}");
            }
        }

        private async Task HandleAuthenticated(JsonElement data)
        {
            if (data.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                // Extract session token from authentication response (per official spec)
                if (data.TryGetProperty("sessionToken", out var tokenProp))
                {
                    sessionToken = tokenProp.GetString();
                    await LogAsync($"Session token received: {sessionToken?.Substring(0, Math.Min(8, sessionToken?.Length ?? 0))}...");
                }
                else
                {
                    await LogAsync("WARNING: No session token in authentication response");
                }

                isAuthenticated = true;
                AuthenticationChanged?.Invoke(true);
                await LogAsync("Successfully authenticated with HyperHQ");
            }
            else
            {
                var error = data.TryGetProperty("error", out var errorProp)
                    ? errorProp.GetString()
                    : "Unknown authentication error";
                await LogErrorAsync($"Authentication failed: {error}");
            }
        }

        private async Task HandleEventsSubscribed(JsonElement data)
        {
            if (data.TryGetProperty("events", out var events))
            {
                var eventNames = JsonSerializer.Deserialize<string[]>(events.GetRawText());
                await LogAsync($"Successfully subscribed to events: {string.Join(", ", eventNames ?? Array.Empty<string>())}");
            }
        }

        private async Task HandleHyperAIEvent(JsonElement data)
        {
            if (data.TryGetProperty("type", out var eventType))
            {
                var eventName = eventType.GetString();
                var eventData = data.TryGetProperty("data", out var eventDataProp)
                    ? eventDataProp
                    : new JsonElement();

                await LogAsync($"Received HyperAI event: {eventName}");
                EventReceived?.Invoke(eventName ?? "", eventData);
            }
        }

        private async Task HandleFileData(JsonElement data)
        {
            if (data.TryGetProperty("requestId", out var requestIdProp))
            {
                var requestId = requestIdProp.GetString();
                if (requestId != null && pendingResponses.ContainsKey(requestId))
                {
                    pendingResponses[requestId].SetResult(data);
                }
            }
        }

        private async Task HandleDataResponse(JsonElement data)
        {
            if (data.TryGetProperty("requestId", out var requestIdProp))
            {
                var requestId = requestIdProp.GetString();
                if (requestId != null && pendingResponses.ContainsKey(requestId))
                {
                    pendingResponses[requestId].SetResult(data);
                }
            }
        }

        private async Task HandleError(JsonElement data)
        {
            var message = data.TryGetProperty("message", out var messageProp)
                ? messageProp.GetString()
                : "Unknown error";
            await LogErrorAsync($"Server error: {message}");

            // Check if this is a response to a pending request
            if (data.TryGetProperty("requestId", out var requestIdProp))
            {
                var requestId = requestIdProp.GetString();
                if (requestId != null && pendingResponses.ContainsKey(requestId))
                {
                    pendingResponses[requestId].SetException(new Exception(message ?? "Unknown error"));
                }
            }
        }

        private string GenerateRequestId()
        {
            return $"{pluginId}_{Interlocked.Increment(ref messageIdCounter)}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        public async Task DisconnectAsync()
        {
            if (webSocket?.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin shutting down", CancellationToken.None);
            }
        }

        public void Dispose()
        {
            cancellationTokenSource?.Cancel();
            webSocket?.Dispose();
            cancellationTokenSource?.Dispose();
        }

        private async Task LogAsync(string message)
        {
            var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var formattedMessage = $"[{DateTime.UtcNow:O}] {logPrefix}-SOCKETIO-INFO: {message}";

            try
            {
                // Only write to console if we can (avoid issues in background processes)
                if (Environment.UserInteractive)
                {
                    WriteColoredSocketIOLog(timestamp, $"{logPrefix}-SOCKETIO", message, ConsoleColor.Magenta, ConsoleColor.Gray);
                }
            }
            catch
            {
                // Ignore console errors in background mode
            }

            // Always write to log file
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, $"{pluginId.ToLower()}-socketio.log");
                await File.AppendAllTextAsync(logPath, $"{formattedMessage}{Environment.NewLine}");
            }
            catch
            {
                // If file logging fails, we can't do much about it
            }
        }

        private async Task LogErrorAsync(string message)
        {
            var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var formattedMessage = $"[{DateTime.UtcNow:O}] {logPrefix}-SOCKETIO-ERROR: {message}";

            try
            {
                // Only write to console if we can (avoid issues in background processes)
                if (Environment.UserInteractive)
                {
                    WriteColoredSocketIOLog(timestamp, $"{logPrefix}-SOCKETIO-ERR", message, ConsoleColor.Red, ConsoleColor.White);
                }
            }
            catch
            {
                // Ignore console errors in background mode
            }

            // Always write to log file
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, $"{pluginId.ToLower()}-socketio.log");
                await File.AppendAllTextAsync(logPath, $"{formattedMessage}{Environment.NewLine}");
            }
            catch
            {
                // If file logging fails, we can't do much about it
            }
        }

        private static void WriteColoredSocketIOLog(string timestamp, string level, string message, ConsoleColor levelColor, ConsoleColor messageColor)
        {
            try
            {
                var originalColor = Console.ForegroundColor;

                // Write timestamp in dark gray
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{timestamp}] ");

                // Write level in specified color with Socket.IO indicator
                Console.ForegroundColor = levelColor;
                Console.Write($" {level.PadRight(15)}: ");
                // Write message with special highlighting for different types
                if (message.Contains("RECEIVED") || message.Contains("SENDING"))
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                }
                else if (message.Contains("Connected") || message.Contains("authenticated"))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }
                else if (message.Contains("ERROR") || message.Contains("Failed"))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                else if (message.Contains("WARNING"))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
                else if (message.Contains("heartbeat"))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                }
                else
                {
                    Console.ForegroundColor = messageColor;
                }

                Console.WriteLine(message);

                // Reset color
                Console.ForegroundColor = originalColor;
            }
            catch
            {
                // Fallback to simple output if colors fail
                Console.WriteLine($"[{timestamp}] {level}: {message}");
            }
        }
    }
}
