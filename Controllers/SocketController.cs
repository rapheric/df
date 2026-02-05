using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBA.DCL.Services;
using NCBA.DCL.Data;
using NCBA.DCL.Models;
using Microsoft.EntityFrameworkCore;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/socket")]
[Authorize]
public class SocketController : ControllerBase
{
    private readonly OnlineUserTracker _tracker;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<SocketController> _logger;

    public SocketController(OnlineUserTracker tracker, ApplicationDbContext context, ILogger<SocketController> logger)
    {
        _tracker = tracker;
        _context = context;
        _logger = logger;
    }

    [HttpGet("online-users")]
    public IActionResult GetOnlineUsers()
    {
        var users = _tracker.GetAll()
            .Select(u => new
            {
                _id = u.Id,
                name = u.Name,
                email = u.Email,
                role = u.Role,
                lastSeen = u.LastSeen,
                currentPage = u.CurrentPage,
                loginTime = u.LoginTime,
                ipAddress = u.IpAddress,
                userAgent = u.UserAgent,
                socketCount = u.SocketIds.Count
            })
            .OrderBy(u => u.name)
            .ToList();

        return Ok(new { success = true, count = users.Count, users, timestamp = DateTime.UtcNow });
    }

    [HttpGet("debug-online-users")]
    public IActionResult DebugOnlineUsers()
    {
        var all = _tracker.GetAll();
        var debug = new
        {
            timestamp = DateTime.UtcNow,
            totalUsers = all.Count,
            totalSockets = all.Sum(u => u.SocketIds.Count),
            users = all.Select(u => new
            {
                _id = u.Id,
                name = u.Name,
                email = u.Email,
                role = u.Role,
                lastSeen = u.LastSeen,
                loginTime = u.LoginTime,
                currentPage = u.CurrentPage,
                socketCount = u.SocketIds.Count,
                socketIds = u.SocketIds.ToArray(),
                ipAddress = u.IpAddress,
                userAgent = u.UserAgent
            })
        };

        return Ok(new { success = true, data = debug });
    }

    [HttpPost("force-logout")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ForceLogout([FromBody] ForceLogoutRequest req)
    {
        try
        {
            if (!_tracker.ForceLogout(req.UserId, out var removed))
            {
                return NotFound(new { success = false, message = "User not found or already offline" });
            }

            // Optionally: create audit log
            var log = new AuditLog
            {
                Id = Guid.NewGuid(),
                Action = "force_logout",
                PerformedBy = req.AdminId,
                TargetUser = req.UserId,
                Status = "success",
                Resource = "socket",
                Details = $"Admin {req.AdminId} forced logout of {req.UserId}",
                CreatedAt = DateTime.UtcNow
            };

            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync();

            return Ok(new { success = true, message = $"User {removed?.Name} has been logged out" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forcing logout");
            return StatusCode(500, new { success = false, message = "Failed to force logout user" });
        }
    }
}

public class ForceLogoutRequest
{
    public Guid UserId { get; set; }
    public Guid AdminId { get; set; }
}
