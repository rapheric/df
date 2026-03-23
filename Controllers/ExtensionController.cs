using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Middleware;
using NCBA.DCL.Models;
using NCBA.DCL.Services;
using System.Security.Claims;
using System.Text.Json;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/extensions")]
[Authorize]
public class ExtensionController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ExtensionController> _logger;
    private readonly IEmailService _emailService;

    public ExtensionController(
        ApplicationDbContext context,
        ILogger<ExtensionController> logger,
        IEmailService emailService)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
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
            var userName = User.FindFirst("name")?.Value ?? "User";

            // Load deferral with all related data needed for validation and extension creation
            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .Include(d => d.Documents)  // Load documents for fallback calculation
                .FirstOrDefaultAsync(d => d.Id == request.DeferralId);

            if (deferral == null)
                return NotFound(new { message = "Deferral not found" });

            if (deferral.Status != DeferralStatus.Approved)
                return BadRequest(new { message = "Can only apply for extension on approved deferrals" });

            // Deserialize SelectedDocuments from JSON to get accurate DaysSought data
            DeserializeSelectedDocuments(deferral);

            // Determine the current DaysSought value - use main field, or calculate from selected documents if 0
            var currentDaysSought = deferral.DaysSought > 0 ? deferral.DaysSought : 0;

            // Fallback 1: Try to get max from SelectedDocuments (JSON)
            if (currentDaysSought < 1 && deferral.SelectedDocuments != null && deferral.SelectedDocuments.Any())
            {
                currentDaysSought = deferral.SelectedDocuments
                    .Where(d => d.DaysSought.HasValue && d.DaysSought > 0)
                    .Max(d => d.DaysSought ?? 0);

                _logger.LogWarning($"[EXTENSION] Main DaysSought was 0, using max from SelectedDocuments: {currentDaysSought}");
            }

            // Fallback 2: If still 0, try to get max from DeferralDocuments table (for old deferrals)
            if (currentDaysSought < 1 && deferral.Documents != null && deferral.Documents.Any())
            {
                currentDaysSought = deferral.Documents
                    .Where(d => d.DaysSought.HasValue && d.DaysSought > 0)
                    .Max(d => d.DaysSought ?? 0);

                _logger.LogWarning($"[EXTENSION] SelectedDocuments was empty, using max from DeferralDocuments table: {currentDaysSought}");
            }

            if (currentDaysSought < 1)
                return BadRequest(new { message = $"Deferral must have valid daysSought field (current value: {currentDaysSought})" });

            if (request.RequestedDaysSought <= currentDaysSought)
                return BadRequest(new { message = $"Requested days ({request.RequestedDaysSought}) must be greater than current days ({currentDaysSought})" });

            // Create extension
            var extension = new Extension
            {
                DeferralId = request.DeferralId,
                DeferralNumber = deferral.DeferralNumber,
                CustomerName = deferral.CustomerName,
                CustomerNumber = deferral.CustomerNumber,
                DclNumber = deferral.DclNumber,
                LoanAmount = deferral.LoanAmount,
                NextDueDate = deferral.NextDueDate,
                NextDocumentDueDate = deferral.NextDocumentDueDate,
                SlaExpiry = deferral.SlaExpiry,
                CurrentDaysSought = currentDaysSought,
                RequestedDaysSought = request.RequestedDaysSought,
                ExtensionReason = request.ExtensionReason,
                RequestedById = userId,
                RequestedByName = userName,
                Status = ExtensionStatus.PendingApproval,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Handle Additional Files
            if (request.AdditionalFiles != null && request.AdditionalFiles.Any())
            {
                foreach (var fileDto in request.AdditionalFiles)
                {
                    extension.AdditionalFiles.Add(new ExtensionFile
                    {
                        Name = fileDto.Name,
                        Url = fileDto.Url,
                        Size = fileDto.Size,
                        UploadedAt = DateTime.UtcNow
                    });
                }
            }

            // Compute and persist selected documents for this extension using extensionDaysByDoc if provided
            try
            {
                var selectedDocs = new List<SelectedDocumentData>();
                var extDays = request.ExtensionDaysByDoc ?? new Dictionary<string, int>();

                // Ensure deferral.SelectedDocuments is deserialized earlier
                var persisted = deferral.SelectedDocuments ?? new List<SelectedDocumentData>();

                if (persisted != null && persisted.Any())
                {
                    foreach (var sd in persisted)
                    {
                        var name = (sd?.Name ?? sd?.Type ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var key = name.ToLowerInvariant().Trim();
                        extDays.TryGetValue(key, out var addDays);

                        DateTime baseDate = sd.NextDocumentDueDate ?? deferral.NextDocumentDueDate ?? deferral.CreatedAt;
                        DateTime? nextDue = null;
                        if (addDays > 0 && baseDate != default)
                        {
                            nextDue = baseDate.AddDays(addDays);
                        }

                        selectedDocs.Add(new SelectedDocumentData
                        {
                            Name = name,
                            Type = sd?.Type,
                            Category = sd?.Category,
                            DaysSought = addDays > 0 ? addDays : (int?)null,
                            NextDocumentDueDate = nextDue
                        });
                    }
                }
                else if (deferral.Documents != null && deferral.Documents.Any())
                {
                    foreach (var d in deferral.Documents)
                    {
                        var name = d.Name ?? string.Empty;
                        var key = name.ToLowerInvariant().Trim();
                        extDays.TryGetValue(key, out var addDays);
                        DateTime baseDate = d.NextDocumentDueDate ?? deferral.NextDocumentDueDate ?? deferral.CreatedAt;
                        DateTime? nextDue = null;
                        if (addDays > 0 && baseDate != default)
                        {
                            nextDue = baseDate.AddDays(addDays);
                        }
                        selectedDocs.Add(new SelectedDocumentData
                        {
                            Name = name,
                            Type = null,
                            Category = null,
                            DaysSought = addDays > 0 ? addDays : (int?)null,
                            NextDocumentDueDate = nextDue
                        });
                    }
                }

                if (selectedDocs.Any())
                {
                    extension.SelectedDocuments = selectedDocs;
                    extension.SelectedDocumentsJson = JsonSerializer.Serialize(selectedDocs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute extension selected documents");
            }

            // Copy approvers from deferral
            var deferralApprovers = await _context.Approvers
                .Where(a => a.DeferralId == deferral.Id)
                .Include(a => a.User)
                .ToListAsync();

            if (!deferralApprovers.Any())
                return BadRequest(new { message = "Deferral must have approvers to create extension" });

            var orderedDeferralApprovers = deferralApprovers
                .Select((approver, originalIndex) => new
                {
                    Approver = approver,
                    OriginalIndex = originalIndex,
                    Sequence = GetApproverSequenceValue(approver)
                })
                .OrderBy(x => x.Sequence)
                .ThenBy(x => x.OriginalIndex)
                .Select(x => x.Approver)
                .ToList();

            foreach (var approver in orderedDeferralApprovers.Select((value, index) => new { value, index }))
            {
                extension.Approvers.Add(new ExtensionApprover
                {
                    UserId = approver.value.UserId,
                    User = approver.value.User,
                    Role = approver.value.Role,
                    ApprovalStatus = ApproverApprovalStatus.Pending,
                    IsCurrent = approver.index == 0,
                    Sequence = approver.index + 1,
                });
            }

            extension.History.Add(new ExtensionHistory
            {
                Action = "extension_requested",
                UserId = userId,
                UserName = userName,
                UserRole = User.FindFirst(ClaimTypes.Role)?.Value,
                Notes = $"Extension requested: {deferral.DaysSought} days -> {request.RequestedDaysSought} days"
            });

            _context.Extensions.Add(extension);

            // Update deferral to indicate it has a pending extension
            deferral.ExtensionStatus = ExtensionStatus.PendingApproval.ToString();
            deferral.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Send email notification to first approver
            var currentApprover = extension.Approvers
                .OrderBy(a => a.Sequence)
                .FirstOrDefault(a => a.IsCurrent);
            if (currentApprover != null && currentApprover.UserId.HasValue)
            {
                var approverUser = await _context.Users.FindAsync(currentApprover.UserId);
                if (approverUser != null && !string.IsNullOrEmpty(approverUser.Email))
                {
                    try
                    {
                        await _emailService.SendExtensionApprovalRequestAsync(
                            approverUser.Email,
                            approverUser.Name,
                            extension.DeferralNumber ?? "Unknown",
                            userName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send extension notification email");
                    }
                }
            }

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
                .Include(e => e.AdditionalFiles)
                .Where(e => e.RequestedById == userId)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            DeserializeExtensionSelectedDocuments(extensions);

            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting my extensions");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/extensions/rm/applications
    [HttpGet("rm/applications")]
    [RoleAuthorize(UserRole.RM)]
    public async Task<IActionResult> GetRMExtensionApplications()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var extensions = await _context.Extensions
                .Include(e => e.Deferral)
                .Include(e => e.Approvers).ThenInclude(a => a.User)
                .Include(e => e.RequestedBy)
                .Include(e => e.History).ThenInclude(h => h.User)
                .Include(e => e.AdditionalFiles)
                .Include(e => e.Comments).ThenInclude(c => c.Author)
                .Where(e => e.RequestedById == userId && e.Status != ExtensionStatus.Approved)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            DeserializeExtensionSelectedDocuments(extensions);

            _logger.LogInformation($"✅ Fetched {extensions.Count} extension applications for RM {userId}");
            return Ok(extensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting RM extension applications");
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

            DeserializeExtensionSelectedDocuments(extensions);

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
                    a.ApprovalStatus != ApproverApprovalStatus.Pending) &&
                    e.Status != ExtensionStatus.Approved)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            DeserializeExtensionSelectedDocuments(extensions);

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
                .OrderBy(a => a.Sequence)
                .FirstOrDefault(a => a.ApprovalStatus == ApproverApprovalStatus.Pending);

            if (nextApprover != null)
            {
                nextApprover.IsCurrent = true;
                extension.Status = ExtensionStatus.InReview;
            }
            else
            {
                extension.AllApproversApproved = true;
                extension.Status = ExtensionStatus.InReview;
            }

            // Update deferral's extension status to match
            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == extension.DeferralId);
            if (deferral != null)
            {
                deferral.ExtensionStatus = extension.Status.ToString();
                deferral.UpdatedAt = DateTime.UtcNow;

                // Mark the corresponding deferral approver as approved
                var deferralApprover = deferral.Approvers.FirstOrDefault(a => a.UserId == userId);
                if (deferralApprover != null)
                {
                    deferralApprover.Approved = true;
                    deferralApprover.ApprovedAt = DateTime.UtcNow;
                }

                extension.Deferral = deferral;
            }

            await _context.SaveChangesAsync();

            if (nextApprover != null && nextApprover.UserId.HasValue)
            {
                var nextApproverUser = await _context.Users.FindAsync(nextApprover.UserId.Value);
                if (nextApproverUser != null && !string.IsNullOrWhiteSpace(nextApproverUser.Email))
                {
                    try
                    {
                        await _emailService.SendExtensionApprovalRequestAsync(
                            nextApproverUser.Email,
                            nextApproverUser.Name,
                            extension.DeferralNumber ?? "Unknown",
                            User.FindFirst("name")?.Value ?? "User");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send next approver extension notification email");
                    }
                }
            }

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(new { message = "Extension approved", extension, deferral = extension.Deferral });
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

            // Update deferral's extension status to match
            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == extension.DeferralId);
            if (deferral != null)
            {
                deferral.ExtensionStatus = extension.Status.ToString();
                deferral.UpdatedAt = DateTime.UtcNow;

                // Mark the corresponding deferral approver as rejected
                var deferralApprover = deferral.Approvers.FirstOrDefault(a => a.UserId == userId);
                if (deferralApprover != null)
                {
                    deferralApprover.Rejected = true;
                    deferralApprover.RejectedAt = DateTime.UtcNow;
                }

                extension.Deferral = deferral;
            }

            await _context.SaveChangesAsync();

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(new { message = "Extension rejected", extension, deferral = extension.Deferral });
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
                .Where(e =>
                    e.AllApproversApproved &&
                    e.CreatorApprovalStatus == CreatorApprovalStatus.Pending &&
                    e.Status != ExtensionStatus.Approved &&
                    e.Status != ExtensionStatus.Rejected)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            DeserializeExtensionSelectedDocuments(extensions);

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
                .Include(e => e.Deferral)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (extension == null)
                return NotFound(new { message = "Extension not found" });

            extension.CreatorApprovalStatus = CreatorApprovalStatus.Approved;
            extension.CreatorApprovedById = userId;
            extension.CreatorApprovalDate = DateTime.UtcNow;
            extension.CreatorApprovalComment = request.Comment;
            extension.Status = ExtensionStatus.InReview;

            extension.History.Add(new ExtensionHistory
            {
                Action = "approved_by_creator",
                UserId = userId,
                UserName = User.FindFirst("name")?.Value,
                UserRole = "Creator",
                Date = DateTime.UtcNow,
                Comment = request.Comment
            });

            var deferral = await _context.Deferrals.FindAsync(extension.DeferralId);
            if (deferral != null)
            {
                deferral.ExtensionStatus = extension.Status.ToString();
                deferral.UpdatedAt = DateTime.UtcNow;

                extension.Deferral = deferral;
            }

            await _context.SaveChangesAsync();

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(new { message = "Extension approved by creator", extension, deferral = extension.Deferral });
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
                .Include(e => e.Deferral)
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

            // Update deferral's extension status to match
            var deferral = await _context.Deferrals.FindAsync(extension.DeferralId);
            if (deferral != null)
            {
                deferral.ExtensionStatus = extension.Status.ToString();
                deferral.UpdatedAt = DateTime.UtcNow;

                extension.Deferral = deferral;
            }

            await _context.SaveChangesAsync();

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(new { message = "Extension rejected by creator", extension, deferral = extension.Deferral });
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
                .Where(e =>
                    e.AllApproversApproved &&
                    e.CreatorApprovalStatus == CreatorApprovalStatus.Approved &&
                    e.CheckerApprovalStatus == CheckerApprovalStatus.Pending &&
                    e.Status != ExtensionStatus.Approved &&
                    e.Status != ExtensionStatus.Rejected)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            DeserializeExtensionSelectedDocuments(extensions);

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

            DeserializeExtensionSelectedDocuments(extension);

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
                extension.Deferral.ExtensionStatus = extension.Status.ToString();
                if (!string.IsNullOrWhiteSpace(extension.SelectedDocumentsJson))
                {
                    extension.Deferral.SelectedDocumentsJson = extension.SelectedDocumentsJson;
                    extension.Deferral.SelectedDocuments = extension.SelectedDocuments;
                }

                var latestNextDocumentDueDate = extension.SelectedDocuments?
                    .Where(document => document.NextDocumentDueDate.HasValue)
                    .Select(document => document.NextDocumentDueDate!.Value)
                    .OrderByDescending(value => value)
                    .FirstOrDefault();

                if (latestNextDocumentDueDate.HasValue)
                {
                    extension.Deferral.NextDocumentDueDate = latestNextDocumentDueDate.Value;
                    extension.Deferral.NextDueDate = latestNextDocumentDueDate.Value;
                }

                extension.Deferral.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(new { message = "Extension approved by checker", extension, deferral = extension.Deferral });
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
                .Include(e => e.Deferral)
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

            // Update deferral's extension status to match
            var deferral = await _context.Deferrals.FindAsync(extension.DeferralId);
            if (deferral != null)
            {
                deferral.ExtensionStatus = extension.Status.ToString();
                deferral.UpdatedAt = DateTime.UtcNow;

                extension.Deferral = deferral;
            }

            await _context.SaveChangesAsync();

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(new { message = "Extension rejected by checker", extension, deferral = extension.Deferral });
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

            DeserializeExtensionSelectedDocuments(extension);

            return Ok(extension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting extension by id");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // ================================
    // HELPER METHODS
    // ================================

    private static void DeserializeSelectedDocuments(Deferral deferral)
    {
        if (deferral == null || string.IsNullOrWhiteSpace(deferral.SelectedDocumentsJson))
        {
            deferral!.SelectedDocuments = new List<SelectedDocumentData>();
            return;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            deferral.SelectedDocuments = JsonSerializer.Deserialize<List<SelectedDocumentData>>(
                deferral.SelectedDocumentsJson,
                options
            ) ?? new List<SelectedDocumentData>();
        }
        catch (Exception ex)
        {
            // If deserialization fails, return empty list
            deferral.SelectedDocuments = new List<SelectedDocumentData>();
        }
    }

    private static void DeserializeExtensionSelectedDocuments(IEnumerable<Extension> extensions)
    {
        foreach (var extension in extensions)
        {
            DeserializeExtensionSelectedDocuments(extension);
        }
    }

    private static void DeserializeExtensionSelectedDocuments(Extension extension)
    {
        if (extension == null || string.IsNullOrWhiteSpace(extension.SelectedDocumentsJson))
        {
            extension!.SelectedDocuments = new List<SelectedDocumentData>();
            return;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            extension.SelectedDocuments = JsonSerializer.Deserialize<List<SelectedDocumentData>>(
                extension.SelectedDocumentsJson,
                options
            ) ?? new List<SelectedDocumentData>();
        }
        catch
        {
            extension.SelectedDocuments = new List<SelectedDocumentData>();
        }
    }

    private static int GetApproverSequenceValue(Approver? approver)
    {
        if (approver == null)
        {
            return int.MaxValue;
        }

        var rawId = approver.Id.ToString("N");
        if (rawId.Length >= 8)
        {
            var sequenceHex = rawId.Substring(0, 8);
            if (int.TryParse(sequenceHex, System.Globalization.NumberStyles.HexNumber, null, out var sequence))
            {
                return sequence;
            }
        }

        return int.MaxValue;
    }
}