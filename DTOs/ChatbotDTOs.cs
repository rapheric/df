using System.Text.Json.Serialization;

namespace NCBA.DCL.DTOs;

public class ChatbotPublicConfigDto
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "ProxyApi";
    public string Title { get; set; } = "DCL Assistant";
    public string WelcomeMessage { get; set; } = "Hello. How can I help you today?";
    public string Position { get; set; } = "bottom-right";
    public bool ProxyEnabled { get; set; } = true;
}

public class CreateChatSessionRequest
{
    public string? ClientId { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Locale { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class CreateChatSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public ChatMessageDto? WelcomeMessage { get; set; }
}

public class SendChatMessageRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Locale { get; set; }
    public List<ChatMessageDto>? History { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class SendChatMessageResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public List<ChatMessageDto> Messages { get; set; } = [];
    public DateTimeOffset RespondedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Watermark { get; set; }
}

public class EndChatSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public bool Closed { get; set; }
}

public class ChatbotErrorResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
}

public class ChatMessageDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "assistant";
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; set; }
}