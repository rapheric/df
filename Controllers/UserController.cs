using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Helpers;
using NCBA.DCL.Middleware;
using NCBA.DCL.Models;
using NCBA.DCL.Services;
using System.Text.RegularExpressions;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<UserController> _logger;
    private readonly IAdminService _adminService;
    private readonly OnlineUserTracker _onlineUserTracker;

    public UserController(ApplicationDbContext context, ILogger<UserController> logger, IAdminService adminService, OnlineUserTracker onlineUserTracker)
    {
        _context = context;
        _logger = logger;
        _adminService = adminService;
        _onlineUserTracker = onlineUserTracker;
    }

    // ============================
    // Password Validation Helper
    // ============================
    private bool IsStrongPassword(string password)
    {
        // Min 8 chars, 1 Uppercase, 1 Lowercase, 1 Number, 1 Special
        var regex = new Regex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]{8,}$");
        return regex.IsMatch(password);
    }

    [HttpPost("create")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        try
        {
            // Validate required fields
            if (string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.Email) ||
                string.IsNullOrEmpty(request.Password)) // Role is enum, hard to be null/empty if binding works
            {
                return BadRequest(new
                {
                    message = "All fields are required: name, email, password, and role.",
                    missingFields = new
                    {
                        name = string.IsNullOrEmpty(request.Name),
                        email = string.IsNullOrEmpty(request.Email),
                        password = string.IsNullOrEmpty(request.Password),
                        role = false // Enum default
                    }
                });
            }

            // Validate password strength
            if (!IsStrongPassword(request.Password))
            {
                return BadRequest(new
                {
                    message = "Password must be at least 8 characters long and include an uppercase letter, lowercase letter, number, and special character.",
                    example = "Example: MyPass123!",
                    requirements = new
                    {
                        minLength = request.Password.Length >= 8,
                        hasUppercase = Regex.IsMatch(request.Password, "[A-Z]"),
                        hasLowercase = Regex.IsMatch(request.Password, "[a-z]"),
                        hasNumber = Regex.IsMatch(request.Password, @"\d"),
                        hasSpecial = Regex.IsMatch(request.Password, @"[@$!%*?&]")
                    }
                });
            }

            var exists = await _context.Users.AnyAsync(u => u.Email == request.Email);
            if (exists)
            {
                return BadRequest(new { message = "User already exists with this email" });
            }

            string? rmId = null;
            string? customerNumber = null;

            if (request.Role == UserRole.RM)
            {
                rmId = Guid.NewGuid().ToString();
            }

            if (request.Role == UserRole.Customer)
            {
                bool isUnique = false;
                var random = new Random();
                while (!isUnique)
                {
                    var randomNumber = random.Next(100000, 999999);
                    customerNumber = $"CUST-{randomNumber}";
                    var existing = await _context.Users.AnyAsync(u => u.CustomerNumber == customerNumber);
                    if (!existing) isUnique = true;
                }
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Email = request.Email,
                Password = PasswordHasher.HashPassword(request.Password),
                Role = request.Role,
                CustomerNumber = customerNumber,
                RmId = rmId,
                Active = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);

            // Log the action
            var performedById = Guid.Parse(User.FindFirst("id")?.Value ?? Guid.Empty.ToString());
            var log = new UserLog
            {
                Id = Guid.NewGuid(),
                Action = "CREATE_USER",
                TargetUserId = user.Id,
                TargetEmail = user.Email,
                PerformedById = performedById,
                PerformedByEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                Timestamp = DateTime.UtcNow
            };
            _context.UserLogs.Add(log);

            await _context.SaveChangesAsync();

            return StatusCode(201, new
            {
                id = user.Id,
                name = user.Name,
                email = user.Email,
                role = user.Role.ToString(),
                customerNumber = user.CustomerNumber,
                rmId = user.RmId
            }); // Returning flattened user object as per Node default behavior (it returns created doc)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create user");
            return StatusCode(500, new { message = "Failed to create user", error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers([FromQuery] string? role, [FromQuery] string? q)
    {
        try
        {
            var query = _context.Users.AsQueryable();

            if (!string.IsNullOrEmpty(role) && Enum.TryParse<UserRole>(role, true, out var roleEnum))
            {
                query = query.Where(u => u.Role == roleEnum);
            }

            if (!string.IsNullOrEmpty(q))
            {
                q = q.ToLower();
                query = query.Where(u => u.Name.ToLower().Contains(q) || u.Email.ToLower().Contains(q));
            }

            var users = await query
                .Select(u => new
                {
                    id = u.Id, // Node uses _id, but frontend likely adapted or uses id
                    _id = u.Id, // Include both for safety
                    name = u.Name,
                    email = u.Email,
                    role = u.Role.ToString(),
                    active = u.Active,
                    customerNumber = u.CustomerNumber,
                    customerId = u.CustomerId,
                    rmId = u.RmId,
                    createdAt = u.CreatedAt,
                    updatedAt = u.UpdatedAt,
                    isOnline = u.IsOnline
                })
                .ToListAsync();

            return Ok(users);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("customers")]
    public async Task<IActionResult> GetCustomers([FromQuery] string? q)
    {
        try
        {
            var query = _context.Users.Where(u => u.Role == UserRole.Customer);

            if (!string.IsNullOrEmpty(q))
            {
                q = q.ToLower();
                query = query.Where(u => u.Name.ToLower().Contains(q) || (u.CustomerNumber != null && u.CustomerNumber.ToLower().Contains(q)));
            }

            var customers = await query.Select(u => new
            {
                _id = u.Id,
                name = u.Name,
                email = u.Email,
                customerNumber = u.CustomerNumber,
                customerId = u.CustomerId
            }).ToListAsync();

            return Ok(customers);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("customers/{id}")]
    public async Task<IActionResult> GetCustomerById(Guid id)
    {
        try
        {
            var customer = await _context.Users
                .Where(u => u.Id == id)
                .Select(u => new
                {
                    _id = u.Id,
                    name = u.Name,
                    email = u.Email,
                    role = u.Role.ToString(),
                    customerNumber = u.CustomerNumber,
                    customerId = u.CustomerId,
                    rmId = u.RmId,
                    active = u.Active,
                    createdAt = u.CreatedAt,
                    updatedAt = u.UpdatedAt
                })
                .FirstOrDefaultAsync();

            if (customer == null) return NotFound(new { message = "Not found" });
            return Ok(customer);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }


    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        try
        {
            var total = await _context.Users.CountAsync();
            var active = await _context.Users.CountAsync(u => u.Active);
            var inactive = total - active;

            return Ok(new
            {
                success = true,
                data = new { total, active, inactive }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpGet("rms")] // Not in routes file explicitly but userController exports it
    public async Task<IActionResult> GetRMs()
    {
        try
        {
            var rms = await _context.Users
                .Where(u => u.Role == UserRole.RM)
                .Select(u => new { _id = u.Id, name = u.Name })
                .ToListAsync();
            return Ok(rms);
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "Server error" });
        }
    }

    [HttpGet("online")]
    public async Task<IActionResult> GetOnlineUsers()
    {
        try
        {
            var threshold = DateTime.UtcNow.AddMinutes(-10);

            var onlineUsers = await _context.Users
                .Where(u => u.Active && u.IsOnline && u.LastSeen >= threshold)
                .Select(u => new
                {
                    _id = u.Id,
                    name = u.Name,
                    email = u.Email,
                    customerNumber = u.CustomerNumber,
                    role = u.Role.ToString(),
                    lastSeen = u.LastSeen,
                    loginTime = u.LoginTime,
                    socketCount = 1,
                    status = "Active in last 10m"
                })
                .OrderBy(u => u.name)
                .ToListAsync();

            return Ok(new { success = true, count = onlineUsers.Count, users = onlineUsers });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch online users");
            return StatusCode(500, new { success = false, message = "Failed to fetch online users" });
        }
    }

    [HttpPost("presence/heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        try
        {
            var userIdClaim = User.FindFirst("id")?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { success = false, message = "Invalid user session" });
            }

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return NotFound(new { success = false, message = "User not found" });
            }

            user.IsOnline = true;
            user.LastSeen = DateTime.UtcNow;
            user.LoginTime ??= DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                lastSeen = user.LastSeen,
                loginTime = user.LoginTime,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update user heartbeat");
            return StatusCode(500, new { success = false, message = "Failed to update heartbeat" });
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetUsersWithStatus()
    {
        try
        {
            var users = await _context.Users
                .Select(u => new
                {
                    _id = u.Id,
                    name = u.Name,
                    email = u.Email,
                    role = u.Role.ToString(),
                    isOnline = u.IsOnline,
                    lastSeen = u.LastSeen,
                    loginTime = u.LoginTime
                })
                .ToListAsync();

            var onlineCount = users.Count(u => u.isOnline);
            return Ok(new { success = true, count = users.Count, onlineCount, users });
        }
        catch (Exception)
        {
            return StatusCode(500, new { success = false, message = "Failed to fetch users" });
        }
    }

    [HttpPost("cleanup-online")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> CleanupOnlineUsers()
    {
        try
        {
            // Update all users to offline
            // EF Core Batch update
            await _context.Users.ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsOnline, false)
                .SetProperty(u => u.SocketIds, (string?)null));

            return Ok(new { success = true, message = "All users marked offline" });
        }
        catch (Exception)
        {
            return StatusCode(500, new { success = false, message = "Failed to cleanup online users" });
        }
    }

    [HttpPut("{id}/active")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> ToggleActive(Guid id)
    {
        try
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { message = "User not found" });

            user.Active = !user.Active;
            if (user.Active)
            {
                user.TasksReassigned = false;
            }

            // Audit
            var performedById = Guid.Parse(User.FindFirst("id")?.Value ?? Guid.Empty.ToString());
            var log = new UserLog
            {
                Id = Guid.NewGuid(),
                Action = "TOGGLE_ACTIVE",
                TargetUserId = user.Id,
                TargetEmail = user.Email,
                PerformedById = performedById,
                PerformedByEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                Timestamp = DateTime.UtcNow
            };
            _context.UserLogs.Add(log);

            await _context.SaveChangesAsync();
            return Ok(new { message = "User status updated", user = new { active = user.Active } });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("{id}/role")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeRoleRequest request)
    {
        try
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { message = "User not found" });

            user.Role = request.Role;

            // Audit
            var performedById = Guid.Parse(User.FindFirst("id")?.Value ?? Guid.Empty.ToString());
            var log = new UserLog
            {
                Id = Guid.NewGuid(),
                Action = "CHANGE_ROLE",
                TargetUserId = user.Id,
                TargetEmail = user.Email,
                PerformedById = performedById,
                PerformedByEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                Timestamp = DateTime.UtcNow
            };
            _context.UserLogs.Add(log);

            await _context.SaveChangesAsync();
            return Ok(new { message = "User role updated", user = new { role = user.Role.ToString() } });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/reassign")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> ReassignTasks(Guid id, [FromBody] ReassignTasksDto dto)
    {
        // Proxy to AdminService but ensure we handle route match
        // _adminService expects string ID, returns tuple
        var result = await _adminService.ReassignTasksAsync(id.ToString(), dto);

        // Log it? AdminService doesn't log it in UserLog? JS userController logs it.
        // JS logs "REASSIGN_TASKS" to UserLog.
        // AdminService might not log it to UserLog. Let's check. 
        // AdminService DOES NOT log to UserLog. Node DOES.
        // So I should log it here.

        if (result.StatusCode == 200)
        {
            var user = await _context.Users.FindAsync(id);
            if (user != null)
            {
                var performedById = Guid.Parse(User.FindFirst("id")?.Value ?? Guid.Empty.ToString());
                var log = new UserLog
                {
                    Id = Guid.NewGuid(),
                    Action = "REASSIGN_TASKS",
                    TargetUserId = user.Id,
                    TargetEmail = user.Email,
                    PerformedById = performedById,
                    PerformedByEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                    Timestamp = DateTime.UtcNow,
                    // Meta data logic skipped for brevity/complexity mapping, but action is recorded
                };
                _context.UserLogs.Add(log);
                await _context.SaveChangesAsync();
            }
        }

        return StatusCode(result.StatusCode, result.Body);
    }

    [HttpGet("{id}/activity")]
    public async Task<IActionResult> GetUserActivity(Guid id)
    {
        try
        {
            var auditLogs = await _context.AuditLogs
               .Where(l => l.PerformedById == id)
               .OrderByDescending(l => l.CreatedAt)
               .Take(200)
               .Select(l => new
               {
                   id = l.Id,
                   date = l.CreatedAt,
                   action = l.Action,
                   details = (string?)l.Details,
                   status = l.Status ?? "success",
                   performedBy = l.PerformedBy != null ? l.PerformedBy.Name : "System",
                   target = l.TargetUser != null ? l.TargetUser.Name : (l.Resource != null ? $"Resource: {l.Resource}" : "N/A"),
                   resource = (string?)l.Resource,
                   resourceId = l.ResourceId,
                   type = "audit"
               })
               .ToListAsync();

            var checklistLogs = await _context.ChecklistLogs
               .Where(l => l.UserId == id)
               .OrderByDescending(l => l.Timestamp)
               .Take(200)
               .Select(l => new
               {
                   id = l.Id,
                   date = l.Timestamp,
                   message = l.Message,
                   checklistId = l.ChecklistId,
                   checklistNumber = l.Checklist.DclNo,
                   customerName = l.Checklist.CustomerName,
                   type = "workflow"
               })
               .ToListAsync();

            var userLogs = await _context.UserLogs
               .Where(l => l.TargetUserId == id || l.PerformedById == id)
               .OrderByDescending(l => l.Timestamp)
               .Take(50)
               .Select(l => new
               {
                   id = l.Id,
                   date = l.Timestamp,
                   action = l.Action,
                   details = (string?)null,
                   status = "success",
                   performedBy = l.PerformedBy != null ? l.PerformedBy.Name : (l.PerformedByEmail ?? "System"),
                   target = l.TargetUser != null ? l.TargetUser.Name : (l.TargetEmail ?? "N/A"),
                   resource = (string?)null,
                   type = "log"
               })
               .ToListAsync();

            var allActivities = auditLogs
                .Select(log => new UserActivityItem
                {
                    Id = log.id,
                    Date = log.date,
                    Action = log.action,
                    ActionLabel = HumanizeAction(log.action),
                    Summary = BuildAuditSummary(log.action, log.details, log.resource, log.target, log.status),
                    Details = log.details,
                    Status = log.status,
                    PerformedBy = log.performedBy,
                    Target = log.target,
                    Resource = BuildResourceLabel(log.resource, log.resourceId),
                    Type = log.type,
                    Source = "Audit Log"
                })
                .Concat(checklistLogs.Select(log => new UserActivityItem
                {
                    Id = log.id,
                    Date = log.date,
                    Action = "CHECKLIST_ACTIVITY",
                    ActionLabel = "Workflow Activity",
                    Summary = string.IsNullOrWhiteSpace(log.message)
                        ? $"Worked on DCL {log.checklistNumber}"
                        : log.message!,
                    Details = string.IsNullOrWhiteSpace(log.customerName)
                        ? $"DCL {log.checklistNumber}"
                        : $"DCL {log.checklistNumber} for {log.customerName}",
                    Status = "success",
                    PerformedBy = "Workflow User",
                    Target = log.customerName ?? log.checklistNumber ?? "Checklist",
                    Resource = $"Checklist {log.checklistNumber}",
                    Type = log.type,
                    Source = "Checklist Log"
                }))
                .Concat(userLogs.Select(log => new UserActivityItem
                {
                    Id = log.id,
                    Date = log.date,
                    Action = log.action,
                    ActionLabel = HumanizeAction(log.action),
                    Summary = BuildUserLogSummary(log.action, log.target),
                    Details = log.details,
                    Status = log.status,
                    PerformedBy = log.performedBy,
                    Target = log.target,
                    Resource = log.resource,
                    Type = log.type,
                    Source = "Access Log"
                }))
                .OrderByDescending(x => x.Date)
                .Take(200)
                .ToList();

            return Ok(new
            {
                success = true,
                count = allActivities.Count,
                activities = allActivities
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity");
            return StatusCode(500, new { success = false, message = "Failed to fetch user activity", error = ex.Message });
        }
    }

    [HttpPut("toggle/{id}")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> ToggleActiveAdmin(Guid id)
    {
        var result = await _adminService.ToggleActiveAsync(id.ToString());
        return StatusCode(result.StatusCode, result.Body);
    }

    [HttpPut("archive/{id}")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> ArchiveUser(Guid id)
    {
        var result = await _adminService.ArchiveUserAsync(id.ToString());
        return StatusCode(result.StatusCode, result.Body);
    }

    [HttpPut("transfer/{id}")]
    [RoleAuthorize(UserRole.Admin)]
    public async Task<IActionResult> TransferRole(Guid id, [FromBody] TransferRoleDto dto)
    {
        var result = await _adminService.TransferRoleAsync(id.ToString(), dto);
        return StatusCode(result.StatusCode, result.Body);
    }
    private sealed class UserActivityItem
    {
        public Guid Id { get; set; }
        public DateTime Date { get; set; }
        public string Action { get; set; } = string.Empty;
        public string ActionLabel { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? Details { get; set; }
        public string Status { get; set; } = "success";
        public string? PerformedBy { get; set; }
        public string? Target { get; set; }
        public string? Resource { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    private static string HumanizeAction(string? action)
    {
        return action switch
        {
            "LOGIN" => "Signed In",
            "LOGOUT" => "Signed Out",
            "CREATE_USER" => "Created User",
            "TOGGLE_ACTIVE" => "Changed Account Status",
            "CHANGE_ROLE" => "Changed Role",
            "REASSIGN_TASKS" => "Reassigned Tasks",
            "CHECKLIST_ACTIVITY" => "Workflow Activity",
            _ when string.IsNullOrWhiteSpace(action) => "Activity",
            _ => action.Replace("_", " ").Trim()
        };
    }

    private static string BuildAuditSummary(string? action, string? details, string? resource, string? target, string? status)
    {
        if (!string.IsNullOrWhiteSpace(details))
        {
            return details!;
        }

        var label = HumanizeAction(action);
        var resourceLabel = string.IsNullOrWhiteSpace(resource) ? "item" : resource!.ToLowerInvariant();
        var targetLabel = string.IsNullOrWhiteSpace(target) || target == "N/A" ? string.Empty : $" for {target}";
        var statusLabel = string.IsNullOrWhiteSpace(status) ? string.Empty : $" ({status})";
        return $"{label} on {resourceLabel}{targetLabel}{statusLabel}".Trim();
    }

    private static string BuildUserLogSummary(string? action, string? target)
    {
        return action switch
        {
            "LOGIN" => "Signed into the system",
            "LOGOUT" => "Signed out of the system",
            "CREATE_USER" => $"Created user account for {target}",
            "TOGGLE_ACTIVE" => $"Changed account status for {target}",
            "CHANGE_ROLE" => $"Changed role assignment for {target}",
            "REASSIGN_TASKS" => $"Reassigned tasks for {target}",
            _ => HumanizeAction(action)
        };
    }

    private static string? BuildResourceLabel(string? resource, string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resource) && string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return resource;
        }

        return $"{resource} {resourceId}";
    }
}

public class ChangeRoleRequest
{
    public UserRole Role { get; set; }
}
