using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NCBA.DCL.Models;
using NCBA.DCL.Services;

namespace NCBA.DCL.Hubs;

[Authorize]
public class DclHub : Hub
{
    private readonly OnlineUserTracker _tracker;
    private readonly ILogger<DclHub> _logger;

    public DclHub(OnlineUserTracker tracker, ILogger<DclHub> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        try
        {
            var userIdClaim = Context.User?.FindFirst("id")?.Value;
            if (userIdClaim != null && Guid.TryParse(userIdClaim, out var userId))
            {
                var user = new OnlineUser
                {
                    Id = userId,
                    Name = Context.User?.Identity?.Name,
                    Email = Context.User?.FindFirst("email")?.Value,
                    Role = Context.User?.FindFirst("role")?.Value,
                    IpAddress = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = Context.GetHttpContext()?.Request.Headers["User-Agent"].ToString(),
                    LoginTime = DateTime.UtcNow,
                };

                _tracker.AddSocket(userId, Context.ConnectionId, user);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error on connected");
        }

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var userIdClaim = Context.User?.FindFirst("id")?.Value;
            if (userIdClaim != null && Guid.TryParse(userIdClaim, out var userId))
            {
                _tracker.RemoveSocket(userId, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error on disconnected");
        }

        return base.OnDisconnectedAsync(exception);
    }

    public Task RegisterPage(string page)
    {
        var userIdClaim = Context.User?.FindFirst("id")?.Value;
        if (userIdClaim != null && Guid.TryParse(userIdClaim, out var userId))
        {
            var u = _tracker.Get(userId);
            if (u != null)
            {
                u.CurrentPage = page;
                u.LastSeen = DateTime.UtcNow;
            }
        }

        return Task.CompletedTask;
    }
}
