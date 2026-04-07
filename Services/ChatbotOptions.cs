namespace NCBA.DCL.Services;

public class ChatbotOptions
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "ProxyApi";
    public string Title { get; set; } = "DCL Assistant";
    public string WelcomeMessage { get; set; } = "Hello. How can I help you today?";
    public string Position { get; set; } = "bottom-right";
    public bool ProxyEnabled { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 45;
    public int SessionTimeoutMinutes { get; set; } = 30;
    public string ProviderBaseUrl { get; set; } = string.Empty;
    public string SessionEndpoint { get; set; } = "/sessions";
    public string MessageEndpoint { get; set; } = "/messages";
    public string EndSessionEndpoint { get; set; } = "/sessions/{sessionId}";
    public string AuthType { get; set; } = "None";
    public string ApiKeyHeaderName { get; set; } = "x-api-key";
    public string ApiKey { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";
    public string TokenEndpoint { get; set; } = string.Empty;
    public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DirectLineBaseUrl { get; set; } = "https://directline.botframework.com/v3/directline";
    public string DirectLineSecret { get; set; } = string.Empty;
    public int DirectLinePollAttempts { get; set; } = 6;
    public int DirectLinePollDelayMilliseconds { get; set; } = 750;
}