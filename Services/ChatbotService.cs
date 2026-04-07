using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.Extensions.Options;
using NCBA.DCL.DTOs;

namespace NCBA.DCL.Services;

public class ChatbotService : IChatbotService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChatbotService> _logger;
    private readonly IOptionsMonitor<ChatbotOptions> _optionsMonitor;
    private readonly ConcurrentDictionary<string, ChatbotSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessTokenState? _accessToken;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ChatbotService(
        IHttpClientFactory httpClientFactory,
        ILogger<ChatbotService> logger,
        IOptionsMonitor<ChatbotOptions> optionsMonitor)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _optionsMonitor = optionsMonitor;
    }

    private ChatbotOptions Options => _optionsMonitor.CurrentValue;

    public Task<ChatbotPublicConfigDto> GetPublicConfigAsync(CancellationToken cancellationToken = default)
    {
        var options = Options;
        return Task.FromResult(new ChatbotPublicConfigDto
        {
            Enabled = options.Enabled,
            Mode = options.Mode,
            Title = options.Title,
            WelcomeMessage = options.WelcomeMessage,
            Position = options.Position,
            ProxyEnabled = options.ProxyEnabled,
        });
    }

    public async Task<CreateChatSessionResponse> StartSessionAsync(CreateChatSessionRequest request, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        CleanupExpiredSessions();

        var sessionId = Guid.NewGuid().ToString("N");
        var state = new ChatbotSessionState
        {
            SessionId = sessionId,
            ClientId = request.ClientId,
            UserId = request.UserId,
            UserName = request.UserName,
            Locale = request.Locale,
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastActivityAtUtc = DateTimeOffset.UtcNow,
        };

        switch (NormalizeMode())
        {
            case "directline":
                await InitializeDirectLineSessionAsync(state, cancellationToken);
                break;
            case "proxyapi":
                await InitializeProxySessionAsync(state, request, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported chatbot mode '{Options.Mode}'.");
        }

        _sessions[sessionId] = state;
        _logger.LogInformation("Started chatbot session {SessionId} in {Mode} mode", sessionId, Options.Mode);

        return new CreateChatSessionResponse
        {
            SessionId = sessionId,
            ConversationId = state.ConversationId,
            StartedAtUtc = state.StartedAtUtc,
            ExpiresAtUtc = state.StartedAtUtc.AddMinutes(Math.Max(Options.SessionTimeoutMinutes, 1)),
            WelcomeMessage = string.IsNullOrWhiteSpace(Options.WelcomeMessage)
                ? null
                : new ChatMessageDto
                {
                    Role = "assistant",
                    Text = Options.WelcomeMessage,
                    TimestampUtc = DateTimeOffset.UtcNow,
                },
        };
    }

    public async Task<SendChatMessageResponse> SendMessageAsync(SendChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new InvalidOperationException("A chatbot sessionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new InvalidOperationException("A chatbot message cannot be empty.");
        }

        if (!_sessions.TryGetValue(request.SessionId, out var session))
        {
            throw new KeyNotFoundException($"Chatbot session '{request.SessionId}' was not found or has expired.");
        }

        session.LastActivityAtUtc = DateTimeOffset.UtcNow;
        List<ChatMessageDto> messages;

        switch (NormalizeMode())
        {
            case "directline":
                messages = await SendDirectLineMessageAsync(session, request, cancellationToken);
                break;
            case "proxyapi":
                messages = await SendProxyMessageAsync(session, request, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported chatbot mode '{Options.Mode}'.");
        }

        _logger.LogInformation(
            "Processed chatbot message for session {SessionId}. ResponseCount={ResponseCount}",
            request.SessionId,
            messages.Count);

        return new SendChatMessageResponse
        {
            SessionId = request.SessionId,
            ConversationId = session.ConversationId,
            Messages = messages,
            RespondedAtUtc = DateTimeOffset.UtcNow,
            Watermark = session.Watermark,
        };
    }

    public async Task<EndChatSessionResponse> EndSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("A chatbot sessionId is required.");
        }

        if (_sessions.TryRemove(sessionId, out var session) && NormalizeMode() == "proxyapi")
        {
            await EndProxySessionAsync(session, cancellationToken);
        }

        _logger.LogInformation("Closed chatbot session {SessionId}", sessionId);
        return new EndChatSessionResponse { SessionId = sessionId, Closed = true };
    }

    private void EnsureEnabled()
    {
        if (!Options.Enabled)
        {
            throw new InvalidOperationException("Chatbot integration is disabled.");
        }
    }

    private string NormalizeMode() => (Options.Mode ?? string.Empty).Trim().ToLowerInvariant();

    private void CleanupExpiredSessions()
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        var sessionLifetime = TimeSpan.FromMinutes(Math.Max(Options.SessionTimeoutMinutes, 1));
        var cutoff = DateTimeOffset.UtcNow.Subtract(sessionLifetime);
        foreach (var entry in _sessions)
        {
            if (entry.Value.LastActivityAtUtc < cutoff)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }

    private async Task InitializeProxySessionAsync(
        ChatbotSessionState state,
        CreateChatSessionRequest request,
        CancellationToken cancellationToken)
    {
        EnsureProxyProviderConfigured();

        if (string.IsNullOrWhiteSpace(Options.ProviderBaseUrl) || string.IsNullOrWhiteSpace(Options.SessionEndpoint))
        {
            state.RemoteSessionId = state.SessionId;
            state.ConversationId = state.SessionId;
            return;
        }

        var response = await ExecuteWithRetryAsync(
            () => SendJsonAsync(HttpMethod.Post, BuildUrl(Options.ProviderBaseUrl, Options.SessionEndpoint), request, cancellationToken),
            cancellationToken);

        using var document = await ReadJsonDocumentAsync(response, cancellationToken);
        state.RemoteSessionId = ReadStringProperty(document.RootElement, "sessionId", "conversationId", "id") ?? state.SessionId;
        state.ConversationId = ReadStringProperty(document.RootElement, "conversationId", "sessionId", "id") ?? state.RemoteSessionId;
    }

    private async Task InitializeDirectLineSessionAsync(ChatbotSessionState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Options.DirectLineSecret))
        {
            throw new InvalidOperationException("Chatbot.DirectLineSecret must be configured when Mode is DirectLine.");
        }

        var response = await ExecuteWithRetryAsync(
            () => SendRawAsync(HttpMethod.Post, BuildUrl(Options.DirectLineBaseUrl, "/conversations"), null, cancellationToken, useDirectLineSecret: true),
            cancellationToken);

        using var document = await ReadJsonDocumentAsync(response, cancellationToken);
        state.ConversationId = ReadStringProperty(document.RootElement, "conversationId")
            ?? throw new InvalidOperationException("The Direct Line start-conversation response did not include a conversationId.");
        state.DirectLineToken = ReadStringProperty(document.RootElement, "token");
        state.RemoteSessionId = state.ConversationId;
        state.Watermark = ReadStringProperty(document.RootElement, "watermark");
    }

    private async Task<List<ChatMessageDto>> SendProxyMessageAsync(
        ChatbotSessionState session,
        SendChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        EnsureProxyProviderConfigured();

        if (string.IsNullOrWhiteSpace(Options.ProviderBaseUrl) || string.IsNullOrWhiteSpace(Options.MessageEndpoint))
        {
            throw new InvalidOperationException("Chatbot.ProviderBaseUrl and Chatbot.MessageEndpoint must be configured for ProxyApi mode.");
        }

        var proxyRequest = new
        {
            sessionId = session.RemoteSessionId ?? request.SessionId,
            conversationId = session.ConversationId,
            text = request.Text,
            userId = request.UserId ?? session.UserId,
            userName = request.UserName ?? session.UserName,
            locale = request.Locale ?? session.Locale,
            history = request.History,
            metadata = request.Metadata,
        };

        var response = await ExecuteWithRetryAsync(
            () => SendJsonAsync(HttpMethod.Post, BuildUrl(Options.ProviderBaseUrl, Options.MessageEndpoint), proxyRequest, cancellationToken),
            cancellationToken);

        using var document = await ReadJsonDocumentAsync(response, cancellationToken);
        var normalizedMessages = ExtractMessages(document.RootElement);
        if (normalizedMessages.Count == 0)
        {
            throw new InvalidOperationException("The chatbot proxy returned a response without any assistant messages.");
        }

        return normalizedMessages;
    }

    private async Task<List<ChatMessageDto>> SendDirectLineMessageAsync(
        ChatbotSessionState session,
        SendChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ConversationId))
        {
            throw new InvalidOperationException("The Direct Line conversation has not been initialized.");
        }

        var payload = new
        {
            type = "message",
            from = new
            {
                id = request.UserId ?? session.UserId ?? "web-user",
                name = request.UserName ?? session.UserName ?? "Web User",
            },
            text = request.Text,
            locale = request.Locale ?? session.Locale ?? "en-US",
        };

        var activitiesEndpoint = BuildUrl(Options.DirectLineBaseUrl, $"/conversations/{session.ConversationId}/activities");
        await ExecuteWithRetryAsync(
            () => SendJsonAsync(HttpMethod.Post, activitiesEndpoint, payload, cancellationToken, useDirectLineToken: true, session: session),
            cancellationToken);

        var botMessages = new List<ChatMessageDto>();
        for (var attempt = 0; attempt < Math.Max(Options.DirectLinePollAttempts, 1); attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(Math.Max(Options.DirectLinePollDelayMilliseconds, 100), cancellationToken);
            }

            var pollUrl = string.IsNullOrWhiteSpace(session.Watermark)
                ? activitiesEndpoint
                : $"{activitiesEndpoint}?watermark={Uri.EscapeDataString(session.Watermark)}";

            var pollResponse = await ExecuteWithRetryAsync(
                () => SendRawAsync(HttpMethod.Get, pollUrl, null, cancellationToken, useDirectLineToken: true, session: session),
                cancellationToken);

            using var document = await ReadJsonDocumentAsync(pollResponse, cancellationToken);
            session.Watermark = ReadStringProperty(document.RootElement, "watermark") ?? session.Watermark;

            if (!document.RootElement.TryGetProperty("activities", out var activitiesElement) || activitiesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var activity in activitiesElement.EnumerateArray())
            {
                var activityType = ReadStringProperty(activity, "type");
                if (!string.Equals(activityType, "message", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fromElement = activity.TryGetProperty("from", out var source) ? source : default;
                var fromId = fromElement.ValueKind == JsonValueKind.Object
                    ? ReadStringProperty(fromElement, "id")
                    : null;
                if (string.Equals(fromId, request.UserId ?? session.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = ReadStringProperty(activity, "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                botMessages.Add(new ChatMessageDto
                {
                    Id = ReadStringProperty(activity, "id") ?? Guid.NewGuid().ToString("N"),
                    Role = "assistant",
                    Text = text,
                    TimestampUtc = ReadDateTime(activity, "timestamp") ?? DateTimeOffset.UtcNow,
                    Metadata = ExtractActivityMetadata(activity),
                });
            }

            if (botMessages.Count > 0)
            {
                return botMessages;
            }
        }

        throw new TimeoutException("The chatbot did not return a response before the timeout window expired.");
    }

    private async Task EndProxySessionAsync(ChatbotSessionState session, CancellationToken cancellationToken)
    {
        if (!IsUsableProviderBaseUrl())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Options.ProviderBaseUrl) || string.IsNullOrWhiteSpace(Options.EndSessionEndpoint))
        {
            return;
        }

        var endpoint = Options.EndSessionEndpoint.Replace("{sessionId}", session.RemoteSessionId ?? session.SessionId, StringComparison.OrdinalIgnoreCase);
        try
        {
            await SendRawAsync(HttpMethod.Delete, BuildUrl(Options.ProviderBaseUrl, endpoint), null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close remote chatbot session {SessionId}", session.SessionId);
        }
    }

    private async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        Func<Task<HttpResponseMessage>> action,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                lastResponse = await action();
                if ((int)lastResponse.StatusCode >= 500 && attempt < 2)
                {
                    _logger.LogWarning("Chatbot upstream returned {StatusCode}. Retrying attempt {Attempt}.", (int)lastResponse.StatusCode, attempt + 1);
                    continue;
                }

                if (!lastResponse.IsSuccessStatusCode)
                {
                    var body = await lastResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"Chatbot upstream returned {(int)lastResponse.StatusCode}: {Truncate(body, 500)}",
                        null,
                        lastResponse.StatusCode);
                }

                return lastResponse;
            }
            catch (Exception ex) when (attempt < 2 && IsTransient(ex))
            {
                lastException = ex;
                _logger.LogWarning(ex, "Chatbot request failed on attempt {Attempt}. Retrying once.", attempt);
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }

        if (lastResponse is not null)
        {
            return lastResponse;
        }

        throw new InvalidOperationException("The chatbot request did not produce a response.");
    }

    private bool IsTransient(Exception exception)
    {
        if (exception is TimeoutException or TaskCanceledException)
        {
            return true;
        }

        return exception is HttpRequestException httpRequestException &&
               (httpRequestException.StatusCode is null || (int)httpRequestException.StatusCode >= 500);
    }

    private void EnsureProxyProviderConfigured()
    {
        if (!IsUsableProviderBaseUrl())
        {
            throw new InvalidOperationException(
                "Chatbot.ProviderBaseUrl is still set to a placeholder. Set it to the real deployed chatbot API URL in appsettings before sending messages.");
        }
    }

    private bool IsUsableProviderBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(Options.ProviderBaseUrl))
        {
            return false;
        }

        return !Options.ProviderBaseUrl.Contains("your-chatbot-host.example.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string url,
        object? payload,
        CancellationToken cancellationToken,
        bool useDirectLineSecret = false,
        bool useDirectLineToken = false,
        ChatbotSessionState? session = null)
    {
        var json = payload is null ? null : JsonSerializer.Serialize(payload, _jsonOptions);
        return await SendRawAsync(method, url, json, cancellationToken, useDirectLineSecret, useDirectLineToken, session);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string url,
        string? payload,
        CancellationToken cancellationToken,
        bool useDirectLineSecret = false,
        bool useDirectLineToken = false,
        ChatbotSessionState? session = null)
    {
        var client = _httpClientFactory.CreateClient("ChatbotProxy");
        using var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(payload))
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        ApplyDefaultHeaders(request);

        if (useDirectLineSecret)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.DirectLineSecret);
        }
        else if (useDirectLineToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session?.DirectLineToken ?? Options.DirectLineSecret);
        }
        else
        {
            await ApplyConfiguredAuthenticationAsync(request, cancellationToken);
        }

        _logger.LogInformation("Chatbot {Method} {Url}", method, url);
        var response = await client.SendAsync(request, cancellationToken);
        _logger.LogInformation("Chatbot response {StatusCode} from {Url}", (int)response.StatusCode, url);
        return response;
    }

    private void ApplyDefaultHeaders(HttpRequestMessage request)
    {
        foreach (var header in Options.DefaultHeaders)
        {
            if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    private async Task ApplyConfiguredAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authType = (Options.AuthType ?? string.Empty).Trim().ToLowerInvariant();
        switch (authType)
        {
            case "apikey":
            case "api-key":
                if (!string.IsNullOrWhiteSpace(Options.ApiKey))
                {
                    request.Headers.TryAddWithoutValidation(Options.ApiKeyHeaderName, Options.ApiKey);
                }
                break;
            case "bearer":
                if (!string.IsNullOrWhiteSpace(Options.BearerToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.BearerToken);
                }
                break;
            case "clientcredentials":
            case "client-credentials":
            case "aad":
            case "azuread":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetClientCredentialsTokenAsync(cancellationToken));
                break;
        }
    }

    private async Task<string> GetClientCredentialsTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _accessToken.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _accessToken.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken.Token;
            }

            if (string.IsNullOrWhiteSpace(Options.TenantId) ||
                string.IsNullOrWhiteSpace(Options.ClientId) ||
                string.IsNullOrWhiteSpace(Options.ClientSecret))
            {
                throw new InvalidOperationException("Chatbot client-credentials auth requires TenantId, ClientId, and ClientSecret.");
            }

            var tokenEndpoint = string.IsNullOrWhiteSpace(Options.TokenEndpoint)
                ? $"https://login.microsoftonline.com/{Options.TenantId}/oauth2/v2.0/token"
                : Options.TokenEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = Options.ClientId,
                    ["client_secret"] = Options.ClientSecret,
                    ["scope"] = string.IsNullOrWhiteSpace(Options.TokenScope) ? "https://ai.azure.com/.default" : Options.TokenScope,
                }),
            };

            var client = _httpClientFactory.CreateClient("ChatbotProxy");
            var response = await client.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Failed to get chatbot access token: {(int)response.StatusCode} {Truncate(payload, 500)}",
                    null,
                    response.StatusCode);
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
            var token = ReadStringProperty(document.RootElement, "access_token")
                ?? throw new InvalidOperationException("The token endpoint did not return an access_token.");

            var expiresInRaw = ReadStringProperty(document.RootElement, "expires_in");
            var expiresInSeconds = int.TryParse(expiresInRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 3600;

            _accessToken = new AccessTokenState
            {
                Token = token,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresInSeconds, 300)),
            };

            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }

    private static string BuildUrl(string baseUrl, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return endpoint;
        }

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
    }

    private static string? ReadStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            var match = element.EnumerateObject().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.String)
            {
                return match.Value.GetString();
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTime(JsonElement element, params string[] propertyNames)
    {
        var value = ReadStringProperty(element, propertyNames);
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }

    private static Dictionary<string, string>? ExtractActivityMetadata(JsonElement activity)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (activity.TryGetProperty("suggestedActions", out var suggestedActionsElement) &&
            suggestedActionsElement.ValueKind == JsonValueKind.Object &&
            suggestedActionsElement.TryGetProperty("actions", out var actionsElement) &&
            actionsElement.ValueKind == JsonValueKind.Array)
        {
            var firstAction = actionsElement.EnumerateArray().FirstOrDefault();
            if (firstAction.ValueKind == JsonValueKind.Object)
            {
                AddCardActionMetadata(metadata, firstAction);
            }
        }

        if (activity.TryGetProperty("attachments", out var attachmentsElement) && attachmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachmentsElement.EnumerateArray())
            {
                if (attachment.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var contentType = ReadStringProperty(attachment, "contentType");
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    metadata.TryAdd("attachmentContentType", contentType);
                }

                if (!attachment.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (contentElement.TryGetProperty("buttons", out var buttonsElement) && buttonsElement.ValueKind == JsonValueKind.Array)
                {
                    var firstButton = buttonsElement.EnumerateArray().FirstOrDefault();
                    if (firstButton.ValueKind == JsonValueKind.Object)
                    {
                        AddCardActionMetadata(metadata, firstButton);
                    }
                }

                if (string.Equals(contentType, "application/vnd.microsoft.card.oauth", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(contentType, "application/vnd.microsoft.card.signin", StringComparison.OrdinalIgnoreCase))
                {
                    metadata.TryAdd("requiresSignIn", "true");
                }
            }
        }

        return metadata.Count == 0 ? null : metadata;
    }

    private static void AddCardActionMetadata(Dictionary<string, string> metadata, JsonElement action)
    {
        var actionType = ReadStringProperty(action, "type") ?? string.Empty;
        var actionTitle = ReadStringProperty(action, "title") ?? "Sign in";
        var actionValue = ReadStringProperty(action, "value") ?? ReadStringProperty(action, "url") ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(actionType))
        {
            metadata["actionType"] = actionType;
        }

        if (!string.IsNullOrWhiteSpace(actionTitle))
        {
            metadata["actionTitle"] = actionTitle;
        }

        if (!string.IsNullOrWhiteSpace(actionValue))
        {
            metadata["actionValue"] = actionValue;
        }

        if (string.Equals(actionType, "signin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actionType, "openUrl", StringComparison.OrdinalIgnoreCase))
        {
            metadata["requiresSignIn"] = "true";
        }
    }

    private List<ChatMessageDto> ExtractMessages(JsonElement rootElement)
    {
        var messages = new List<ChatMessageDto>();
        if (rootElement.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                var text = ReadStringProperty(messageElement, "text", "message", "content");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                messages.Add(new ChatMessageDto
                {
                    Id = ReadStringProperty(messageElement, "id") ?? Guid.NewGuid().ToString("N"),
                    Role = ReadStringProperty(messageElement, "role") ?? "assistant",
                    Text = text,
                    TimestampUtc = ReadDateTime(messageElement, "timestampUtc", "timestamp", "createdAt") ?? DateTimeOffset.UtcNow,
                });
            }
        }

        if (messages.Count > 0)
        {
            return messages;
        }

        var fallbackText = ReadStringProperty(rootElement, "reply", "message", "text", "outputText");
        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            messages.Add(new ChatMessageDto
            {
                Role = "assistant",
                Text = fallbackText,
                TimestampUtc = DateTimeOffset.UtcNow,
            });
        }

        return messages;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed class ChatbotSessionState
    {
        public string SessionId { get; set; } = string.Empty;
        public string? RemoteSessionId { get; set; }
        public string? ConversationId { get; set; }
        public string? DirectLineToken { get; set; }
        public string? Watermark { get; set; }
        public string? ClientId { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? Locale { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset LastActivityAtUtc { get; set; }
    }

    private sealed class AccessTokenState
    {
        public string Token { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}