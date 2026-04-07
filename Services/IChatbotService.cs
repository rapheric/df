using NCBA.DCL.DTOs;

namespace NCBA.DCL.Services;

public interface IChatbotService
{
    Task<ChatbotPublicConfigDto> GetPublicConfigAsync(CancellationToken cancellationToken = default);
    Task<CreateChatSessionResponse> StartSessionAsync(CreateChatSessionRequest request, CancellationToken cancellationToken = default);
    Task<SendChatMessageResponse> SendMessageAsync(SendChatMessageRequest request, CancellationToken cancellationToken = default);
    Task<EndChatSessionResponse> EndSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}