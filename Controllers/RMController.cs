using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Middleware;
using NCBA.DCL.Models;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/rmChecklist")]
[Authorize]
public class RMController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<RMController> _logger;

    public RMController(ApplicationDbContext context, ILogger<RMController> logger)
    {
        _context = context;
        _logger = logger;
    }

    // DELETE /api/rmChecklist/:id
    [HttpDelete("{id}")]
    [RoleAuthorize(UserRole.RM, UserRole.Admin)]
    public async Task<IActionResult> RemoveDCL(Guid id)
    {
        try
        {
            var checklist = await _context.Checklists.FindAsync(id);

            if (checklist == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "DCL not found"
                });
            }

            _context.Checklists.Remove(checklist);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                message = "DCL deleted successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete DCL Error");
            return StatusCode(500, new
            {
                success = false,
                message = "Internal server error"
            });
        }
    }

    // GET /api/rmChecklist/:rmId/myqueue
    [HttpGet("{rmId}/myqueue")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> GetMyQueue(Guid rmId)
    {
        try
        {
            _logger.LogInformation($"🔥 RM ID received: {rmId}");

            if (rmId == Guid.Empty)
            {
                return BadRequest(new { message = "rmId is required" });
            }

            var checklists = await _context.Checklists
                .Where(c => c.AssignedToRMId == rmId &&
                           (c.Status == ChecklistStatus.Pending ||
                            c.Status == ChecklistStatus.RMReview))
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    ibpsNo = c.IbpsNo,
                    status = c.Status.ToString(),
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name, email = c.CreatedBy.Email } : null,
                    assignedToRM = c.AssignedToRM != null ? new { id = c.AssignedToRM.Id, name = c.AssignedToRM.Name } : null,
                    documents = c.Documents.Select(dc => new
                    {
                        id = dc.Id,
                        category = dc.Category,
                        docList = dc.DocList.Select(d => new
                        {
                            id = d.Id,
                            name = d.Name,
                            status = d.Status.ToString().ToLower(),
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            fileUrl = d.FileUrl,
                            comment = d.Comment,
                            deferralNumber = d.DeferralNumber
                        }).ToList()
                    }).ToList(),
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt
                })
                .ToListAsync();

            _logger.LogInformation($"🔥 Fetched RM Queue Count: {checklists.Count}");

            return Ok(checklists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 ERROR in getMyQueue");
            return StatusCode(500, new
            {
                message = "Server Error",
                error = ex.Message
            });
        }
    }

    // POST /api/rmChecklist/rm-submit-to-co-creator
    [HttpPost("rm-submit-to-co-creator")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> RmSubmitChecklistToCoCreator([FromBody] SubmitToCoCreatorRequest request)
    {
        try
        {
            if (!request.ChecklistId.HasValue || request.ChecklistId == Guid.Empty)
            {
                return BadRequest(new { error = "Checklist ID is required" });
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var checklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.Documents)
                    .ThenInclude(cat => cat.DocList)
                .FirstOrDefaultAsync(c => c.Id == request.ChecklistId.Value);

            if (checklist == null)
            {
                return NotFound(new { error = "Checklist not found" });
            }

            _logger.LogInformation($"📋 RM Submission - Checklist {checklist.DclNo} loaded with {checklist.Documents.Count} document categories");

            /* ===========================
               1. Update documents (RM only)
               ✅ Do NOT overwrite CO-Creator status
            =========================== */
            if (request.Documents != null && request.Documents.Any())
            {
                _logger.LogInformation($"📋 Processing {request.Documents.Count} document updates");
                int docsUpdated = 0;

                foreach (var updatedDoc in request.Documents)
                {
                    if (string.IsNullOrEmpty(updatedDoc.Category))
                    {
                        _logger.LogWarning($"⚠️ Document update skipped - missing category");
                        continue;
                    }

                    if (!updatedDoc.DocumentId.HasValue)
                    {
                        _logger.LogWarning($"⚠️ Document update skipped - missing DocumentId. _id={updatedDoc._id}, Id={updatedDoc.Id}");
                        continue;
                    }

                    var category = checklist.Documents.FirstOrDefault(c => c.Category == updatedDoc.Category);
                    if (category == null)
                    {
                        _logger.LogWarning($"⚠️ Category '{updatedDoc.Category}' not found");
                        continue;
                    }

                    var doc = category.DocList.FirstOrDefault(d => d.Id == updatedDoc.DocumentId.Value);
                    if (doc == null)
                    {
                        _logger.LogWarning($"⚠️ Document {updatedDoc.DocumentId.Value} not found in category '{updatedDoc.Category}'");
                        continue;
                    }

                    docsUpdated++;

                    // Only RM fields
                    if (updatedDoc.Status.HasValue)
                    {
                        doc.Status = updatedDoc.Status.Value;
                    }

                    if (updatedDoc.RmStatus.HasValue)
                    {
                        doc.RmStatus = updatedDoc.RmStatus.Value;
                    }

                    if (!string.IsNullOrEmpty(updatedDoc.Comment))
                    {
                        doc.Comment = updatedDoc.Comment;
                    }

                    if (!string.IsNullOrEmpty(updatedDoc.FileUrl))
                    {
                        doc.FileUrl = updatedDoc.FileUrl;
                    }

                    if (!string.IsNullOrEmpty(updatedDoc.DeferralReason))
                    {
                        doc.DeferralReason = updatedDoc.DeferralReason;
                    }

                    // ✅ Save deferral number when RM sets it
                    if (!string.IsNullOrWhiteSpace(updatedDoc.DeferralNumber))
                    {
                        doc.DeferralNumber = updatedDoc.DeferralNumber;
                    }

                    // Mark document as modified by setting UpdatedAt
                    doc.UpdatedAt = DateTime.UtcNow;
                    _logger.LogInformation($"✅ Document '{doc.Name}' in category '{updatedDoc.Category}' updated successfully");
                }

                _logger.LogInformation($"✅ {docsUpdated} documents updated");
            }

            /* ===========================
               2. RM general comment log
            =========================== */
            if (!string.IsNullOrEmpty(request.RmGeneralComment))
            {
                var commentLog = new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = $"RM Comment: {request.RmGeneralComment}",
                    UserId = userId,
                    ChecklistId = request.ChecklistId.Value,
                    Timestamp = DateTime.UtcNow
                };
                _context.ChecklistLogs.Add(commentLog);
                _logger.LogInformation($"📝 RM comment added: {request.RmGeneralComment}");
            }

            /* ===========================
               2b. Handle supporting documents (Store reference URLs)
            =========================== */
            if (request.SupportingDocs != null && request.SupportingDocs.Any())
            {
                _logger.LogInformation($"📎 Processing {request.SupportingDocs.Count} supporting documents");
                // Store supporting docs as a JSON field or separate entity
                // For now, we'll create logs for tracking
                foreach (var supportingDoc in request.SupportingDocs)
                {
                    if (!string.IsNullOrEmpty(supportingDoc.FileUrl))
                    {
                        var supportingDocLog = new ChecklistLog
                        {
                            Id = Guid.NewGuid(),
                            Message = $"Supporting Document uploaded: {supportingDoc.Name}",
                            UserId = userId,
                            ChecklistId = request.ChecklistId.Value,
                            Timestamp = DateTime.UtcNow
                        };
                        _context.ChecklistLogs.Add(supportingDocLog);
                    }
                }
            }

            /* ===========================
               3. Move checklist to Co-Creator
            =========================== */
            checklist.Status = ChecklistStatus.CoCreatorReview;
            checklist.UpdatedAt = DateTime.UtcNow; // Mark checklist as modified

            // Add log entry
            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Checklist submitted back to Co-Creator by RM",
                UserId = userId,
                ChecklistId = request.ChecklistId.Value,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // Audit
            var auditLog = new AuditLog
            {
                Id = Guid.NewGuid(),
                PerformedById = userId,
                Action = "RM_SUBMIT_TO_COCREATOR",
                Resource = "CHECKLIST",
                ResourceId = checklist.Id.ToString(),
                Details = $"DCL: {checklist.DclNo}, Status: {checklist.Status}",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(auditLog);

            // Create notification for co-creator
            if (checklist.CreatedById.HasValue)
            {
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = checklist.CreatedById.Value,
                    Message = $"DCL {checklist.DclNo} has been submitted for your review by RM",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Notifications.Add(notification);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation($"✅ RM submitted checklist {checklist.DclNo} to Co-Creator");

            return Ok(new
            {
                message = "Checklist successfully submitted to Co-Creator",
                checklist = new
                {
                    id = checklist.Id,
                    dclNo = checklist.DclNo,
                    status = checklist.Status.ToString(),
                    createdBy = checklist.CreatedBy != null ? new
                    {
                        id = checklist.CreatedBy.Id,
                        name = checklist.CreatedBy.Name,
                        email = checklist.CreatedBy.Email
                    } : null,
                    updatedAt = checklist.UpdatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RM SUBMIT TO CO-CREATOR ERROR");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // GET /api/rmChecklist/:id
    [HttpGet("{id}")]
    public async Task<IActionResult> GetChecklistById(Guid id)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Customer)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                        .ThenInclude(d => d.CoCreatorFiles)
                .Include(c => c.SupportingDocs)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { error = "Checklist not found" });
            }

            return Ok(new
            {
                id = checklist.Id,
                dclNo = checklist.DclNo,
                customerNumber = checklist.CustomerNumber,
                customerName = checklist.CustomerName,
                loanType = checklist.LoanType,
                ibpsNo = checklist.IbpsNo,
                status = checklist.Status.ToString(),
                assignedToRMId = checklist.AssignedToRMId,
                createdBy = checklist.CreatedBy != null ? new { id = checklist.CreatedBy.Id, name = checklist.CreatedBy.Name } : null,
                assignedToRM = checklist.AssignedToRM != null ? new { id = checklist.AssignedToRM.Id, name = checklist.AssignedToRM.Name } : null,
                assignedToCoChecker = checklist.AssignedToCoChecker != null ? new { id = checklist.AssignedToCoChecker.Id, name = checklist.AssignedToCoChecker.Name } : null,
                documents = checklist.Documents.Select(dc => new
                {
                    id = dc.Id,
                    category = dc.Category,
                    docList = dc.DocList.Select(d => new
                    {
                        id = d.Id,
                        name = d.Name,
                        status = d.Status.ToString().ToLower(),
                        fileUrl = d.FileUrl,
                        comment = d.Comment,
                        deferralReason = d.DeferralReason,
                        deferralNumber = d.DeferralNumber,
                        rmStatus = d.RmStatus.ToString().ToLower(),
                        coCreatorFiles = d.CoCreatorFiles.Select(cf => new
                        {
                            id = cf.Id,
                            url = cf.Url,
                            name = cf.Name
                        }).ToList()
                    }).ToList()
                }).ToList(),
                supportingDocs = checklist.SupportingDocs.Select(sd => new
                {
                    id = sd.Id,
                    name = sd.FileName,
                    fileUrl = sd.FileUrl,
                    fileSize = sd.FileSize,
                    fileType = sd.FileType,
                    uploadedBy = sd.UploadedBy != null ? new { id = sd.UploadedBy.Id, name = sd.UploadedBy.Name } : null,
                    uploadedByRole = sd.UploadedByRole,
                    uploadedAt = sd.UploadedAt
                }).ToList(),
                logs = checklist.Logs.Select(l => new
                {
                    id = l.Id,
                    message = l.Message,
                    user = l.User != null ? new { id = l.User.Id, name = l.User.Name } : null,
                    timestamp = l.Timestamp
                }).ToList(),
                createdAt = checklist.CreatedAt,
                updatedAt = checklist.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GET CHECKLIST ERROR");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // DELETE /api/rmChecklist/:checklistId/document/:documentId
    [HttpDelete("{checklistId}/document/{documentId}")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> DeleteDocumentFile(Guid checklistId, Guid documentId)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .FirstOrDefaultAsync(c => c.Id == checklistId);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var document = checklist.Documents
                .SelectMany(dc => dc.DocList)
                .FirstOrDefault(d => d.Id == documentId);

            if (document == null)
            {
                return NotFound(new { message = "Document not found" });
            }

            document.FileUrl = null;
            await _context.SaveChangesAsync();

            return Ok(new { message = "File deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting document file");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // GET /api/rmChecklist/notifications/rm
    [HttpGet("notifications/rm")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> GetRmNotifications([FromQuery] Guid userId)
    {
        try
        {
            var notifications = await _context.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => new
                {
                    id = n.Id,
                    message = n.Message,
                    read = n.Read,
                    createdAt = n.CreatedAt
                })
                .ToListAsync();

            return Ok(notifications);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notifi cations");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // PUT /api/rmChecklist/notifications/rm/:notificationId
    [HttpPut("notifications/rm/{notificationId}")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> MarkRmNotificationsAsRead(Guid notificationId)
    {
        try
        {
            var notification = await _context.Notifications.FindAsync(notificationId);
            if (notification == null)
            {
                return NotFound(new { message = "Notification not found" });
            }

            notification.Read = true;
            await _context.SaveChangesAsync();

            return Ok(notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking notification as read");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // GET /api/rmChecklist/completed/rm/:rmId
    [HttpGet("completed/rm/{rmId}")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> GetCompletedDclsForRm(Guid rmId)
    {
        try
        {
            var completed = await _context.Checklists
                .Where(c => c.AssignedToRMId == rmId &&
                           (c.Status == ChecklistStatus.Approved ||
                            c.Status == ChecklistStatus.Completed))
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToCoChecker)
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    status = c.Status.ToString(),
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name } : null,
                    assignedToCoChecker = c.AssignedToCoChecker != null ? new { id = c.AssignedToCoChecker.Id, name = c.AssignedToCoChecker.Name } : null,
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt
                })
                .ToListAsync();

            return Ok(completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching completed DCLs");
            return StatusCode(500, new { message = "Server Error" });
        }
    }

    // POST /api/rmChecklist/:id/supporting-docs
    [HttpPost("{id}/supporting-docs")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> UploadSupportingDocs(Guid id, [FromForm] List<IFormFile> files)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            _logger.LogInformation($"🔥 RM Upload Supporting Docs: checklistId={id}, filesCount={files?.Count}, userId={userId}");

            // Validate checklist exists
            var checklist = await _context.Checklists.FindAsync(id);
            if (checklist == null)
            {
                _logger.LogWarning($"❌ Checklist not found: {id}");
                return NotFound(new { message = "Checklist not found" });
            }

            _logger.LogInformation($"✅ Checklist found: {checklist.DclNo}");

            // Validate files exist
            if (files == null || files.Count == 0)
            {
                _logger.LogWarning("❌ No files in request");
                return BadRequest(new { message = "No files uploaded" });
            }

            _logger.LogInformation($"✅ Files validated: count={files.Count}");

            // Map uploaded files to supportingDocs format
            var uploadedFiles = new List<SupportingDoc>();

            foreach (var file in files)
            {
                if (file.Length > 0)
                {
                    var fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                    var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");

                    if (!Directory.Exists(uploadDir))
                        Directory.CreateDirectory(uploadDir);

                    var filePath = Path.Combine(uploadDir, fileName);

                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var doc = new SupportingDoc
                    {
                        Id = Guid.NewGuid(),
                        FileName = file.FileName,
                        FileUrl = $"/uploads/{fileName}",
                        FileSize = file.Length,
                        FileType = file.ContentType,
                        UploadedById = userId,
                        UploadedByRole = "RM",
                        UploadedAt = DateTime.UtcNow,
                        ChecklistId = id
                    };

                    _context.SupportingDocs.Add(doc);
                    uploadedFiles.Add(doc);
                }
            }

            _logger.LogInformation($"✅ Files mapped successfully: count={uploadedFiles.Count}");

            await _context.SaveChangesAsync();

            // Log audit trail
            var auditLog = new AuditLog
            {
                Id = Guid.NewGuid(),
                PerformedById = userId,
                Action = "RM_UPLOAD_SUPPORTING_DOCS",
                Resource = "CHECKLIST",
                ResourceId = checklist.Id.ToString(),
                Details = $"DCL: {checklist.DclNo}, Files: {uploadedFiles.Count}",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(auditLog);
            await _context.SaveChangesAsync();

            _logger.LogInformation("✅ Audit log created");
            _logger.LogInformation("✅ Upload successful - responding with 200");

            return StatusCode(200, new
            {
                message = "Files uploaded successfully",
                files = uploadedFiles.Select(f => new
                {
                    id = f.Id,
                    fileName = f.FileName,
                    fileUrl = f.FileUrl,
                    fileSize = f.FileSize,
                    fileType = f.FileType,
                    uploadedAt = f.UploadedAt
                }),
                checklist = new
                {
                    id = checklist.Id,
                    dclNo = checklist.DclNo,
                    status = checklist.Status.ToString()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 RM Upload Fatal Error");
            return StatusCode(500, new
            {
                message = "Server Error",
                error = ex.Message
            });
        }
    }

    // Helper method to convert string status to DocumentStatus enum
    private DocumentStatus? TryParseDocumentStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return null;

        if (Enum.TryParse<DocumentStatus>(status, ignoreCase: true, out var result))
            return result;

        var normalized = status.ToLowerInvariant();
        return normalized switch
        {
            "pending" => DocumentStatus.Pending,
            "pendingrm" => DocumentStatus.PendingRM,
            "pending_rm" => DocumentStatus.PendingRM,
            "pendingco" => DocumentStatus.PendingCo,
            "pending_co" => DocumentStatus.PendingCo,
            "submitted" => DocumentStatus.Submitted,
            "submittedreview" => DocumentStatus.SubmittedForReview,
            "submitted_for_review" => DocumentStatus.SubmittedForReview,
            "sighted" => DocumentStatus.Sighted,
            "waived" => DocumentStatus.Waived,
            "deferred" => DocumentStatus.Deferred,
            "deferral_requested" => DocumentStatus.DeferralRequested,
            "tbo" => DocumentStatus.TBO,
            "approved" => DocumentStatus.Approved,
            "incomplete" => DocumentStatus.Incomplete,
            "returned_by_checker" => DocumentStatus.ReturnedByChecker,
            "pending_from_customer" => DocumentStatus.PendingFromCustomer,
            _ => null
        };
    }

    // Helper method to convert string status to RmStatus enum
    private RmStatus? TryParseRmStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return null;

        if (Enum.TryParse<RmStatus>(status, ignoreCase: true, out var result))
            return result;

        var normalized = status.ToLowerInvariant();
        return normalized switch
        {
            "deferral_requested" => RmStatus.DeferralRequested,
            "deferralrequested" => RmStatus.DeferralRequested,
            "submitted_for_review" => RmStatus.SubmittedForReview,
            "submittedreview" => RmStatus.SubmittedForReview,
            "pending_from_customer" => RmStatus.PendingFromCustomer,
            "pendingfromcustomer" => RmStatus.PendingFromCustomer,
            _ => null
        };
    }
}
