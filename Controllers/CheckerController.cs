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

    private async Task<bool> TrySendChecklistEmailAsync(Func<Task<bool>> sendOperation, string failureMessage, params object[] args)
    {
        try
        {
            var sent = await sendOperation();
            if (!sent)
            {
                _logger.LogWarning(failureMessage, args);
            }

            return sent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage, args);
            return false;
        }
    }

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

            // DEBUG: Check all checklists in the system
            var allChecklists = await _context.Checklists.CountAsync();
            var coCheckerReviewCount = await _context.Checklists
                .Where(c => c.Status == ChecklistStatus.CoCheckerReview)
                .CountAsync();
            _logger.LogInformation($"📊 DEBUG: Total checklists: {allChecklists}, CoCheckerReview status: {coCheckerReviewCount}");

            // DEBUG: Check checklists assigned to this specific checker
            var assignedToThisChecker = await _context.Checklists
                .Where(c => c.AssignedToCoCheckerId == checkerId)
                .CountAsync();
            _logger.LogInformation($"📊 DEBUG: Checklists assigned to checker {checkerId}: {assignedToThisChecker}");

            var myQueue = await _context.Checklists
                .Where(c => c.AssignedToCoCheckerId == checkerId &&
                           c.Status == ChecklistStatus.CoCheckerReview)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.LockedByUser)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                        .ThenInclude(d => d.CoCreatorFiles)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .OrderByDescending(c => c.UpdatedAt)
                .ToListAsync();

            var uploadLookup = await GetLatestDocumentUploadsLookupAsync(myQueue.Select(c => c.Id));

            var response = myQueue.Select(c => new
                {
                    id = c.Id,
                    _id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    status = c.Status.ToString(),
                    lockedByUserId = c.LockedByUserId,
                    lockedByUserName = c.LockedByUser != null ? c.LockedByUser.Name : null,
                    lockedBy = c.LockedByUser != null ? new { id = c.LockedByUser.Id, name = c.LockedByUser.Name } : null,
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name } : null,
                    assignedToRM = c.AssignedToRM != null ? new { id = c.AssignedToRM.Id, name = c.AssignedToRM.Name } : null,
                    assignedToCoChecker = c.AssignedToCoChecker != null ? new { id = c.AssignedToCoChecker.Id, name = c.AssignedToCoChecker.Name } : null,
                    documents = c.Documents.Select(dc => new
                    {
                        id = dc.Id,
                        category = dc.Category,
                        docList = dc.DocList.Select(d =>
                        {
                            var upload = GetLatestDocumentUpload(uploadLookup, c.Id, d.Id);
                            return new
                            {
                                id = d.Id,
                                name = d.Name,
                                status = d.Status.ToString().ToLower(),
                                    creatorStatus = d.CreatorStatus.HasValue ? d.CreatorStatus.Value.ToString().ToLowerInvariant() : null,
                                checkerStatus = d.CheckerStatus.ToString().ToLower(),
                                rmStatus = d.RmStatus.ToString().ToLower(),
                                fileUrl = d.FileUrl,
                                comment = d.Comment,
                                deferralNumber = d.DeferralNumber,
                                deferralNo = d.DeferralNumber,
                                uploadedAt = BuildDocumentUploadedAt(d, upload, c.AssignedToRM),
                                uploadedBy = BuildDocumentUploader(upload, d, c.AssignedToRM),
                                uploadedByRole = BuildDocumentUploaderRole(upload, d, c.AssignedToRM)
                            };
                        }).ToList()
                    }).ToList(),
                    supportingDocs = c.SupportingDocs.Select(sd => new
                    {
                        id = sd.Id,
                        _id = sd.Id,
                        fileName = sd.FileName,
                        fileUrl = sd.FileUrl,
                        fileSize = sd.FileSize,
                        fileType = sd.FileType,
                        uploadedBy = sd.UploadedBy != null ? sd.UploadedBy.Name : "Unknown",
                        uploadedByRole = sd.UploadedByRole,
                        uploadedAt = sd.UploadedAt
                    }).ToList(),
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt
                })
                .ToList();

            _logger.LogInformation($"📊 Found {response.Count} items in queue for checker {checkerId}");
            return Ok(response);
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
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
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
                    documents = c.Documents.Select(dc => new
                    {
                        id = dc.Id,
                        category = dc.Category,
                        docList = dc.DocList.Select(d => new
                        {
                            id = d.Id,
                            name = d.Name,
                            status = d.Status.ToString().ToLower(),
                            creatorStatus = d.CreatorStatus.HasValue ? d.CreatorStatus.Value.ToString().ToLowerInvariant() : null,
                            checkerStatus = d.CheckerStatus.ToString().ToLower(),
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            fileUrl = d.FileUrl,
                            comment = d.Comment,
                            deferralNumber = d.DeferralNumber,
                            deferralNo = d.DeferralNumber,
                            expiryDate = d.ExpiryDate
                        }).ToList()
                    }).ToList(),
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
                .Include(c => c.LockedByUser)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                        .ThenInclude(d => d.CoCreatorFiles)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (dcl == null)
            {
                _logger.LogWarning($"❌ DCL not found: {id}");
                return NotFound(new { error = "DCL not found" });
            }

            _logger.LogInformation($"✅ DCL found: {dcl.DclNo}");

            // Fetch supporting documents from both Uploads and SupportingDocs tables
            var supportingDocs = await CombineSupportingDocsWithUploadsAsync(id, dcl);
            var uploadLookup = await GetLatestDocumentUploadsLookupAsync(new[] { id });

            // ✅ Properly map to DTO to avoid serialization errors
            var response = new
            {
                id = dcl.Id,
                _id = dcl.Id,
                dclNo = dcl.DclNo,
                customerNumber = dcl.CustomerNumber,
                customerName = dcl.CustomerName,
                loanType = dcl.LoanType,
                ibpsNo = dcl.IbpsNo,
                status = dcl.Status.ToString(),
                lockedByUserId = dcl.LockedByUserId,
                lockedByUserName = dcl.LockedByUser != null ? dcl.LockedByUser.Name : null,
                lockedBy = dcl.LockedByUser != null ? new { id = dcl.LockedByUser.Id, name = dcl.LockedByUser.Name } : null,
                createdBy = dcl.CreatedBy != null ? new { id = dcl.CreatedBy.Id, name = dcl.CreatedBy.Name } : null,
                assignedToRM = dcl.AssignedToRM != null ? new { id = dcl.AssignedToRM.Id, name = dcl.AssignedToRM.Name } : null,
                assignedToCoChecker = dcl.AssignedToCoChecker != null ? new { id = dcl.AssignedToCoChecker.Id, name = dcl.AssignedToCoChecker.Name } : null,
                documents = dcl.Documents.Select(dc => new
                {
                    id = dc.Id,
                    category = dc.Category,
                    docList = dc.DocList.Select(d =>
                    {
                        var upload = GetLatestDocumentUpload(uploadLookup, id, d.Id);
                        return new
                        {
                            id = d.Id,
                            name = d.Name,
                            status = d.Status.ToString().ToLower(),
                            creatorStatus = d.CreatorStatus?.ToString().ToLower(),
                            checkerStatus = d.CheckerStatus.ToString().ToLower(),
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            fileUrl = d.FileUrl,
                            comment = d.Comment,
                            checkerComment = d.CheckerComment,
                            deferralNumber = d.DeferralNumber,
                            deferralNo = d.DeferralNumber,
                            uploadedAt = BuildDocumentUploadedAt(d, upload, dcl.AssignedToRM),
                            uploadedBy = BuildDocumentUploader(upload, d, dcl.AssignedToRM),
                            uploadedByRole = BuildDocumentUploaderRole(upload, d, dcl.AssignedToRM),
                            coCreatorFiles = d.CoCreatorFiles.Select(cf => new
                            {
                                id = cf.Id,
                                name = cf.Name,
                                url = cf.Url
                            }).ToList()
                        };
                    }).ToList()
                }).ToList(),
                supportingDocs = supportingDocs,  // Use combined results from both tables
                logs = dcl.Logs.Select(l => new
                {
                    id = l.Id,
                    message = l.Message,
                    user = l.User != null ? new { id = l.User.Id, name = l.User.Name } : null,
                    timestamp = l.Timestamp
                }).ToList(),
                createdAt = dcl.CreatedAt,
                updatedAt = dcl.UpdatedAt
            };

            return Ok(response);
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

                            // Explicitly mark the entity as modified to ensure EF Core tracks the change
                            _context.Entry(doc).State = EntityState.Modified;
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

                        // Explicitly mark the entity as modified to ensure EF Core tracks the change
                        _context.Entry(doc).State = EntityState.Modified;
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
                await TrySendChecklistEmailAsync(
                    () => _emailService.SendCheckerStatusChangedAsync(
                        dcl.CreatedBy.Email,
                        dcl.CreatedBy.Name,
                        dcl.DclNo,
                        request.Status.ToString()),
                    "⚠️ Checker status update email to creator reported no delivery for {DclNo}",
                    dcl.DclNo);
            }
            if (dcl.AssignedToRM != null && !string.IsNullOrEmpty(dcl.AssignedToRM.Email))
            {
                await TrySendChecklistEmailAsync(
                    () => _emailService.SendCheckerStatusChangedAsync(
                        dcl.AssignedToRM.Email,
                        dcl.AssignedToRM.Name,
                        dcl.DclNo,
                        request.Status.ToString()),
                    "⚠️ Checker status update email to RM reported no delivery for {DclNo}",
                    dcl.DclNo);
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

            _logger.LogInformation($"🔴 CHECKER RETURNING TO CO-CREATOR");
            _logger.LogInformation($"   Action: {request.Action}");
            _logger.LogInformation($"   CheckerDecisions count: {request.CheckerDecisions?.Count}");

            if (request.CheckerDecisions != null)
            {
                foreach (var decision in request.CheckerDecisions)
                {
                    _logger.LogInformation($"      DocumentId: {decision.DocumentId}");
                    _logger.LogInformation($"      CheckerStatus (incoming): {decision.CheckerStatus}");
                }
            }

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
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == request.Id.Value);

            if (checklist == null)
            {
                return NotFound(new { error = "Checklist not found" });
            }

            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "submit checker decisions");
            if (lockConflict != null)
            {
                return lockConflict;
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

                    _logger.LogInformation($"🔧 Processing doc: {doc.Name} (ID: {docIdStr})");
                    _logger.LogInformation($"   Old CheckerStatus: {doc.CheckerStatus}");
                    _logger.LogInformation($"   Old CreatorStatus: {doc.CreatorStatus}");

                    CheckerStatus finalCheckerStatus = doc.CheckerStatus;

                    if (updatesMap.ContainsKey(docIdStr))
                    {
                        // Use the update from frontend - checker explicitly set this document's status
                        finalCheckerStatus = updatesMap[docIdStr].Status ?? doc.CheckerStatus;
                        _logger.LogInformation($"   ✅ New CheckerStatus from decision: {finalCheckerStatus}");
                        if (!string.IsNullOrWhiteSpace(updatesMap[docIdStr].Comment))
                        {
                            doc.CheckerComment = updatesMap[docIdStr].Comment;
                        }
                    }
                    else if (newStatus == ChecklistStatus.Approved)
                    {
                        // If approving entire checklist, all become approved
                        finalCheckerStatus = CheckerStatus.Approved;
                        _logger.LogInformation($"   ✅ Set to Approved (whole checklist approved)");
                    }
                    else if (newStatus == ChecklistStatus.Rejected)
                    {
                        // If rejecting entire checklist, all become rejected
                        finalCheckerStatus = CheckerStatus.Rejected;
                        _logger.LogInformation($"   ✅ Set to Rejected (whole checklist rejected)");
                    }
                    else if (newStatus == ChecklistStatus.CoCreatorReview)
                    {
                        // When returning to CoCreator for review/changes
                        // If no specific decision was made for this document, keep existing CheckerStatus
                        // (which should have been set in a previous review cycle)
                        _logger.LogInformation($"   ℹ️ Returning for review - preserving CheckerStatus: {finalCheckerStatus}");
                    }
                    else
                    {
                        // For any other status, preserve the existing checker status
                        _logger.LogInformation($"   ℹ️ Preserving CheckerStatus: {finalCheckerStatus}");
                    }

                    doc.CheckerStatus = finalCheckerStatus;
                    doc.UpdatedAt = DateTime.UtcNow; // Ensure document update timestamp is set

                    // Explicitly mark the entity as modified to ensure EF Core tracks the change
                    _context.Entry(doc).State = EntityState.Modified;
                }
            }

            // Update overall checklist status
            checklist.Status = newStatus;
            checklist.LockedByUserId = null;
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
                    {
                        var emailSent = await _emailService.SendCheckerApprovedAsync(
                            coCreator.Email, coCreator.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);

                        if (!emailSent)
                        {
                            _logger.LogWarning("⚠️ Checker approval email to Co-Creator reported no delivery for {DclNo}", checklist.DclNo);
                        }
                    }

                    if (rmUser?.Email != null)
                    {
                        var emailSent = await _emailService.SendCheckerApprovedAsync(
                            rmUser.Email, rmUser.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);

                        if (!emailSent)
                        {
                            _logger.LogWarning("⚠️ Checker approval email to RM reported no delivery for {DclNo}", checklist.DclNo);
                        }
                    }
                }

                if (newStatus == ChecklistStatus.CoCreatorReview || newStatus == ChecklistStatus.Rejected)
                {
                    if (coCreator?.Email != null)
                    {
                        var emailSent = await _emailService.SendCheckerReturnedAsync(
                            coCreator.Email, coCreator.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);

                        if (!emailSent)
                        {
                            _logger.LogWarning("⚠️ Checker return email to Co-Creator reported no delivery for {DclNo}", checklist.DclNo);
                        }
                    }

                    if (rmUser?.Email != null)
                    {
                        var emailSent = await _emailService.SendCheckerReturnedAsync(
                            rmUser.Email, rmUser.Name,
                            checklist.Id.ToString(), checklist.DclNo, checkerName);

                        if (!emailSent)
                        {
                            _logger.LogWarning("⚠️ Checker return email to RM reported no delivery for {DclNo}", checklist.DclNo);
                        }
                    }
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

            _logger.LogInformation($"💾 About to SaveChangesAsync... Final document states:");
            foreach (var category in checklist.Documents)
            {
                foreach (var doc in category.DocList)
                {
                    _logger.LogInformation($"   📋 {doc.Name}: CheckerStatus={doc.CheckerStatus}, CreatorStatus={doc.CreatorStatus}");
                }
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation($"✅ SaveChangesAsync completed - Reloading checklist...");

            // Reload the checklist with all includes to return updated data
            var updatedChecklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                .FirstOrDefaultAsync(c => c.Id == checklist.Id);

            // ✅ Properly map the response to avoid circular references
            var response = new
            {
                message = $"Checklist successfully updated to {newStatus}",
                checklistId = checklist.Id,
                dclNo = checklist.DclNo,
                status = newStatus.ToString(),
                updatedAt = DateTime.UtcNow,
                checklist = updatedChecklist != null ? new
                {
                    id = updatedChecklist.Id,
                    dclNo = updatedChecklist.DclNo,
                    status = updatedChecklist.Status.ToString(),
                    generalComment = updatedChecklist.GeneralComment,
                    finalComment = updatedChecklist.FinalComment,
                    documents = updatedChecklist.Documents.Select(dc => new
                    {
                        id = dc.Id,
                        category = dc.Category,
                        docList = dc.DocList.Select(d => new
                        {
                            id = d.Id,
                            name = d.Name,
                            status = d.Status.ToString().ToLower(),
                            creatorStatus = d.CreatorStatus?.ToString().ToLower(),
                            checkerStatus = d.CheckerStatus.ToString().ToLower(),
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            checkerComment = d.CheckerComment,
                            deferralNumber = d.DeferralNumber,
                            deferralNo = d.DeferralNumber
                        }).ToList()
                    }).ToList()
                } : null
            };

            return Ok(response);
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

            if (!string.IsNullOrWhiteSpace(dcl.CreatedBy?.Email))
            {
                var checkerName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "Checker";
                await TrySendChecklistEmailAsync(
                    () => _emailService.SendCheckerApprovedAsync(
                        dcl.CreatedBy.Email,
                        dcl.CreatedBy.Name,
                        dcl.Id.ToString(),
                        dcl.DclNo,
                        checkerName),
                    "⚠️ Checker approval email to creator reported no delivery for {DclNo}",
                    dcl.DclNo);
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

            if (!string.IsNullOrWhiteSpace(dcl.CreatedBy?.Email))
            {
                var checkerName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "Checker";
                await TrySendChecklistEmailAsync(
                    () => _emailService.SendCheckerReturnedAsync(
                        dcl.CreatedBy.Email,
                        dcl.CreatedBy.Name,
                        dcl.Id.ToString(),
                        dcl.DclNo,
                        checkerName),
                    "⚠️ Checker rejection email to creator reported no delivery for {DclNo}",
                    dcl.DclNo);
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

    // Helper method to combine supporting docs from both legacy SupportingDocs table and Uploads table
    private async Task<List<object>> CombineSupportingDocsWithUploadsAsync(Guid checklistId, Checklist checklist)
    {
        var combinedDocs = new List<object>();

        // Fetch supporting documents from Uploads table
        var uploads = await _context.Uploads
            .Where(u => u.ChecklistId == checklistId && u.Category == "Supporting Documents")
            .ToListAsync();

        _logger.LogInformation($"📎 Checker Controller - Found {uploads.Count} supporting documents from Uploads table for checklist {checklistId}");

        // Add legacy SupportingDocs
        combinedDocs.AddRange(checklist.SupportingDocs.Select(sd => new
        {
            id = sd.Id,
            _id = sd.Id,
            name = sd.FileName,
            fileName = sd.FileName,
            fileUrl = sd.FileUrl,
            fileSize = sd.FileSize,
            fileType = sd.FileType,
            category = "Supporting Documents",
            uploadedBy = sd.UploadedBy != null ? new { id = sd.UploadedBy.Id, name = sd.UploadedBy.Name } : null,
            uploadedByRole = sd.UploadedByRole,
            uploadedAt = sd.UploadedAt,
            uploadData = new
            {
                fileName = sd.FileName,
                fileUrl = sd.FileUrl,
                fileSize = sd.FileSize,
                fileType = sd.FileType,
                uploadedBy = sd.UploadedBy?.Name ?? "Unknown",
                uploadedByRole = sd.UploadedByRole,
                createdAt = sd.UploadedAt
            }
        }));

        // Add uploads from Uploads table
        combinedDocs.AddRange(uploads.Select(u => new
        {
            id = u.Id,
            _id = u.Id,
            name = u.DocumentName ?? u.FileName,
            fileName = u.FileName,
            fileUrl = u.FileUrl,
            fileSize = u.FileSize,
            fileType = u.FileType,
            category = "Supporting Documents",
            uploadedBy = new { id = (Guid?)null, name = u.UploadedBy ?? "RM" },
            uploadedByRole = u.UploadedByRole,
            uploadedAt = u.CreatedAt,
            uploadData = new
            {
                fileName = u.FileName,
                fileUrl = u.FileUrl,
                fileSize = u.FileSize,
                fileType = u.FileType,
                uploadedBy = u.UploadedBy ?? "RM",
                uploadedByRole = u.UploadedByRole,
                createdAt = u.CreatedAt
            }
        }));

        _logger.LogInformation($"📎 Checker Controller - Total supporting docs to return: {combinedDocs.Count}");

        return combinedDocs;
    }

    private async Task<Dictionary<Guid, Dictionary<Guid, Upload>>> GetLatestDocumentUploadsLookupAsync(IEnumerable<Guid> checklistIds)
    {
        var checklistIdList = checklistIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (checklistIdList.Count == 0)
        {
            return new Dictionary<Guid, Dictionary<Guid, Upload>>();
        }

        var uploads = await _context.Uploads
            .Where(u =>
                u.ChecklistId.HasValue &&
                checklistIdList.Contains(u.ChecklistId.Value) &&
                u.DocumentId.HasValue &&
                u.Category != "Supporting Documents" &&
                (u.Status == null || u.Status != "deleted"))
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();

        return uploads
            .GroupBy(u => u.ChecklistId!.Value)
            .ToDictionary(
                checklistGroup => checklistGroup.Key,
                checklistGroup => checklistGroup
                    .GroupBy(u => u.DocumentId!.Value)
                    .ToDictionary(documentGroup => documentGroup.Key, documentGroup => documentGroup.First()));
    }

    private static Upload? GetLatestDocumentUpload(
        IReadOnlyDictionary<Guid, Dictionary<Guid, Upload>> uploadLookup,
        Guid checklistId,
        Guid documentId)
    {
        if (uploadLookup.TryGetValue(checklistId, out var checklistUploads) &&
            checklistUploads.TryGetValue(documentId, out var upload))
        {
            return upload;
        }

        return null;
    }

    private static object? BuildDocumentUploader(Upload? upload, Document document, User? assignedToRm)
    {
        if (!string.IsNullOrWhiteSpace(upload?.UploadedBy))
        {
            return new { id = (Guid?)null, name = upload.UploadedBy };
        }

        if (ShouldUseRmUploadFallback(document, assignedToRm))
        {
            return new { id = assignedToRm!.Id, name = assignedToRm.Name };
        }

        return null;
    }

    private static string? BuildDocumentUploaderRole(Upload? upload, Document document, User? assignedToRm)
    {
        if (!string.IsNullOrWhiteSpace(upload?.UploadedByRole))
        {
            return upload.UploadedByRole;
        }

        if (ShouldUseRmUploadFallback(document, assignedToRm))
        {
            return "RM";
        }

        return null;
    }

    private static DateTime? BuildDocumentUploadedAt(Document document, Upload? upload, User? assignedToRm)
    {
        if (upload != null)
        {
            return upload.CreatedAt;
        }

        if (ShouldUseRmUploadFallback(document, assignedToRm))
        {
            return document.UpdatedAt > document.CreatedAt ? document.UpdatedAt : document.CreatedAt;
        }

        return null;
    }

    private static bool ShouldUseRmUploadFallback(Document document, User? assignedToRm)
    {
        return assignedToRm != null &&
               !string.IsNullOrWhiteSpace(document.FileUrl) &&
               document.UpdatedAt > document.CreatedAt;
    }

    private async Task<IActionResult?> EnsureChecklistEditableByCurrentUserAsync(Checklist checklist, Guid userId, string actionDescription)
    {
        if (checklist.LockedByUserId.HasValue && checklist.LockedByUserId.Value != userId)
        {
            if (checklist.LockedByUser == null)
            {
                await _context.Entry(checklist).Reference(c => c.LockedByUser).LoadAsync();
            }

            return Conflict(new
            {
                message = $"This checklist is currently being edited by another user and cannot be used to {actionDescription}.",
                lockedByUserId = checklist.LockedByUserId,
                lockedByUserName = checklist.LockedByUser?.Name,
                lockedBy = checklist.LockedByUser != null ? new { id = checklist.LockedByUser.Id, name = checklist.LockedByUser.Name } : null
            });
        }

        if (!checklist.LockedByUserId.HasValue)
        {
            checklist.LockedByUserId = userId;
        }

        return null;
    }
}
