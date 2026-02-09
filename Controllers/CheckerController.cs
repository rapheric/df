using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Middleware;
using NCBA.DCL.Models;
using NCBA.DCL.Services;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/checkerChecklist")]
[Authorize]
public class CheckerController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CheckerController> _logger;

    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    public CheckerController(ApplicationDbContext context, ILogger<CheckerController> logger, IEmailService emailService, IAuditLogService auditLogService)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
        _auditLogService = auditLogService;
    }

    // GET /api/checkerChecklist/active-dcls
    // Aligns with Node.js: Returns DCLs in co_creator_review status
    [HttpGet("active-dcls")]
    public async Task<IActionResult> GetActiveDCLs()
    {
        try
        {
            _logger.LogInformation("🔍 Fetching active DCLs");

            var activeDcls = await _context.Checklists
                .Where(c => c.Status == ChecklistStatus.CoCreatorReview)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    _id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    status = c.Status.ToString(),
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name } : null,
                    assignedToRM = c.AssignedToRM != null ? new { id = c.AssignedToRM.Id, name = c.AssignedToRM.Name } : null,
                    createdAt = c.CreatedAt
                })
                .ToListAsync();

            _logger.LogInformation($"📊 Found {activeDcls.Count} active DCLs");
            return Ok(activeDcls);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Active DCLs Error");
            return StatusCode(500, new { error = ex.Message });
        }
    }



    // GET /api/checkerChecklist/my-queue/:checkerId
    // Aligns with Node.js: status "in_progress" assigned to checker
    [HttpGet("my-queue/{checkerId}")]
    public async Task<IActionResult> GetMyQueue(Guid checkerId)
    {
        try
        {
            _logger.LogInformation($"🔍 Fetching my queue for checker: {checkerId}");

            var myQueue = await _context.Checklists
                .Where(c => c.AssignedToCoCheckerId == checkerId &&
                           c.Status == ChecklistStatus.CoCheckerReview)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                        .ThenInclude(d => d.CoCreatorFiles)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    _id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    status = c.Status.ToString(),
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name } : null,
                    assignedToRM = c.AssignedToRM != null ? new { id = c.AssignedToRM.Id, name = c.AssignedToRM.Name } : null,
                    assignedToCoChecker = c.AssignedToCoChecker != null ? new { id = c.AssignedToCoChecker.Id, name = c.AssignedToCoChecker.Name } : null,
                    documents = c.Documents,
                    supportingDocs = c.SupportingDocs.Select(sd => new
                    {
                        id = sd.Id,
                        _id = sd.Id,
                        fileName = sd.FileName,
                        fileUrl = sd.FileUrl,
                        fileSize = sd.FileSize,
                        fileType = sd.FileType,
                        uploadedBy = sd.UploadedBy != null ? sd.UploadedBy.Name : "Unknown",
                        uploadedAt = sd.UploadedAt
                    }).ToList(),
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt
                })
                .ToListAsync();

            _logger.LogInformation($"📊 Found {myQueue.Count} items in queue");
            return Ok(myQueue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching checker queue");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // GET /api/checkerChecklist/completed/:checkerId
    // Aligns with Node.js: Returns approved DCLs for this checker
    [HttpGet("completed/{checkerId}")]
    public async Task<IActionResult> GetCompletedDCLs(Guid checkerId)
    {
        try
        {
            _logger.LogInformation($"🔍 Fetching completed DCLs for checker: {checkerId}");

            var completed = await _context.Checklists
                .Where(c => c.AssignedToCoCheckerId == checkerId &&
                           c.Status == ChecklistStatus.Approved)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    _id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    status = c.Status.ToString(),
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name } : null,
                    assignedToRM = c.AssignedToRM != null ? new { id = c.AssignedToRM.Id, name = c.AssignedToRM.Name } : null,
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt
                })
                .ToListAsync();

            _logger.LogInformation($"📊 Found {completed.Count} completed DCLs");
            return Ok(completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Completed DCLs Error");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // GET /api/checkerChecklist/dcl/:id
    // Aligns with Node.js: Returns single DCL with all details
    [HttpGet("dcl/{id}")]
    public async Task<IActionResult> GetDCLById(Guid id)
    {
        try
        {
            _logger.LogInformation($"🔍 Fetching DCL by ID: {id}");

            var dcl = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                        .ThenInclude(d => d.CoCreatorFiles)
                .Include(c => c.SupportingDocs)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (dcl == null)
            {
                _logger.LogWarning($"❌ DCL not found: {id}");
                return NotFound(new { error = "DCL not found" });
            }

            _logger.LogInformation($"✅ DCL found: {dcl.DclNo}");
            return Ok(dcl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Get DCL Error");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // PUT /api/checkerChecklist/dcl/:id
    [HttpPut("dcl/{id}")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> UpdateDCLStatus(Guid id, [FromBody] UpdateCheckerDCLRequest request)
    {
        try
        {
            var dcl = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (dcl == null)
            {
                return NotFound(new { message = "DCL not found" });
            }

            // Document-level status/comments update (if provided)
            if (request.DocumentUpdates != null && request.DocumentUpdates.Count > 0)
            {
                foreach (var docUpdate in request.DocumentUpdates)
                {
                    // Use DocumentId helper if available, fallback to Id or _id
                    var docId = docUpdate.DocumentId ?? docUpdate.Id ?? docUpdate._id;
                    if (!docId.HasValue) continue;

                    foreach (var category in dcl.Documents)
                    {
                        var doc = category.DocList.FirstOrDefault(x => x.Id == docId.Value);
                        if (doc != null)
                        {
                            if (docUpdate.Status.HasValue) doc.CheckerStatus = docUpdate.Status.Value;
                            if (!string.IsNullOrEmpty(docUpdate.CheckerComment)) doc.CheckerComment = docUpdate.CheckerComment;
                        }
                    }
                }
            }
            else if (request.Status == ChecklistStatus.Approved || request.Status == ChecklistStatus.Rejected)
            {
                // Bulk update all documents if no specific updates
                foreach (var category in dcl.Documents)
                {
                    foreach (var doc in category.DocList)
                    {
                        doc.CheckerStatus = request.Status == ChecklistStatus.Approved ? CheckerStatus.Approved : CheckerStatus.Rejected;
                    }
                }
            }

            dcl.Status = request.Status;

            // Add log entry
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = $"Status updated to {request.Status} by checker",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // Audit log
            await _auditLogService.CreateLogAsync(new DTOs.AuditLogCreateDto
            {
                Action = $"CHECKER_{request.Status.ToString().ToUpper()}",
                Resource = "CHECKLIST",
                PerformedById = userId,
                TargetUserId = dcl.CreatedById,
                Status = "success",
                Details = $"DCL status updated to {request.Status} by checker."
            });

            // Email notifications
            if (dcl.CreatedBy != null && !string.IsNullOrEmpty(dcl.CreatedBy.Email))
            {
                await _emailService.SendCheckerStatusChangedAsync(dcl.CreatedBy.Email, dcl.CreatedBy.Name, dcl.DclNo, request.Status.ToString());
            }
            if (dcl.AssignedToRM != null && !string.IsNullOrEmpty(dcl.AssignedToRM.Email))
            {
                await _emailService.SendCheckerStatusChangedAsync(dcl.AssignedToRM.Email, dcl.AssignedToRM.Name, dcl.DclNo, request.Status.ToString());
            }

            // In-app notifications
            var notifications = new List<Notification>();
            if (dcl.CreatedById.HasValue)
            {
                notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = dcl.CreatedById.Value,
                    Message = $"DCL {dcl.DclNo} was {request.Status}",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            if (dcl.AssignedToRMId.HasValue)
            {
                notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = dcl.AssignedToRMId.Value,
                    Message = $"DCL {dcl.DclNo} was {request.Status}",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            if (notifications.Count > 0)
            {
                _context.Notifications.AddRange(notifications);
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "DCL status updated successfully", status = dcl.Status.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DCL status");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/checkerChecklist/my-queue-auto/:checkerId
    [HttpGet("my-queue-auto/{checkerId}")]
    public async Task<IActionResult> GetAutoMovedQueue(Guid checkerId)
    {
        try
        {
            // Auto-move approved items from queue to completed
            var approvedItems = await _context.Checklists
                .Where(c => c.AssignedToCoCheckerId == checkerId &&
                           c.Status == ChecklistStatus.Approved)
                .ToListAsync();

            var myQueue = await _context.Checklists
                .Where(c => c.AssignedToCoCheckerId == checkerId &&
                           c.Status == ChecklistStatus.CoCheckerReview)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            return Ok(new
            {
                queue = myQueue,
                movedToCompleted = approvedItems.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching auto-moved queue");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PATCH /api/checkerChecklist/update-status
    // Aligns with Node.js: Handles bulk updates with checkerDecisions array
    [HttpPatch("update-status")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateCheckerStatusRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            _logger.LogInformation($"RTK Query: Sending payload to updateCheckerStatus: {System.Text.Json.JsonSerializer.Serialize(request)}");

            if (!request.Id.HasValue)
            {
                return BadRequest(new { error = "Checklist ID is required" });
            }

            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(cat => cat.DocList)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .FirstOrDefaultAsync(c => c.Id == request.Id.Value);

            if (checklist == null)
            {
                return NotFound(new { error = "Checklist not found" });
            }

            // Determine new status from action
            ChecklistStatus newStatus;
            if (request.Action?.ToLower() == "approved") newStatus = ChecklistStatus.Approved;
            else if (request.Action?.ToLower() == "rejected") newStatus = ChecklistStatus.Rejected;
            else if (request.Action?.ToLower() == "co_creator_review") newStatus = ChecklistStatus.CoCreatorReview;
            else return BadRequest(new { error = $"Invalid action: {request.Action}" });

            // Create a map of document updates for easy lookup
            var updatesMap = new Dictionary<string, (CheckerStatus? Status, string? Comment)>();
            if (request.CheckerDecisions?.Any() == true)
            {
                foreach (var decision in request.CheckerDecisions)
                {
                    if (decision.DocumentId.HasValue)
                    {
                        if (Enum.TryParse<CheckerStatus>(decision.CheckerStatus, ignoreCase: true, out var checkerStatus))
                        {
                            updatesMap[decision.DocumentId.Value.ToString()] = (checkerStatus, decision.CheckerComment);
                        }
                    }
                }
            }

            // Update documents
            foreach (var category in checklist.Documents)
            {
                foreach (var doc in category.DocList)
                {
                    var docIdStr = doc.Id.ToString();
                    CheckerStatus finalCheckerStatus;

                    if (updatesMap.ContainsKey(docIdStr))
                    {
                        // Use the update from frontend
                        finalCheckerStatus = updatesMap[docIdStr].Status ?? doc.CheckerStatus;
                        if (!string.IsNullOrWhiteSpace(updatesMap[docIdStr].Comment))
                        {
                            doc.CheckerComment = updatesMap[docIdStr].Comment;
                        }
                    }
                    else if (newStatus == ChecklistStatus.Approved)
                    {
                        // If approving entire checklist, all become approved
                        finalCheckerStatus = CheckerStatus.Approved;
                    }
                    else if (newStatus == ChecklistStatus.Rejected)
                    {
                        // If rejecting entire checklist, all become rejected
                        finalCheckerStatus = CheckerStatus.Rejected;
                    }
                    else
                    {
                        // Keep existing status if returning to co_creator_review
                        finalCheckerStatus = doc.CheckerStatus;
                    }

                    doc.CheckerStatus = finalCheckerStatus;
                    doc.UpdatedAt = DateTime.UtcNow; // Ensure document update timestamp is set
                }
            }

            // Update overall checklist status
            checklist.Status = newStatus;
            checklist.LastUpdatedBy = userId;
            checklist.UpdatedAt = DateTime.UtcNow;

            // Log Checker's general comment if provided
            if (!string.IsNullOrWhiteSpace(request.CheckerComment))
            {
                var commentLog = new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = request.CheckerComment,
                    UserId = userId,
                    ChecklistId = checklist.Id,
                    Timestamp = DateTime.UtcNow
                };
                _context.ChecklistLogs.Add(commentLog);
            }

            // Log Audit
            await _auditLogService.CreateLogAsync(new DTOs.AuditLogCreateDto
            {
                Action = $"CHECKER_{request.Action?.ToUpper()}",
                Resource = "CHECKLIST",
                PerformedById = userId,
                TargetUserId = checklist.CreatedById,
                Status = "success",
                Details = $"DCL {checklist.DclNo} updated to {newStatus}"
            });

            // Email notifications
            try
            {
                var checker = await _context.Users.FindAsync(userId);
                var checkerName = checker?.Name ?? "Checker";

                var coCreator = await _context.Users.FindAsync(checklist.CreatedById);
                var rmUser = checklist.AssignedToRMId.HasValue
                    ? await _context.Users.FindAsync(checklist.AssignedToRMId.Value)
                    : null;

                if (newStatus == ChecklistStatus.Approved)
                {
                    if (coCreator?.Email != null)
                        await _emailService.SendCheckerApprovedAsync(
                            coCreator.Email, coCreator.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);

                    if (rmUser?.Email != null)
                        await _emailService.SendCheckerApprovedAsync(
                            rmUser.Email, rmUser.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);
                }

                if (newStatus == ChecklistStatus.CoCreatorReview || newStatus == ChecklistStatus.Rejected)
                {
                    if (coCreator?.Email != null)
                        await _emailService.SendCheckerReturnedAsync(
                            coCreator.Email, coCreator.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);

                    if (rmUser?.Email != null)
                        await _emailService.SendCheckerReturnedAsync(
                            rmUser.Email, rmUser.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);
                }
            }
            catch (Exception emailError)
            {
                _logger.LogError(emailError, "❌ EMAIL FAILURE (non-blocking)");
            }

            // In-app notifications
            var notifications = new List<Notification>();

            if (checklist.CreatedById.HasValue)
            {
                notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = checklist.CreatedById.Value,
                    Message = $"DCL {checklist.DclNo ?? checklist.Id.ToString()} was {newStatus}.",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            if (checklist.AssignedToRMId.HasValue)
            {
                notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = checklist.AssignedToRMId.Value,
                    Message = $"DCL {checklist.DclNo ?? checklist.Id.ToString()} was {newStatus}.",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            if (notifications.Count > 0)
            {
                _context.Notifications.AddRange(notifications);
            }

            await _context.SaveChangesAsync();

            // Reload the checklist with all includes to return updated data
            var updatedChecklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                .FirstOrDefaultAsync(c => c.Id == checklist.Id);

            return Ok(new
            {
                message = $"Checklist successfully updated to {newStatus}",
                checklistId = checklist.Id,
                dclNo = checklist.DclNo,
                status = newStatus.ToString(),
                updatedAt = DateTime.UtcNow,
                checklist = updatedChecklist // Include full checklist for verification
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checker status update error");
            return StatusCode(500, new { error = "Failed to process checker action", details = ex.Message });
        }
    }

    // GET /api/checkerChecklist/reports/:checkerId
    // Aligns with Node.js: Returns counts for dashboard
    [HttpGet("reports/{checkerId}")]
    public async Task<IActionResult> GetReports(Guid checkerId)
    {
        try
        {
            _logger.LogInformation($"🔍 Fetching reports for checker: {checkerId}");

            var myQueueCount = await _context.Checklists.CountAsync(c =>
                c.AssignedToCoCheckerId == checkerId &&
                c.Status == ChecklistStatus.CoCheckerReview);

            var activeDclsCount = await _context.Checklists.CountAsync(c =>
                c.Status == ChecklistStatus.CoCreatorReview);

            var completedCount = await _context.Checklists.CountAsync(c =>
                c.AssignedToCoCheckerId == checkerId &&
                c.Status == ChecklistStatus.Approved);

            _logger.LogInformation($"📊 Reports - Queue: {myQueueCount}, Active: {activeDclsCount}, Completed: {completedCount}");

            return Ok(new
            {
                myQueue = myQueueCount,
                activeDcls = activeDclsCount,
                completed = completedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Reports API Error");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // PATCH /api/checkerChecklist/approve/:id
    [HttpPatch("approve/{id}")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> ApproveDCL(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var dcl = await _context.Checklists
                .Include(c => c.CreatedBy)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (dcl == null)
            {
                return NotFound(new { message = "DCL not found" });
            }

            dcl.Status = ChecklistStatus.Approved;

            // Add log
            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "DCL approved by checker",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // Create notification for creator
            if (dcl.CreatedById.HasValue)
            {
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = dcl.CreatedById.Value,
                    Message = $"Your DCL {dcl.DclNo} has been approved",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Notifications.Add(notification);
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "DCL approved successfully", status = dcl.Status.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving DCL");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PATCH /api/checkerChecklist/reject/:id
    [HttpPatch("reject/{id}")]
    [RoleAuthorize(UserRole.CoChecker)]
    public async Task<IActionResult> RejectDCL(Guid id, [FromBody] RejectDCLRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var dcl = await _context.Checklists
                .Include(c => c.CreatedBy)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (dcl == null)
            {
                return NotFound(new { message = "DCL not found" });
            }

            dcl.Status = ChecklistStatus.Rejected;

            // Add log
            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = $"DCL rejected by checker: {request.Reason}",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // Create notification for creator
            if (dcl.CreatedById.HasValue)
            {
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = dcl.CreatedById.Value,
                    Message = $"Your DCL {dcl.DclNo} has been rejected: {request.Reason}",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Notifications.Add(notification);
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "DCL rejected successfully", status = dcl.Status.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting DCL");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
