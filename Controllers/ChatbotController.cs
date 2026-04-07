using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBA.DCL.DTOs;
using NCBA.DCL.Services;

namespace NCBA.DCL.Controllers;

[ApiController]
[Authorize]
[Route("api/chatbot")]
public class ChatbotController : ControllerBase
{
    private readonly IChatbotService _chatbotService;
    private readonly ILogger<ChatbotController> _logger;

    public ChatbotController(IChatbotService chatbotService, ILogger<ChatbotController> logger)
    {
        _chatbotService = chatbotService;
        _logger = logger;
    }

    [HttpGet("config")]
    public async Task<ActionResult<ChatbotPublicConfigDto>> GetConfig(CancellationToken cancellationToken)
    {
        return Ok(await _chatbotService.GetPublicConfigAsync(cancellationToken));
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<CreateChatSessionResponse>> StartSession(
        [FromBody] CreateChatSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _chatbotService.StartSessionAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return HandleException(ex, "start a chatbot session");
        }
    }

    [HttpPost("messages")]
    public async Task<ActionResult<SendChatMessageResponse>> SendMessage(
        [FromBody] SendChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _chatbotService.SendMessageAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return HandleException(ex, "send a chatbot message");
        }
    }

    [HttpDelete("sessions/{sessionId}")]
    public async Task<ActionResult<EndChatSessionResponse>> EndSession(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _chatbotService.EndSessionAsync(sessionId, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return HandleException(ex, "end a chatbot session");
        }
    }

    private ActionResult HandleException(Exception exception, string action)
    {
        _logger.LogError(exception, "Failed to {Action}", action);

        return exception switch
        {
            KeyNotFoundException => NotFound(new ChatbotErrorResponseDto { Message = exception.Message, ErrorCode = "session_not_found" }),
            TimeoutException => StatusCode(StatusCodes.Status504GatewayTimeout, new ChatbotErrorResponseDto { Message = exception.Message, ErrorCode = "timeout" }),
            HttpRequestException httpRequestException when httpRequestException.StatusCode == System.Net.HttpStatusCode.BadGateway => StatusCode(StatusCodes.Status502BadGateway, new ChatbotErrorResponseDto { Message = exception.Message, ErrorCode = "upstream_error" }),
            HttpRequestException => StatusCode(StatusCodes.Status502BadGateway, new ChatbotErrorResponseDto { Message = exception.Message, ErrorCode = "upstream_error" }),
            InvalidOperationException => BadRequest(new ChatbotErrorResponseDto { Message = exception.Message, ErrorCode = "invalid_operation" }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new ChatbotErrorResponseDto { Message = "Chatbot integration failed.", ErrorCode = "internal_error" }),
        };
    }
}