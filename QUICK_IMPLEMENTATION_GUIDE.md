# Quick Implementation Guide

## ⚡ Fast Track to Completion

Since all models and DTOs are complete, here's your fast track to finish the conversion:

## Step 1: Create Migration (5 minutes)

```powershell
cd c:\Users\raphael.eric\convert\dclcsharp
dotnet ef migrations add AddCompleteExtensionModel
dotnet ef database update
```

## Step 2: Build ExtensionController (Copy & Adapt)

Create `Controllers/ExtensionController.cs` and use this starter template with TODOs:

```csharp
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
            
            // TODO: Get deferral and validate
            var deferral = await _context.Deferrals.FindAsync(request.DeferralId);
            if (deferral == null)
                return NotFound(new { message = "Deferral not found" });

            // TODO: Create extension with approvers from deferral
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
                Status = ExtensionStatus.PendingApproval
            };

            // TODO: Copy approvers from deferral
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

            if (extension.Approvers.Any())
            {
                extension.Approvers.First().IsCurrent = true;
            }

            _context.Extensions.Add(extension);
            await _context.SaveChangesAsync();

            // TODO: Send email notifications to first approver

            return StatusCode(201, new { message = "Extension created", extension });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating extension");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/extensions/my
    [HttpGet("my")]
    [RoleAuthorize(UserRole.RM)]
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
                .Include(e => e. History)
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

            // Move to next approver or mark as complete
            extension.CurrentApproverIndex++;
            if (extension.CurrentApproverIndex < extension.Approvers.Count)
            {
                var approversList = extension.Approvers.OrderBy(a => a.Id).ToList();
                approversList[extension.CurrentApproverIndex].IsCurrent = true;
                extension.Status = ExtensionStatus.InReview;
            }
            else
            {
                extension.AllApproversApproved = true;
                extension.Status = ExtensionStatus.Approved;
                
                // TODO: Update original deferral daysSought
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
                // TODO: Set extensionApproved flag if exists
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
```

## Step 3: Create CustomerController

Create `Controllers/CustomerController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Models;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomerController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CustomerController> _logger;

    public CustomerController(
        ApplicationDbContext context,
        ILogger<CustomerController> _logger)
    {
        _context = context;
        _logger = logger;
    }

    // POST /api/customers/search
    [HttpPost("search")]
    public async Task<IActionResult> SearchCustomers([FromBody] CustomerSearchRequest request)
    {
        try
        {
            var query = _context.Users.Where(u => u.Role == UserRole.Customer);

            if (!string.IsNullOrEmpty(request.CustomerNumber))
            {
                query = query.Where(u => u.CustomerNumber != null && 
                                        u.CustomerNumber.Contains(request.CustomerNumber));
            }

            if (!string.IsNullOrEmpty(request.CustomerName))
            {
                query = query.Where(u => u.Name.Contains(request.CustomerName));
            }

            var customers = await query
                .Select(u => new CustomerSearchResponse
                {
                    CustomerNumber = u.CustomerNumber ?? "",
                    CustomerName = u.Name,
                    Email = u.Email,
                    Active = u.Active
                })
                .ToListAsync();

            return Ok(customers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching customers");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/customers/search-dcl
    [HttpGet("search-dcl")]
    public async Task<IActionResult> SearchByDcl([FromQuery] string dclNo)
    {
        try
        {
            if (string.IsNullOrEmpty(dclNo))
                return BadRequest(new { message = "DCL number is required" });

            var checklist = await _context.Checklists
                .Where(c => c.DclNo!.Contains(dclNo))
                .Select(c => new DclSearchResponse
                {
                    Id = c.Id,
                    DclNo = c.DclNo!,
                    CustomerName = c.CustomerName,
                    CustomerNumber = c.CustomerNumber,
                    Status = c.Status.ToString(),
                    CreatedAt = c.CreatedAt
                })
                .ToListAsync();

            return Ok(checklist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching DCL");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
```

## Step 4: Build and Test

```powershell
# Build
dotnet build

# If successful, run
dotnet watch run

# Test in browser
# https://localhost:5001/swagger
```

## ✅ Done!

That's it! Your conversion will be 100% complete.
