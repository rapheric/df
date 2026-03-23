using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.Models;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(ApplicationDbContext context, ILogger<NotificationsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetNotifications()
    {
        try
        {
            var userIdStr = User.FindFirst("id")?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var notifications = await _context.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();

            return Ok(notifications);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notifications");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPut("{id}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id)
    {
        try
        {
            var userIdStr = User.FindFirst("id")?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var notif = await _context.Notifications.FindAsync(id);
            if (notif == null) return NotFound(new { message = "Notification not found" });
            if (notif.UserId != userId) return Forbid();

            notif.Read = true;
            notif.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Marked as read" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking notification as read");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPut("read-all")]
    public async Task<IActionResult> MarkAllAsRead()
    {
        try
        {
            var userIdStr = User.FindFirst("id")?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var notifications = await _context.Notifications
                .Where(n => n.UserId == userId && !n.Read)
                .ToListAsync();

            if (notifications.Count == 0)
            {
                return Ok(new { message = "No unread notifications" });
            }

            foreach (var notification in notifications)
            {
                notification.Read = true;
                notification.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "All notifications marked as read", count = notifications.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking all notifications as read");
            return StatusCode(500, new { message = ex.Message });
        }
    }
}
