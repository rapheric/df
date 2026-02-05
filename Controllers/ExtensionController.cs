using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Middleware;
using NCBA.DCL.Models;
using System.Security.Claims;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/extensions")]
[Authorize]
public class ExtensionController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ExtensionController> _logger;

    public ExtensionController(
        ApplicationDbContext context,
        ILogger<ExtensionController> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ================================
    // RM ROUTES
    // ================================

    // POST /api/extensions
    [HttpPost]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> CreateExtension([FromBody] CreateExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var deferral = await _context.Deferrals.FindAsync(request.DeferralId);
            if (deferral == null)
                return NotFound(new { message = "Deferral not found" });

            // Create extension
            var extension = new Extension
            {
                DeferralId = request.DeferralId,
                DeferralNumber = deferral.DeferralNumber,
                CustomerName = deferral.CustomerName,
                CustomerNumber = deferral.CustomerNumber,
                DclNumber = deferral.DclNumber,
                CurrentDaysSought = deferral.DaysSought,
                RequestedDaysSought = request.RequestedDaysSought,
                ExtensionReason = request.ExtensionReason,
                RequestedById = userId,
                Status = ExtensionStatus.PendingApproval,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Copy approvers from deferral
            var deferralApprovers = await _context.Approvers
                .Where(a => a.DeferralId == deferral.Id)
                .ToListAsync();

            foreach (var approver in deferralApprovers)
            {
                extension.Approvers.Add(new ExtensionApprover
                {
                    UserId = approver.UserId,
                    Role = approver.Role,
                    ApprovalStatus = ApproverApprovalStatus.Pending,
                    IsCurrent = false
                });
            }

            // Set first approver as current
            if (extension.Approvers.Any())
            {
                extension.Approvers.OrderBy(a => a.Id).First().IsCurrent = true;
            }

            _context.Extensions.Add(extension);
            await _context.SaveChangesAsync();

            // TODO: Send email notifications to first approver

            return StatusCode(201, extension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating extension");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/extensions/my
    [HttpGet("my")]
    public async Task<IActionResult> GetMyExtensions()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extensions = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.RequestedBy)
                .Include(e => e.History).ThenInclude(h => h.User)
                .Where(e => e.RequestedById == userId)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting my extensions");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // ================================
    // APPROVER ROUTES
    // ================================

    // GET /api/extensions/approver/queue
    [HttpGet("approver/queue")]
    public async Task<IActionResult> GetApproverQueue()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extensions = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.RequestedBy)
                .Where(e => e.Approvers.Any(a => 
                    a.UserId == userId && 
                    a.IsCurrent == true && 
                    a.ApprovalStatus == ApproverApprovalStatus.Pending))
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting approver queue");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/extensions/approver/actioned
    [HttpGet("approver/actioned")]
    public async Task<IActionResult> GetApproverActioned()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extensions = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.RequestedBy)
                .Where(e => e.Approvers.Any(a => 
                    a.UserId == userId && 
                    a.ApprovalStatus != ApproverApprovalStatus.Pending))
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting actioned extensions");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/extensions/{id}/approve
    [HttpPut("{id}/approve")]
    public async Task<IActionResult> ApproveExtension(Guid id, [FromBody] ApproveExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extension = await _context.Extensions
                .Include(e => e.Approvers)
                .Include(e => e.History)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            var currentApprover = extension.Approvers.FirstOrDefault(a => a.IsCurrent);
            if (currentApprover == null || currentApprover.UserId != userId)
                return StatusCode(403, new { message = "Only current approver can approve" });

            // Mark as approved
            currentApprover.ApprovalStatus = ApproverApprovalStatus.Approved;
            currentApprover.ApprovalDate = DateTime.UtcNow;
            currentApprover.ApprovalComment = request.Comment;
            currentApprover.IsCurrent = false;

            // Add to history
            extension.History.Add(new ExtensionHistory
            {
                Action = "approved_by_approver",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = User.FindFirst(ClaimTypes.Role)?.Value,
                Date = DateTime.UtcNow,
                Comment = request.Comment
            });

            // Move to next approver
            var nextApprover = extension.Approvers
                .OrderBy(a => a.Id) // Assuming Id order implies sequence
                .FirstOrDefault(a => a.ApprovalStatus == ApproverApprovalStatus.Pending);

            if (nextApprover != null)
            {
                nextApprover.IsCurrent = true;
                extension.Status = ExtensionStatus.InReview;
            }
            else
            {
                extension.AllApproversApproved = true;
                extension.Status = ExtensionStatus.Approved;
                // Note: Logic to actually update the deferral days might go here or require creator/checker finalization depending on exact reqs
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "Extension approved", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving extension");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/extensions/{id}/reject
    [HttpPut("{id}/reject")]
    public async Task<IActionResult> RejectExtension(Guid id, [FromBody] RejectExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extension = await _context.Extensions
                .Include(e => e.Approvers)
                .Include(e => e.History)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            var currentApprover = extension.Approvers.FirstOrDefault(a => a.IsCurrent);
            if (currentApprover == null || currentApprover.UserId != userId)
                return StatusCode(403, new { message = "Only current approver can reject" });

            // Mark as rejected
            currentApprover.ApprovalStatus = ApproverApprovalStatus.Rejected;
            currentApprover.ApprovalDate = DateTime.UtcNow;
            currentApprover.ApprovalComment = request.Reason;
            currentApprover.IsCurrent = false;

            extension.Status = ExtensionStatus.Rejected;
            extension.RejectionReason = request.Reason;
            extension.RejectedById = userId;
            extension.RejectedDate = DateTime.UtcNow;

            // Add to history
            extension.History.Add(new ExtensionHistory
            {
                Action = "rejected_by_approver",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = User.FindFirst(ClaimTypes.Role)?.Value,
                Date = DateTime.UtcNow,
                Comment = request.Reason
            });

            await _context.SaveChangesAsync();

            return Ok(new { message = "Extension rejected", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting extension");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // ================================
    // CREATOR ROUTES
    // ================================

    // GET /api/extensions/creator/pending
    [HttpGet("creator/pending")]
    [RoleAuthorize(UserRole.CoCreator)]
    public async Task<IActionResult> GetCreatorPending()
    {
        try
        {
            var extensions = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.RequestedBy)
                .Where(e => e.CreatorApprovalStatus == CreatorApprovalStatus.Pending)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting creator pending");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/extensions/{id}/approve-creator
    [HttpPut("{id}/approve-creator")]
    [RoleAuthorize(UserRole.CoCreator)]
    public async Task<IActionResult> ApproveAsCreator(Guid id, [FromBody] ApproveExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extension = await _context.Extensions
                .Include(e => e.History)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            extension.CreatorApprovalStatus = CreatorApprovalStatus.Approved;
            extension.CreatorApprovedById = userId;
            extension.CreatorApprovalDate = DateTime.UtcNow;
            extension.CreatorApprovalComment = request.Comment;

            extension.History.Add(new ExtensionHistory
            {
                Action = "approved_by_creator",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = "Creator",
                Date = DateTime.UtcNow,
                Comment = request.Comment
            });

            await _context.SaveChangesAsync();

            return Ok(new { message = "Extension approved by creator", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving as creator");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/extensions/{id}/reject-creator
    [HttpPut("{id}/reject-creator")]
    [RoleAuthorize(UserRole.CoCreator)]
    public async Task<IActionResult> RejectAsCreator(Guid id, [FromBody] RejectExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extension = await _context.Extensions
                .Include(e => e.History)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            extension.CreatorApprovalStatus = CreatorApprovalStatus.Rejected;
            extension.CreatorApprovedById = userId;
            extension.CreatorApprovalDate = DateTime.UtcNow;
            extension.CreatorApprovalComment = request.Reason;
            extension.Status = ExtensionStatus.Rejected;

            extension.History.Add(new ExtensionHistory
            {
                Action = "rejected_by_creator",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = "Creator",
                Date = DateTime.UtcNow,
                Comment = request.Reason
            });

            await _context.SaveChangesAsync();

            return Ok(new { message = "Extension rejected by creator", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting as creator");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // ================================
    // CHECKER ROUTES
    // ================================

    // GET /api/extensions/checker/pending
    [HttpGet("checker/pending")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> GetCheckerPending()
    {
        try
        {
            var extensions = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.RequestedBy)
                .Where(e => e.CheckerApprovalStatus == CheckerApprovalStatus.Pending)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting checker pending");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/extensions/{id}/approve-checker
    [HttpPut("{id}/approve-checker")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> ApproveAsChecker(Guid id, [FromBody] ApproveExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extension = await _context.Extensions
                .Include(e => e.History)
                .Include(e => e.Deferral)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            extension.CheckerApprovalStatus = CheckerApprovalStatus.Approved;
            extension.CheckerApprovedById = userId;
            extension.CheckerApprovalDate = DateTime.UtcNow;
            extension.CheckerApprovalComment = request.Comment;
            extension.Status = ExtensionStatus.Approved;

            extension.History.Add(new ExtensionHistory
            {
                Action = "approved_by_checker",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = "Checker",
                Date = DateTime.UtcNow,
                Comment = request.Comment
            });

            // Update original deferral
            if (extension.Deferral != null)
            {
                extension.Deferral.DaysSought = extension.RequestedDaysSought;
                // Potentially update deferral status or add note
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "Extension approved by checker", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving as checker");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/extensions/{id}/reject-checker
    [HttpPut("{id}/reject-checker")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> RejectAsChecker(Guid id, [FromBody] RejectExtensionRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            
            var extension = await _context.Extensions
                .Include(e => e.History)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            extension.CheckerApprovalStatus = CheckerApprovalStatus.Rejected;
            extension.CheckerApprovedById = userId;
            extension.CheckerApprovalDate = DateTime.UtcNow;
            extension.CheckerApprovalComment = request.Reason;
            extension.Status = ExtensionStatus.Rejected;

            extension.History.Add(new ExtensionHistory
            {
                Action = "rejected_by_checker",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = "Checker",
                Date = DateTime.UtcNow,
                Comment = request.Reason
            });

            await _context.SaveChangesAsync();

            return Ok(new { message = "Extension rejected by checker", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting as checker");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // ================================
    // GENERIC ROUTES
    // ================================

    // GET /api/extensions/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetExtensionById(Guid id)
    {
        try
        {
            var extension = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.RequestedBy)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.History).ThenInclude(h => h.User)
                .Include(e => e.Comments).ThenInclude(c => c.Author)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            return Ok(extension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting extension by id");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
