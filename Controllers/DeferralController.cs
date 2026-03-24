
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using NCBA.DCL.Data;
using NCBA.DCL.Helpers;
using NCBA.DCL.Models;
using NCBA.DCL.Services;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/deferrals")]
[Authorize]
public class DeferralController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<DeferralController> _logger;
    private readonly IEmailService _emailService;
    private const decimal LoanThreshold = 75000000m;

    public DeferralController(ApplicationDbContext context, ILogger<DeferralController> logger, IEmailService emailService)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
    }

    // ============================================
    // CREATE & BASIC OPERATIONS
    // ============================================

    [HttpPost]
    public async Task<IActionResult> CreateDeferral([FromBody] CreateDeferralRequest request)
    {
        try
        {
            var userIdClaim = User.FindFirst("id")?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                _logger.LogWarning("Unauthorized request to create deferral: missing or invalid user id claim");
                return Unauthorized(new { error = "Invalid user context" });
            }

            if (request.SelectedDocuments == null || request.SelectedDocuments.Count == 0)
            {
                return BadRequest(new { error = "At least one document must be selected to determine approvers" });
            }

            if (request.LoanAmount == null)
            {
                return BadRequest(new { error = "Loan amount is required to determine approvers" });
            }

            var requiredRoles = GetApprovalMatrixRoles(request.SelectedDocuments, request.LoanAmount.Value);
            if (requiredRoles.Count == 0)
            {
                return BadRequest(new { error = "Unable to determine approval matrix from selected documents" });
            }

            if (request.Approvers == null || request.Approvers.Count == 0)
            {
                return BadRequest(new { error = "Approver selection is required" });
            }

            var providedRoles = request.Approvers
                .Select(a => a.Role?.Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r!)
                .ToList();

            if (!RolesMatch(requiredRoles, providedRoles))
            {
                return BadRequest(new
                {
                    error = "Approver selection does not match required approval matrix",
                    expectedRoles = requiredRoles
                });
            }

            // Parse approver identifiers robustly (accept Guid values or string GUIDs)
            var approverUserIds = new List<Guid>();
            foreach (var a in request.Approvers)
            {
                var parsed = TryGetGuidFromClientValue(a.UserId) ?? TryGetGuidFromClientValue(a.User);
                if (!parsed.HasValue)
                {
                    return BadRequest(new { error = "All approver slots must be assigned to a valid user GUID" });
                }
                approverUserIds.Add(parsed.Value);
            }

            var approverUsers = await _context.Users
                .Where(u => approverUserIds.Contains(u.Id))
                .ToListAsync();

            if (approverUsers.Count != approverUserIds.Count)
            {
                return BadRequest(new { error = "One or more approvers were not found" });
            }

            // Generate deferral number (DEF-YY-XXXX format)
            var deferralNumber = await GenerateDeferralNumber();

            var deferral = new Deferral
            {
                Id = Guid.NewGuid(),
                DeferralNumber = deferralNumber,
                CustomerNumber = request.CustomerNumber,
                CustomerName = request.CustomerName,
                BusinessName = request.BusinessName,
                LoanType = request.LoanType,
                DaysSought = request.DaysSought ?? 0,
                DclNumber = request.DclNumber,
                DeferralDescription = request.DeferralDescription,
                Status = DeferralStatus.Pending,
                CreatedById = userId,
                CurrentApproverIndex = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var initialComments = (request.Comments ?? new List<DeferralCommentRequest>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .Select(c => new DeferralCommentEntry
                {
                    Text = c.Text?.Trim(),
                    CreatedAt = c.CreatedAt ?? DateTime.UtcNow,
                    AuthorName = c.AuthorName ?? c.Author?.Name,
                    AuthorRole = c.AuthorRole ?? c.Author?.Role,
                    Author = new DeferralCommentAuthor
                    {
                        Name = c.Author?.Name ?? c.AuthorName,
                        Role = c.Author?.Role ?? c.AuthorRole,
                    }
                })
                .ToList();

            if (initialComments.Count > 0)
            {
                deferral.ReworkComments = SerializeDeferralCommentStore(new DeferralCommentStore
                {
                    RmReason = initialComments[0].Text,
                    Comments = initialComments,
                });
            }

            _context.Deferrals.Add(deferral);

            if (request.Facilities != null && request.Facilities.Count > 0)
            {
                foreach (var facilityReq in request.Facilities)
                {
                    _context.Facilities.Add(new Facility
                    {
                        Id = Guid.NewGuid(),
                        Type = facilityReq.Type,
                        Sanctioned = facilityReq.Sanctioned,
                        Balance = facilityReq.Balance,
                        Headroom = facilityReq.Headroom,
                        DeferralId = deferral.Id
                    });
                }
            }

            if (request.SelectedDocuments != null && request.SelectedDocuments.Count > 0)
            {
                var seenDocumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var selectedDoc in request.SelectedDocuments)
                {
                    var documentName = (selectedDoc.Name ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(documentName))
                    {
                        continue;
                    }

                    if (!seenDocumentNames.Add(documentName))
                    {
                        continue;
                    }

                    _context.DeferralDocuments.Add(new DeferralDocument
                    {
                        Id = Guid.NewGuid(),
                        Name = documentName,
                        Url = null,
                        UploadedById = null,
                        DeferralId = deferral.Id,
                        // persist per-document days and next due date when provided
                        DaysSought = selectedDoc.DaysSought,
                        NextDocumentDueDate = selectedDoc.NextDocumentDueDate.HasValue ? DateTime.SpecifyKind(selectedDoc.NextDocumentDueDate.Value, DateTimeKind.Utc) : null
                    });
                }

                // After adding all documents, set the main deferral.DaysSought to the max from documents
                // This ensures the deferral has a valid DaysSought for extension requests later
                var maxDocumentDays = request.SelectedDocuments
                    .Where(d => d.DaysSought.HasValue && d.DaysSought > 0)
                    .Select(d => d.DaysSought ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();

                if (maxDocumentDays > 0)
                {
                    deferral.DaysSought = maxDocumentDays;
                    _logger.LogWarning($"[DEFERRAL-CREATE] Set deferral.DaysSought to {maxDocumentDays} (max from {request.SelectedDocuments.Count} documents)");
                }

                // Also store selected documents as JSON for later retrieval/comparison
                try
                {
                    deferral.SelectedDocumentsJson = JsonSerializer.Serialize(request.SelectedDocuments);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DEFERRAL-CREATE] Failed to serialize selected documents to JSON");
                }
            }

            for (var approverIndex = 0; approverIndex < request.Approvers.Count; approverIndex++)
            {
                var approverReq = request.Approvers[approverIndex];
                var approverUserId = (approverReq.UserId ?? approverReq.User)!.Value;
                var user = approverUsers.First(u => u.Id == approverUserId);
                _context.Approvers.Add(new Approver
                {
                    Id = CreateOrderedApproverId(deferral.Id, approverIndex),
                    UserId = user.Id,
                    Name = user.Name,
                    Role = approverReq.Role,
                    Approved = false,
                    DeferralId = deferral.Id
                });
            }

            await _context.SaveChangesAsync();

            var attemptedRecipients = 0;
            var notifiedRoles = new List<string>();
            var emailDispatchError = false;

            try
            {
                var rmUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

                var recipientDirectory = new Dictionary<string, (string Name, string Role)>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(rmUser?.Email))
                {
                    recipientDirectory[rmUser.Email] =
                        (string.IsNullOrWhiteSpace(rmUser.Name) ? "RM" : rmUser.Name, "Relationship Manager");
                }

                var firstApproverRequest = request.Approvers.FirstOrDefault();
                var firstApproverId = firstApproverRequest?.UserId ?? firstApproverRequest?.User;

                if (firstApproverId.HasValue)
                {
                    var firstApproverUser = approverUsers.FirstOrDefault(u => u.Id == firstApproverId.Value);
                    if (!string.IsNullOrWhiteSpace(firstApproverUser?.Email))
                    {
                        var firstApproverRole = string.IsNullOrWhiteSpace(firstApproverRequest?.Role)
                            ? "Approver"
                            : firstApproverRequest!.Role!;

                        recipientDirectory[firstApproverUser.Email] =
                            (string.IsNullOrWhiteSpace(firstApproverUser.Name) ? "Approver" : firstApproverUser.Name, firstApproverRole);
                    }
                }

                attemptedRecipients = recipientDirectory.Count;
                notifiedRoles = recipientDirectory.Values
                    .Select(v => v.Role)
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var recipient in recipientDirectory)
                {
                    await _emailService.SendDeferralSubmittedAsync(
                        recipient.Key,
                        recipient.Value.Name,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        deferral.DaysSought,
                        recipient.Value.Role
                    );
                }

                _logger.LogInformation("📧 Submission emails queued for deferral {DeferralNumber}: {RecipientCount} recipient(s)",
                    deferral.DeferralNumber,
                    recipientDirectory.Count);
            }
            catch (Exception emailEx)
            {
                emailDispatchError = true;
                _logger.LogWarning(emailEx, "⚠️ Deferral created but submit email notifications failed for {DeferralNumber}", deferral.DeferralNumber);
            }

            _logger.LogInformation($"✅ Deferral created: {deferralNumber}");

            HydrateDeferralComments(deferral);

            return StatusCode(201, new
            {
                deferral,
                selectedDocuments = request.SelectedDocuments,
                emailNotification = new
                {
                    attemptedRecipients,
                    notifiedRoles,
                    hadDispatchError = emailDispatchError
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error creating deferral");
            var inner = ex.GetBaseException();
            return StatusCode(500, new { error = ex.Message, innerException = inner?.Message, stack = ex.ToString() });
        }
    }

    // ============================================
    // FETCH OPERATIONS
    // ============================================

    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingDeferrals()
    {
        try
        {
            var deferrals = await _context.Deferrals
                .Where(d => d.Status == DeferralStatus.Pending || d.Status == DeferralStatus.InReview || d.Status == DeferralStatus.PartiallyApproved)
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);
            deferrals.ForEach(DeserializeSelectedDocuments);

            _logger.LogInformation($"📊 Fetched {deferrals.Count} pending deferrals");
            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching pending deferrals");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("approved")]
    public async Task<IActionResult> GetApprovedDeferrals()
    {
        try
        {
            var deferrals = await _context.Deferrals
                .Where(d => d.Status == DeferralStatus.Approved)
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.Approvers)
                        .ThenInclude(a => a.User)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.CreatorApprovedBy)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.CheckerApprovedBy)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.RequestedBy)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.History)
                        .ThenInclude(h => h.User)
                .OrderByDescending(d => d.UpdatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);
            deferrals.ForEach(DeserializeSelectedDocuments);

            _logger.LogInformation($"✅ Fetched {deferrals.Count} approved deferrals");
            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching approved deferrals");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("all")]
    public async Task<IActionResult> GetAllDeferrals()
    {
        try
        {
            var deferrals = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.Approvers)
                        .ThenInclude(a => a.User)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.CreatorApprovedBy)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.CheckerApprovedBy)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.RequestedBy)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.History)
                        .ThenInclude(h => h.User)
                .OrderByDescending(d => d.UpdatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);
            deferrals.ForEach(DeserializeSelectedDocuments);
            deferrals.ForEach(d => DeserializeExtensionSelectedDocuments(d.Extensions));

            _logger.LogInformation("📚 Fetched {Count} deferrals across all statuses", deferrals.Count);
            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching all deferrals");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("close-workflow")]
    public async Task<IActionResult> GetCloseWorkflowDeferrals()
    {
        try
        {
            var deferrals = await _context.Deferrals
                .Where(d =>
                    d.Status == DeferralStatus.CloseRequested ||
                    d.Status == DeferralStatus.CloseRequestedCreatorApproved ||
                    d.Status == DeferralStatus.Closed)
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                .OrderByDescending(d => d.UpdatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);
            deferrals.ForEach(DeserializeSelectedDocuments);

            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching close workflow deferrals");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("my")]
    public async Task<IActionResult> GetMyDeferrals()
    {
        try
        {
            var userIdClaim = User.FindFirst("id")?.Value;
            _logger.LogInformation(string.Format("[DEFERRAL_API] GetMyDeferrals called - User ID claim: {0}", userIdClaim));
            
            if (string.IsNullOrWhiteSpace(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            {
                _logger.LogError(string.Format("[DEFERRAL_API] Invalid user ID: {0}", userIdClaim));
                return Unauthorized(new { error = "Invalid user context" });
            }

            _logger.LogInformation(string.Format("[DEFERRAL_API] Parsed userId: {0}", userId));

            // First, get basic count
            var totalDefCount = await _context.Deferrals.CountAsync();
            var userDefCount = await _context.Deferrals.CountAsync(d => d.CreatedById == userId);
            _logger.LogInformation(string.Format("[DEFERRAL_API] Total deferrals in DB: {0}, User deferrals: {1}", totalDefCount, userDefCount));

            // Get deferrals with minimal includes first
            var deferrals = await _context.Deferrals
                .Where(d => d.CreatedById == userId)
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Approvers)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            _logger.LogInformation(string.Format("[DEFERRAL_API] Retrieved {0} deferrals from database", deferrals.Count));

            try
            {
                _logger.LogInformation("[DEFERRAL_API] Applying approver ordering...");
                deferrals.ForEach(ApplyApproverOrdering);
                _logger.LogInformation("[DEFERRAL_API] Approver ordering complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DEFERRAL_API] Error in ApplyApproverOrdering");
                throw;
            }

            try
            {
                _logger.LogInformation("[DEFERRAL_API] Hydrating deferral comments...");
                deferrals.ForEach(HydrateDeferralComments);
                _logger.LogInformation("[DEFERRAL_API] Hydrating comments complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DEFERRAL_API] Error in HydrateDeferralComments");
                throw;
            }

            try
            {
                _logger.LogInformation("[DEFERRAL_API] Deserializing selected documents...");
                deferrals.ForEach(DeserializeSelectedDocuments);
                _logger.LogInformation("[DEFERRAL_API] Deserializing documents complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DEFERRAL_API] Error in DeserializeSelectedDocuments");
                throw;
            }

            _logger.LogInformation(string.Format("[DEFERRAL_API] About to return {0} deferrals", deferrals.Count));
            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DEFERRAL_API] Error fetching my deferrals");
            _logger.LogError(string.Format("[DEFERRAL_API] Exception Type: {0}", ex.GetType().Name));
            _logger.LogError(string.Format("[DEFERRAL_API] Exception Message: {0}", ex.Message));
            return StatusCode(500, new { error = ex.Message, type = ex.GetType().Name });
        }
    }

    [HttpGet("approver-queue")]
    public async Task<IActionResult> GetApproverQueue()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var pendingDeferrals = await _context.Deferrals
                .Where(d => d.Status == DeferralStatus.Pending || d.Status == DeferralStatus.InReview)
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            var deferrals = pendingDeferrals
                .Where(d =>
                {
                    var orderedApprovers = OrderApproversForFlow(d.Approvers);
                    var currentIndex = GetSafeCurrentApproverIndex(d.CurrentApproverIndex, orderedApprovers.Count);
                    var currentApprover = orderedApprovers.ElementAtOrDefault(currentIndex);
                    return currentApprover?.UserId == userId && !currentApprover.Approved;
                })
                .ToList();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);
            deferrals.ForEach(DeserializeSelectedDocuments);

            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching approver queue");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("actioned")]
    public async Task<IActionResult> GetActionedDeferrals()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            // Include deferrals the approver has approved OR those they have explicitly rejected
            var deferrals = await _context.Deferrals
                .Where(d =>
                    // Any approver record for this user that's marked approved
                    d.Approvers.Any(a => a.UserId == userId && a.Approved)
                    // OR the deferral is rejected and this user is one of the approvers (they likely performed the rejection)
                    || (d.Status == DeferralStatus.Rejected && d.Approvers.Any(a => a.UserId == userId))
                )
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);
            deferrals.ForEach(DeserializeSelectedDocuments);

            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching actioned deferrals");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetDeferral(Guid id)
    {
        try
        {
            var deferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.Approvers)
                        .ThenInclude(a => a.User)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.AdditionalFiles)
                .Include(d => d.Extensions)
                    .ThenInclude(e => e.History)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            ApplyApproverOrdering(deferral);
            HydrateDeferralComments(deferral);
            DeserializeSelectedDocuments(deferral);
            DeserializeExtensionSelectedDocuments(deferral.Extensions);

            return Ok(deferral);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // SEARCH
    // ============================================

    [HttpGet("search")]
    public async Task<IActionResult> SearchDeferrals([FromQuery] string? dclNumber, [FromQuery] string? deferralNumber)
    {
        try
        {
            var query = _context.Deferrals.Include(d => d.CreatedBy).AsQueryable();

            if (!string.IsNullOrEmpty(dclNumber))
            {
                query = query.Where(d => d.DclNumber != null && d.DclNumber.Contains(dclNumber));
            }

            if (!string.IsNullOrEmpty(deferralNumber))
            {
                query = query.Where(d => d.DeferralNumber != null && d.DeferralNumber.Contains(deferralNumber));
            }

            var results = await query
                .Select(d => new
                {
                    id = d.Id,
                    deferralNumber = d.DeferralNumber,
                    dclNumber = d.DclNumber,
                    dclNo = d.DclNumber,  // Support both naming conventions
                    customerName = d.CreatedBy != null ? d.CreatedBy.Name : "Unknown",
                    customerNumber = d.CreatedBy != null ? d.CreatedBy.CustomerNumber : null,
                    loanType = d.LoanType,
                    status = d.Status.ToString(),
                    createdAt = d.CreatedAt
                })
                .Take(20)  // Limit results for performance
                .ToListAsync();

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching deferrals");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // ============================================
    // DEFERRAL NUMBER GENERATION
    // ============================================

    [HttpGet("next-number")]
    [AllowAnonymous]
    public async Task<IActionResult> GetNextDeferralNumber()
    {
        try
        {
            var deferralNumber = await GenerateDeferralNumber();
            return Ok(new { deferralNumber });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error generating deferral number - returning preview fallback");
            // Return a safe preview-style deferral number instead of 500 to keep frontend resilient
            var yy = DateTime.UtcNow.Year.ToString().Substring(2);
            var prefix = $"DEF-{yy}-";
            var preview = prefix + "0001";
            return Ok(new { deferralNumber = preview, preview = true });
        }
    }

    [HttpGet("preview-number")]
    [AllowAnonymous]
    public IActionResult GetPreviewDeferralNumber()
    {
        try
        {
            var yy = DateTime.UtcNow.Year.ToString().Substring(2);
            var prefix = $"DEF-{yy}-";
            // Lightweight preview: not persisted, safe for display
            var preview = prefix + "TBD";
            return Ok(new { deferralNumber = preview });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error generating preview deferral number");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // FACILITIES OPERATIONS
    // ============================================

    [HttpPut("{id}/facilities")]
    public async Task<IActionResult> UpdateFacilities(Guid id, [FromBody] List<FacilityRequest> facilities)
    {
        try
        {
            var deferral = await _context.Deferrals
                .Include(d => d.Facilities)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            _context.Facilities.RemoveRange(deferral.Facilities);

            foreach (var facilityReq in facilities)
            {
                var facility = new Facility
                {
                    Id = Guid.NewGuid(),
                    Type = facilityReq.Type,
                    Sanctioned = facilityReq.Sanctioned,
                    Balance = facilityReq.Balance,
                    Headroom = facilityReq.Headroom,
                    DeferralId = id
                };
                _context.Facilities.Add(facility);
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Facilities updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error updating facilities");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // DEFERRAL UPDATE (Resubmit)
    // ============================================

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDeferral(Guid id, [FromBody] UpdateDeferralRequest request)
    {
        try
        {
            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .Include(d => d.Documents)  // CRITICAL: Must load documents to delete them
                .Include(d => d.Facilities)  // Also include other relationships that might be updated
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            _logger.LogWarning($"[DEFERRAL-DELETE-FETCH] Loaded deferral {id} with {deferral.Documents?.Count ?? 0} documents");

            // Update allowed fields
            if (!string.IsNullOrEmpty(request.DeferralDescription))
                deferral.DeferralDescription = request.DeferralDescription;

            if (request.Facilities != null && request.Facilities.Count > 0)
            {
                _context.Facilities.RemoveRange(deferral.Facilities);
                foreach (var fac in request.Facilities)
                {
                    _context.Facilities.Add(new Facility
                    {
                        Id = Guid.NewGuid(),
                        Type = fac.Type,
                        Sanctioned = fac.Sanctioned,
                        Balance = fac.Balance,
                        Headroom = fac.Headroom,
                        DeferralId = id
                    });
                }
            }

            // Handle selected documents update (for removing documents during resubmission)
            if (request.SelectedDocuments != null)
            {
                _logger.LogWarning($"[DEFERRAL-DELETE-RECV] Received {request.SelectedDocuments.Count} selectedDocuments from frontend");
                foreach (var doc in request.SelectedDocuments)
                {
                    _logger.LogWarning($"[DEFERRAL-DELETE-RECV]   - Name: '{doc.Name}', Type: '{doc.Type}'");
                }

                // selectedDocuments from frontend contains the documents the user wants to KEEP
                // Documents that are NOT in this list should be removed from DeferralDocuments table
                var selectedDocumentNames = request.SelectedDocuments
                    .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                    .Select(d => d.Name!.Trim().ToLowerInvariant())
                    .ToHashSet();

                _logger.LogWarning($"[DEFERRAL-DELETE-NORM] Normalized names to KEEP ({selectedDocumentNames.Count}): {string.Join(" | ", selectedDocumentNames)}");
                _logger.LogWarning($"[DEFERRAL-DELETE-DB] Current DB has {deferral.Documents.Count} documents:");
                foreach (var dbDoc in deferral.Documents)
                {
                    var normName = (dbDoc.Name ?? "").Trim().ToLowerInvariant();
                    var shouldKeep = selectedDocumentNames.Contains(normName);
                    _logger.LogWarning($"[DEFERRAL-DELETE-DB]   - '{dbDoc.Name}' (normalized: '{normName}') -> {(shouldKeep ? "KEEP" : "REMOVE")}");
                }

                // Remove DeferralDocuments that are no longer in selectedDocuments (user deleted them)
                // Do NOT remove already-uploaded documents (those with a non-empty Url) unless
                // the client explicitly indicates deletion. This preserves files in the
                // Mandatory DCL / Additional sections after resubmission.
                var documentsToRemove = deferral.Documents
                    .Where(d => string.IsNullOrWhiteSpace(d.Url) && !selectedDocumentNames.Contains((d.Name ?? "").Trim().ToLowerInvariant()))
                    .ToList();

                _logger.LogWarning($"[DEFERRAL-DELETE-ACTION] Will remove {documentsToRemove.Count} of {deferral.Documents.Count} documents");
                foreach (var docToRemove in documentsToRemove)
                {
                    _logger.LogWarning($"[DEFERRAL-DELETE-ACTION]   Removing: '{docToRemove.Name}'");
                }

                if (documentsToRemove.Count > 0)
                {
                    _logger.LogWarning($"[DEFERRAL-DELETE-ACTION] Actually calling RemoveRange for {documentsToRemove.Count} documents");
                    _context.DeferralDocuments.RemoveRange(documentsToRemove);
                }
                else
                {
                    _logger.LogWarning($"[DEFERRAL-DELETE-ACTION] No documents matched for removal");
                }

                // Update per-document metadata (DaysSought, NextDocumentDueDate)
                foreach (var selectedDoc in request.SelectedDocuments)
                {
                    if (string.IsNullOrWhiteSpace(selectedDoc.Name)) continue;

                    var selectedDocNameLower = selectedDoc.Name.Trim().ToLowerInvariant();
                    var existingDoc = deferral.Documents.FirstOrDefault(d =>
                        (d.Name ?? "").Trim().ToLowerInvariant() == selectedDocNameLower);
                    if (existingDoc != null)
                    {
                        existingDoc.DaysSought = selectedDoc.DaysSought;
                        existingDoc.NextDocumentDueDate = selectedDoc.NextDocumentDueDate;
                        _logger.LogWarning($"[DEFERRAL-DELETE-META] Updated metadata for '{existingDoc.Name}'");
                    }
                }

                // Persist selected documents as JSON for later retrieval
                try
                {
                    deferral.SelectedDocumentsJson = JsonSerializer.Serialize(request.SelectedDocuments);
                    _logger.LogWarning($"[DEFERRAL-DELETE-JSON] Serialized {request.SelectedDocuments.Count} selected documents to JSON");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DEFERRAL-DELETE-JSON] Failed to serialize selected documents");
                }

                // Update main deferral DaysSought field to match the maximum DaysSought from selected documents
                // This ensures the deferral.DaysSought field is valid for extension submissions
                var maxDaysSought = request.SelectedDocuments
                    .Where(d => d.DaysSought.HasValue && d.DaysSought > 0)
                    .Select(d => d.DaysSought ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();

                if (maxDaysSought > 0)
                {
                    deferral.DaysSought = maxDaysSought;
                    _logger.LogWarning($"[DEFERRAL-DAYSSOUGHT] Updated deferral.DaysSought to {maxDaysSought} (max from {request.SelectedDocuments.Count} selected documents)");
                }
                else
                {
                    _logger.LogWarning($"[DEFERRAL-DAYSSOUGHT] No valid DaysSought found in selected documents, keeping current value: {deferral.DaysSought}");
                }
            }


            var requestedApprovers = request.ApproverFlow ?? request.Approvers;
            Guid? previousFirstApproverUserId = null;
            Guid? previousCurrentApproverUserId = null;
            Guid? updatedFirstApproverUserId = null;
            Guid? currentPendingApproverUserId = null;
            string? currentPendingApproverRole = null;
            var approverFlowUpdated = false;
            var previousApproverSnapshot = new List<ApproverFlowParticipant>();
            var updatedApproverSnapshot = new List<ApproverFlowParticipant>();
            if (requestedApprovers != null && requestedApprovers.Count > 0)
            {
                approverFlowUpdated = true;
                var orderedExistingApprovers = OrderApproversForFlow(deferral.Approvers);
                previousFirstApproverUserId = orderedExistingApprovers.FirstOrDefault()?.UserId;
                previousCurrentApproverUserId = orderedExistingApprovers
                    .ElementAtOrDefault(GetSafeCurrentApproverIndex(deferral.CurrentApproverIndex, orderedExistingApprovers.Count))
                    ?.UserId;
                previousApproverSnapshot = orderedExistingApprovers
                    .Select(a => new ApproverFlowParticipant(a.UserId, a.Name, a.Role, a.Approved))
                    .ToList();
                var approvedExistingApprovers = orderedExistingApprovers
                    .Select((approver, index) => new { approver, index })
                    .Where(x => x.approver.Approved)
                    .ToList();

                updatedFirstApproverUserId = TryGetGuidFromClientValue(requestedApprovers[0].UserId)
                    ?? TryGetGuidFromClientValue(requestedApprovers[0].User);

                foreach (var approved in approvedExistingApprovers)
                {
                    if (approved.index >= requestedApprovers.Count)
                    {
                        return BadRequest(new { error = "Approved approvers cannot be removed or reordered" });
                    }

                    var requestedApprovedUserId = TryGetGuidFromClientValue(requestedApprovers[approved.index].UserId)
                        ?? TryGetGuidFromClientValue(requestedApprovers[approved.index].User);

                    if (requestedApprovedUserId != approved.approver.UserId)
                    {
                        return BadRequest(new { error = "Any approver who has already approved cannot be changed" });
                    }
                }

                _context.Approvers.RemoveRange(deferral.Approvers);

                var firstPendingApproverIndex = -1;

                for (var approverIndex = 0; approverIndex < requestedApprovers.Count; approverIndex++)
                {
                    var approverReq = requestedApprovers[approverIndex];
                    var approverUserId = TryGetGuidFromClientValue(approverReq.UserId)
                        ?? TryGetGuidFromClientValue(approverReq.User);

                    var existingApprovedAtIndex = approvedExistingApprovers.FirstOrDefault(x => x.index == approverIndex);
                    var shouldRemainApproved = existingApprovedAtIndex != null
                        && existingApprovedAtIndex.approver.UserId == approverUserId;

                    if (!shouldRemainApproved && firstPendingApproverIndex < 0)
                    {
                        firstPendingApproverIndex = approverIndex;
                    }

                    _context.Approvers.Add(new Approver
                    {
                        Id = CreateOrderedApproverId(id, approverIndex),
                        UserId = approverUserId,
                        Name = approverReq.Name,
                        Role = approverReq.Role,
                        Approved = shouldRemainApproved,
                        ApprovedAt = shouldRemainApproved ? existingApprovedAtIndex!.approver.ApprovedAt : null,
                        DeferralId = id
                    });
                }

                deferral.CurrentApproverIndex = firstPendingApproverIndex >= 0
                    ? firstPendingApproverIndex
                    : Math.Max(requestedApprovers.Count - 1, 0);

                if (deferral.CurrentApproverIndex >= 0 && deferral.CurrentApproverIndex < requestedApprovers.Count)
                {
                    var pendingApproverRequest = requestedApprovers[deferral.CurrentApproverIndex];
                    currentPendingApproverUserId = TryGetGuidFromClientValue(pendingApproverRequest.UserId)
                        ?? TryGetGuidFromClientValue(pendingApproverRequest.User);
                    currentPendingApproverRole = pendingApproverRequest.Role;
                }

                updatedApproverSnapshot = requestedApprovers
                    .Select(a => new ApproverFlowParticipant(
                        TryGetGuidFromClientValue(a.UserId) ?? TryGetGuidFromClientValue(a.User),
                        a.Name,
                        a.Role,
                        false
                    ))
                    .ToList();

                var storeForStatus = ParseDeferralCommentStore(deferral.ReworkComments);
                if (
                    deferral.Status == DeferralStatus.ReturnedForRework
                    && string.Equals(storeForStatus.LastReturnedByRole, "creator", StringComparison.OrdinalIgnoreCase)
                    && firstPendingApproverIndex >= 0
                )
                {
                    deferral.Status = DeferralStatus.Pending;
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ResubmissionComments))
            {
                var resubmissionComment = request.ResubmissionComments.Trim();
                var actorName = User.FindFirst("name")?.Value
                    ?? User.FindFirst(ClaimTypes.Name)?.Value;
                var actorRole = User.FindFirst("role")?.Value
                    ?? User.FindFirst(ClaimTypes.Role)?.Value;

                if (string.IsNullOrWhiteSpace(actorName) || string.IsNullOrWhiteSpace(actorRole))
                {
                    var actorUserIdText = User.FindFirst("id")?.Value;
                    if (Guid.TryParse(actorUserIdText, out var actorUserId))
                    {
                        var actorUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == actorUserId);
                        actorName ??= actorUser?.Name;
                        actorRole ??= actorUser?.Role.ToString();
                    }
                }

                actorName = string.IsNullOrWhiteSpace(actorName) ? "RM" : actorName.Trim();
                actorRole = string.IsNullOrWhiteSpace(actorRole) ? "RM" : actorRole.Trim();

                var store = ParseDeferralCommentStore(deferral.ReworkComments);
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = resubmissionComment,
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = actorName,
                    AuthorRole = actorRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = actorName,
                        Role = actorRole,
                    }
                });
                store.RmReason ??= resubmissionComment;
                deferral.ReworkComments = SerializeDeferralCommentStore(store);
            }

            // Reset approval statuses on resubmission UNLESS the approver flow was updated
            // (when approver flow is updated we preserve any already-approved approvers)
            if (request.Status == DeferralStatus.Pending)
            {
                deferral.Status = DeferralStatus.Pending;

                if (!approverFlowUpdated)
                {
                    deferral.CurrentApproverIndex = 0;

                    foreach (var approver in deferral.Approvers)
                    {
                        approver.Approved = false;
                        approver.ApprovedAt = null;
                    }
                }
                // if approverFlowUpdated == true, approved flags and CurrentApproverIndex
                // were already set above when rebuilding the approver list, so preserve them
            }

            if (approverFlowUpdated)
            {
                await AppendApproverFlowChangeAuditAndNotificationsAsync(
                    deferral,
                    previousApproverSnapshot,
                    updatedApproverSnapshot,
                    previousCurrentApproverUserId,
                    currentPendingApproverUserId,
                    currentPendingApproverRole
                );
            }

            deferral.UpdatedAt = DateTime.UtcNow;

            // Log state before SaveChanges
            _logger.LogWarning($"[DEFERRAL-DELETE-SAVE] Before SaveChanges: Deferral has {deferral.Documents.Count} documents");
            foreach (var doc in deferral.Documents)
            {
                _logger.LogWarning($"[DEFERRAL-DELETE-SAVE]   - '{doc.Name}' (EntityState: {_context.Entry(doc).State})");
            }

            await _context.SaveChangesAsync();

            _logger.LogWarning($"[DEFERRAL-DELETE-SAVE] After SaveChanges: Deferral has {deferral.Documents.Count} documents");
            foreach (var doc in deferral.Documents)
            {
                _logger.LogWarning($"[DEFERRAL-DELETE-SAVE]   - '{doc.Name}' (EntityState: {_context.Entry(doc).State})");
            }

            if (
                previousFirstApproverUserId.HasValue
                && updatedFirstApproverUserId.HasValue
                && previousFirstApproverUserId.Value != updatedFirstApproverUserId.Value
            )
            {
                try
                {
                    var previousFirstApprover = await _context.Users
                        .FirstOrDefaultAsync(u => u.Id == previousFirstApproverUserId.Value);
                    var updatedFirstApprover = await _context.Users
                        .FirstOrDefaultAsync(u => u.Id == updatedFirstApproverUserId.Value);

                    var deferralNumber = string.IsNullOrWhiteSpace(deferral.DeferralNumber) ? "DEFERRAL" : deferral.DeferralNumber;
                    var customerName = string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName;
                    var previousName = string.IsNullOrWhiteSpace(previousFirstApprover?.Name) ? "Approver" : previousFirstApprover!.Name;
                    var updatedName = string.IsNullOrWhiteSpace(updatedFirstApprover?.Name) ? "Approver" : updatedFirstApprover!.Name;

                    if (!string.IsNullOrWhiteSpace(previousFirstApprover?.Email))
                    {
                        await _emailService.SendFirstApproverReplacedAsync(
                            previousFirstApprover.Email,
                            previousName,
                            deferralNumber,
                            customerName,
                            updatedName
                        );
                    }

                    if (!string.IsNullOrWhiteSpace(updatedFirstApprover?.Email))
                    {
                        await _emailService.SendFirstApproverAssignedAsync(
                            updatedFirstApprover.Email,
                            updatedName,
                            deferralNumber,
                            customerName,
                            previousName
                        );
                    }
                }
                catch (Exception emailEx)
                {
                    _logger.LogWarning(emailEx,
                        "⚠️ Approval flow updated for deferral {DeferralNumber}, but failed to send first approver replacement notifications",
                        deferral.DeferralNumber);
                }
            }

            var pendingApproverAlreadyNotifiedAsFirstReplacement =
                updatedFirstApproverUserId.HasValue
                && currentPendingApproverUserId.HasValue
                && updatedFirstApproverUserId.Value == currentPendingApproverUserId.Value
                && previousFirstApproverUserId.HasValue
                && updatedFirstApproverUserId.Value != previousFirstApproverUserId.Value;

            if (approverFlowUpdated && currentPendingApproverUserId.HasValue && !pendingApproverAlreadyNotifiedAsFirstReplacement)
            {
                try
                {
                    var pendingApproverUser = await _context.Users
                        .FirstOrDefaultAsync(u => u.Id == currentPendingApproverUserId.Value);

                    if (!string.IsNullOrWhiteSpace(pendingApproverUser?.Email))
                    {
                        var pendingApproverName = string.IsNullOrWhiteSpace(pendingApproverUser.Name)
                            ? "Approver"
                            : pendingApproverUser.Name;

                        var pendingApproverRole = string.IsNullOrWhiteSpace(currentPendingApproverRole)
                            ? "Approver"
                            : currentPendingApproverRole;

                        await _emailService.SendDeferralSubmittedAsync(
                            pendingApproverUser.Email,
                            pendingApproverName,
                            string.IsNullOrWhiteSpace(deferral.DeferralNumber) ? "DEFERRAL" : deferral.DeferralNumber,
                            string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                            deferral.DaysSought,
                            pendingApproverRole
                        );
                    }
                }
                catch (Exception pendingApproverEmailEx)
                {
                    _logger.LogWarning(
                        pendingApproverEmailEx,
                        "⚠️ Approval flow updated for deferral {DeferralNumber}, but failed to notify pending approver",
                        deferral.DeferralNumber
                    );
                }
            }

            HydrateDeferralComments(deferral);

            // Reload deferral with documents to ensure they're fresh after any removals
            var refreshedDeferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (refreshedDeferral != null)
            {
                ApplyApproverOrdering(refreshedDeferral);
                HydrateDeferralComments(refreshedDeferral);
                DeserializeSelectedDocuments(refreshedDeferral);
                return Ok(new { success = true, deferral = refreshedDeferral });
            }

            return Ok(new { success = true, deferral });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error updating deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // DOCUMENT OPERATIONS
    // ============================================

    [HttpPost("{id}/documents")]
    public async Task<IActionResult> AddDocument(Guid id, [FromBody] AddDeferralDocumentRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var document = new DeferralDocument
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Url = DecorateDocumentUrl(request.Url, request.IsDCL, request.IsAdditional),
                UploadedById = userId,
                DeferralId = id
            };

            _context.DeferralDocuments.Add(document);
            await _context.SaveChangesAsync();

            return StatusCode(201, new { message = "Document added", document });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error adding document");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/documents/upload")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> UploadDocument(Guid id, IFormFile file, [FromForm] bool? isDCL, [FromForm] bool? isAdditional, [FromForm] string? documentName)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file uploaded" });
            }

            var deferralExists = await _context.Deferrals.AnyAsync(d => d.Id == id);
            if (!deferralExists)
            {
                return NotFound(new { message = "Deferral not found" });
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
            var savedFileName = await FileUploadHelper.SaveFileAsync(file, uploadsPath);
            var publicUrl = $"/uploads/{savedFileName}";

            var document = new DeferralDocument
            {
                Id = Guid.NewGuid(),
                Name = file.FileName,
                Url = DecorateDocumentUrl(publicUrl, isDCL, isAdditional, documentName),
                UploadedById = userId,
                DeferralId = id
            };

            _context.DeferralDocuments.Add(document);
            await _context.SaveChangesAsync();

            return StatusCode(201, new
            {
                success = true,
                message = "Document uploaded",
                document
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error uploading deferral document");
            return StatusCode(500, new { message = "Failed to upload document" });
        }
    }

    [HttpDelete("{id}/documents/{docId}")]
    public async Task<IActionResult> DeleteDocument(Guid id, Guid docId)
    {
        try
        {
            var document = await _context.DeferralDocuments
                .FirstOrDefaultAsync(d => d.Id == docId && d.DeferralId == id);

            if (document == null)
                return NotFound(new { error = "Document not found" });

            _context.DeferralDocuments.Remove(document);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Document deleted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error deleting document");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // APPROVERS OPERATIONS
    // ============================================

    [HttpPut("{id}/approvers")]
    public async Task<IActionResult> SetApprovers(Guid id, [FromBody] List<ApproverRequest> approvers)
    {
        try
        {
            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var orderedExistingApprovers = OrderApproversForFlow(deferral.Approvers);
            var previousApproverSnapshot = orderedExistingApprovers
                .Select(a => new ApproverFlowParticipant(a.UserId, a.Name, a.Role, a.Approved))
                .ToList();
            var previousCurrentApproverUserId = orderedExistingApprovers
                .ElementAtOrDefault(GetSafeCurrentApproverIndex(deferral.CurrentApproverIndex, orderedExistingApprovers.Count))
                ?.UserId;
            var approvedExistingApprovers = orderedExistingApprovers
                .Select((approver, index) => new { approver, index })
                .Where(x => x.approver.Approved)
                .ToList();

            foreach (var approved in approvedExistingApprovers)
            {
                if (approved.index >= approvers.Count)
                {
                    return BadRequest(new { error = "Approved approvers cannot be removed or reordered" });
                }

                var requestedApprovedUserId = approvers[approved.index].UserId ?? approvers[approved.index].User;
                if (requestedApprovedUserId != approved.approver.UserId)
                {
                    return BadRequest(new { error = "Any approver who has already approved cannot be changed" });
                }
            }

            _context.Approvers.RemoveRange(deferral.Approvers);

            var firstPendingApproverIndex = -1;
            Guid? currentPendingApproverUserId = null;
            string? currentPendingApproverRole = null;

            for (var approverIndex = 0; approverIndex < approvers.Count; approverIndex++)
            {
                var approverReq = approvers[approverIndex];
                var approverUserId = approverReq.UserId ?? approverReq.User;
                var existingApprovedAtIndex = approvedExistingApprovers.FirstOrDefault(x => x.index == approverIndex);
                var shouldRemainApproved = existingApprovedAtIndex != null
                    && existingApprovedAtIndex.approver.UserId == approverUserId;

                if (!shouldRemainApproved && firstPendingApproverIndex < 0)
                {
                    firstPendingApproverIndex = approverIndex;
                    currentPendingApproverUserId = approverUserId;
                    currentPendingApproverRole = approverReq.Role;
                }

                var approver = new Approver
                {
                    Id = CreateOrderedApproverId(id, approverIndex),
                    UserId = approverUserId,
                    Name = approverReq.Name,
                    Role = approverReq.Role,
                    Approved = shouldRemainApproved,
                    ApprovedAt = shouldRemainApproved ? existingApprovedAtIndex!.approver.ApprovedAt : null,
                    DeferralId = id
                };
                _context.Approvers.Add(approver);
            }

            deferral.CurrentApproverIndex = firstPendingApproverIndex >= 0
                ? firstPendingApproverIndex
                : Math.Max(approvers.Count - 1, 0);
            deferral.UpdatedAt = DateTime.UtcNow;

            var updatedApproverSnapshot = approvers
                .Select(a => new ApproverFlowParticipant(a.UserId ?? a.User, a.Name, a.Role, false))
                .ToList();

            await AppendApproverFlowChangeAuditAndNotificationsAsync(
                deferral,
                previousApproverSnapshot,
                updatedApproverSnapshot,
                previousCurrentApproverUserId,
                currentPendingApproverUserId,
                currentPendingApproverRole
            );

            await _context.SaveChangesAsync();

            var refreshedDeferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (refreshedDeferral != null)
            {
                ApplyApproverOrdering(refreshedDeferral);
                HydrateDeferralComments(refreshedDeferral);
                DeserializeSelectedDocuments(refreshedDeferral);
                return Ok(new { message = "Approvers set", deferral = refreshedDeferral });
            }

            return Ok(new { message = "Approvers set" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error setting approvers");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpDelete("{id}/approvers/{index}")]
    public async Task<IActionResult> RemoveApprover(Guid id, int index)
    {
        try
        {
            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            if (index < 0 || index >= deferral.Approvers.Count)
                return BadRequest(new { error = "Invalid approver index" });

            var approverToRemove = deferral.Approvers.ElementAt(index);
            _context.Approvers.Remove(approverToRemove);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Approver removed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error removing approver");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // APPROVAL OPERATIONS
    // ============================================

    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveDeferral(Guid id, [FromBody] ApprovalRequest? request = null)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var orderedApprovers = OrderApproversForFlow(deferral.Approvers);
            var currentIndex = GetSafeCurrentApproverIndex(deferral.CurrentApproverIndex, orderedApprovers.Count);
            var currentApprover = orderedApprovers.ElementAtOrDefault(currentIndex);
            if (currentApprover == null || currentApprover.UserId != userId)
                return StatusCode(403, new { error = "Only current approver can take this action" });

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                var approvalComment = request.Comment.Trim();
                var approverName =
                    currentApprover.Name
                    ?? currentApprover.User?.Name
                    ?? currentApprover.User?.Email
                    ?? "Approver";
                var approverRole =
                    currentApprover.Role
                    ?? currentApprover.User?.Role.ToString()
                    ?? "Approver";

                var store = ParseDeferralCommentStore(deferral.ReworkComments);
                store.Comments ??= new List<DeferralCommentEntry>();
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = approvalComment,
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = approverName,
                    AuthorRole = approverRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = approverName,
                        Role = approverRole,
                    }
                });

                deferral.ReworkComments = SerializeDeferralCommentStore(store);
            }

            currentApprover.Approved = true;
            currentApprover.ApprovedAt = DateTime.UtcNow;

            var hasNextApprover = currentIndex + 1 < orderedApprovers.Count;
            var nextApprover = hasNextApprover ? orderedApprovers[currentIndex + 1] : null;

            if (hasNextApprover)
            {
                deferral.CurrentApproverIndex = currentIndex + 1;
                deferral.Status = DeferralStatus.InReview;
            }
            else
            {
                deferral.Status = DeferralStatus.Pending;
            }

            await _context.SaveChangesAsync();

            var approvalEmailAttempted = false;
            var approvalEmailSent = false;
            var nextApproverEmailAttempted = false;
            var nextApproverEmailSent = false;

            try
            {
                var approverEmail = currentApprover.User?.Email;
                if (!string.IsNullOrWhiteSpace(approverEmail))
                {
                    approvalEmailAttempted = true;

                    var approverName =
                        currentApprover.Name
                        ?? currentApprover.User?.Name
                        ?? "Approver";

                    var nextApproverName =
                        nextApprover?.Name
                        ?? nextApprover?.User?.Name
                        ?? nextApprover?.User?.Email;

                    await _emailService.SendDeferralApprovalConfirmationAsync(
                        approverEmail,
                        approverName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        nextApproverName,
                        isFinalApproval: !hasNextApprover
                    );

                    approvalEmailSent = true;
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "⚠️ Deferral approved but failed to send approval confirmation email for {DeferralNumber}", deferral.DeferralNumber);
            }

            try
            {
                if (hasNextApprover)
                {
                    var nextApproverEmail = nextApprover?.User?.Email;
                    if (!string.IsNullOrWhiteSpace(nextApproverEmail))
                    {
                        nextApproverEmailAttempted = true;

                        var nextApproverName =
                            nextApprover?.Name
                            ?? nextApprover?.User?.Name
                            ?? "Approver";

                        var nextApproverRole = string.IsNullOrWhiteSpace(nextApprover?.Role)
                            ? "Approver"
                            : nextApprover!.Role!;

                        await _emailService.SendDeferralSubmittedAsync(
                            nextApproverEmail,
                            nextApproverName,
                            deferral.DeferralNumber,
                            string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                            deferral.DaysSought,
                            nextApproverRole
                        );

                        nextApproverEmailSent = true;
                    }
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "⚠️ Deferral approved but failed to notify next approver for {DeferralNumber}", deferral.DeferralNumber);
            }

            // Notify RM that an approver has approved and where the deferral moved next
            try
            {
                var rm = deferral.CreatedBy;
                if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                {
                    var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                    await _emailService.SendDeferralApprovalConfirmationAsync(
                        rm.Email,
                        rmName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        nextApprover?.Name ?? nextApprover?.User?.Name,
                        !hasNextApprover
                    );
                }
            }
            catch (Exception rmEmailEx)
            {
                _logger.LogWarning(rmEmailEx, "⚠️ Deferral approved but failed to notify RM for {DeferralNumber}", deferral.DeferralNumber);
            }



            _logger.LogInformation($"✅ Deferral {deferral.DeferralNumber} approved by {userId}");

            return Ok(new
            {
                message = "Approved successfully",
                status = deferral.Status.ToString(),
                emailAttempted = approvalEmailAttempted,
                emailSent = approvalEmailSent,
                nextApproverEmailAttempted,
                nextApproverEmailSent,
                movedToNextApprover = hasNextApprover,
                nextApprover = hasNextApprover
                    ? new
                    {
                        name = nextApprover?.Name ?? nextApprover?.User?.Name,
                        email = nextApprover?.User?.Email
                    }
                    : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error approving deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/approve-creator")]
    public async Task<IActionResult> ApproveByCreator(Guid id, [FromBody] ApprovalRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var userRole = (User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty).Trim();

            var deferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var normalizedRole = userRole.ToLowerInvariant();
            var isCreatorRole =
                normalizedRole == "creator" ||
                normalizedRole == "cocreator" ||
                normalizedRole == "co_creator" ||
                normalizedRole == "admin";

            if (!isCreatorRole)
                return StatusCode(403, new { error = "Only creator can approve" });

            var hasApprovers = deferral.Approvers.Any();
            var allApproversApproved = hasApprovers && deferral.Approvers.All(a => a.Approved);
            if (!allApproversApproved)
                return BadRequest(new { error = "Creator can approve only after all approvers have approved" });

            deferral.Status = DeferralStatus.PartiallyApproved;
            deferral.UpdatedAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                var store = ParseDeferralCommentStore(deferral.ReworkComments);
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = request.Comment.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = User.FindFirst(ClaimTypes.Name)?.Value,
                    AuthorRole = userRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = User.FindFirst(ClaimTypes.Name)?.Value,
                        Role = userRole,
                    }
                });

                deferral.ReworkComments = SerializeDeferralCommentStore(store);
            }

            {
                var store = ParseDeferralCommentStore(deferral.ReworkComments);
                if (string.Equals(store.LastReturnedByRole, "creator", StringComparison.OrdinalIgnoreCase))
                {
                    store.LastReturnedByRole = null;
                    deferral.ReworkComments = SerializeDeferralCommentStore(store);
                }
            }

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            // Notify RM that creator approved
            try
            {
                var rm = deferral.CreatedBy ?? await _context.Users.FindAsync(deferral.CreatedById);
                if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                {
                    var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                    await _emailService.SendDeferralApprovalConfirmationAsync(
                        rm.Email,
                        rmName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        null,
                        false);
                }
            }
            catch (Exception rmNotifyEx)
            {
                _logger.LogWarning(rmNotifyEx, "⚠️ Failed to notify RM after creator approval for {DeferralNumber}", deferral.DeferralNumber);
            }

            return Ok(new
            {
                message = "Approved by creator",
                deferral,
                creatorApproved = true,
                status = deferral.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error in creator approval");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/approve-checker")]
    [HttpPut("{id}/approve-by-checker")]
    public async Task<IActionResult> ApproveByChecker(Guid id, [FromBody] ApprovalRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var userRole = (User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty).Trim();

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var normalizedRole = userRole.ToLowerInvariant();
            var isCheckerRole =
                normalizedRole == "checker" ||
                normalizedRole == "cochecker" ||
                normalizedRole == "co_checker" ||
                normalizedRole == "admin";

            if (!isCheckerRole)
                return StatusCode(403, new { error = "Only checker can approve" });

            var hasApprovers = deferral.Approvers.Any();
            var allApproversApproved = hasApprovers && deferral.Approvers.All(a => a.Approved);
            if (!allApproversApproved)
                return BadRequest(new { error = "Checker can approve only after all approvers have approved" });

            deferral.Status = DeferralStatus.Approved;
            deferral.UpdatedAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                var store = ParseDeferralCommentStore(deferral.ReworkComments);
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = request.Comment.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = User.FindFirst(ClaimTypes.Name)?.Value,
                    AuthorRole = userRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = User.FindFirst(ClaimTypes.Name)?.Value,
                        Role = userRole,
                    }
                });

                deferral.ReworkComments = SerializeDeferralCommentStore(store);
            }

            {
                var store = ParseDeferralCommentStore(deferral.ReworkComments);
                if (string.Equals(store.LastReturnedByRole, "checker", StringComparison.OrdinalIgnoreCase))
                {
                    store.LastReturnedByRole = null;
                    deferral.ReworkComments = SerializeDeferralCommentStore(store);
                }
            }

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            // Notify RM that checker approved (final approval)
            try
            {
                var rm = deferral.CreatedBy ?? await _context.Users.FindAsync(deferral.CreatedById);
                if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                {
                    var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                    await _emailService.SendDeferralApprovalConfirmationAsync(
                        rm.Email,
                        rmName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        null,
                        true);
                }
            }
            catch (Exception rmNotifyEx)
            {
                _logger.LogWarning(rmNotifyEx, "⚠️ Failed to notify RM after checker approval for {DeferralNumber}", deferral.DeferralNumber);
            }

            return Ok(new
            {
                message = "Approved by checker",
                deferral,
                checkerApproved = true,
                status = deferral.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error in checker approval");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/reminder")]
    public async Task<IActionResult> SendReminder(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var orderedApprovers = OrderApproversForFlow(deferral.Approvers);
            var currentIndex = GetSafeCurrentApproverIndex(deferral.CurrentApproverIndex, orderedApprovers.Count);
            var currentApprover = orderedApprovers.ElementAtOrDefault(currentIndex);

            if (currentApprover == null || currentApprover.UserId == null)
                return BadRequest(new { error = "No current approver found for reminder" });

            var canSendReminder =
                deferral.CreatedById == userId ||
                currentApprover.UserId == userId ||
                orderedApprovers.Any(a => a.UserId == userId);

            if (!canSendReminder)
                return StatusCode(403, new { error = "Not authorized to send reminder for this deferral" });

            var reminderMessage = $"Reminder: Deferral {deferral.DeferralNumber} is awaiting your approval.";

            _context.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = currentApprover.UserId,
                Message = reminderMessage,
                Read = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var reminderEmailAttempted = false;
            var reminderEmailSent = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(currentApprover.User?.Email))
                {
                    reminderEmailAttempted = true;
                    await _emailService.SendDeferralReminderAsync(
                        currentApprover.User.Email,
                        string.IsNullOrWhiteSpace(currentApprover.Name) ? "Approver" : currentApprover.Name,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName
                    );
                    reminderEmailSent = true;
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "⚠️ Reminder in-app notification saved, but email failed for deferral {DeferralNumber}", deferral.DeferralNumber);
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                message = "Reminder sent",
                userId = currentApprover.UserId,
                email = currentApprover.User?.Email,
                emailAttempted = reminderEmailAttempted,
                emailSent = reminderEmailSent,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error sending reminder");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] DeferralCommentRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return BadRequest(new { error = "Comment text is required" });

            var deferral = await _context.Deferrals.FirstOrDefaultAsync(d => d.Id == id);
            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var store = ParseDeferralCommentStore(deferral.ReworkComments);
            var comment = new DeferralCommentEntry
            {
                Text = request.Text.Trim(),
                CreatedAt = request.CreatedAt ?? DateTime.UtcNow,
                AuthorName = request.AuthorName ?? request.Author?.Name,
                AuthorRole = request.AuthorRole ?? request.Author?.Role,
                Author = new DeferralCommentAuthor
                {
                    Name = request.Author?.Name ?? request.AuthorName,
                    Role = request.Author?.Role ?? request.AuthorRole,
                }
            };

            store.Comments.Add(comment);
            store.RmReason ??= comment.Text;

            deferral.ReworkComments = SerializeDeferralCommentStore(store);
            deferral.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            HydrateDeferralComments(deferral);

            return StatusCode(201, new { success = true, comment, comments = deferral.Comments });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error adding deferral comment");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // EXTENSION OPERATIONS
    // ============================================

    [HttpPost("{id}/extensions")]
    public async Task<IActionResult> SubmitExtension(Guid id, [FromBody] SubmitExtensionRequest request)
    {
        try
        {
            _logger.LogInformation("📥 [SubmitExtension] Received request for deferral {DeferralId}", id);

            if (request == null)
            {
                _logger.LogWarning("⚠️ [SubmitExtension] Request body is null");
                return BadRequest(new { error = "Request body is required" });
            }

            _logger.LogInformation("📦 [SubmitExtension] Request properties: ExtensionDaysByDocument={DocCount}, Comment={CommentLen}, FileUrls={FileCount}",
                request.ExtensionDaysByDocument?.Count ?? 0,
                request.Comment?.Length ?? 0,
                request.FileUrls?.Count ?? 0);

            if (request.ExtensionDaysByDocument != null)
            {
                foreach (var kvp in request.ExtensionDaysByDocument)
                {
                    _logger.LogInformation("[SubmitExtension] Doc: '{DocKey}' = {Days} days", kvp.Key, kvp.Value);
                }
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
            {
                _logger.LogWarning("⚠️ [SubmitExtension] Deferral not found: {DeferralId}", id);
                return NotFound(new { error = "Deferral not found" });
            }

            // Validate that the current user is the RM (created the deferral)
            if (deferral.CreatedById != userId)
            {
                _logger.LogWarning("⚠️ [SubmitExtension] Unauthorized RM: {UserId}, DeferralCreatedBy: {CreatedById}", userId, deferral.CreatedById);
                return StatusCode(403, new { error = "Only the RM who created the deferral can submit extensions" });
            }

            if (request?.ExtensionDaysByDocument == null || request.ExtensionDaysByDocument.Count == 0)
            {
                _logger.LogWarning("⚠️ [SubmitExtension] No extension days provided");
                return BadRequest(new { error = "At least one document extension is required" });
            }

            // Calculate total requested days (max per document)
            var totalDays = request.ExtensionDaysByDocument.Values.DefaultIfEmpty(0).Max();

            // Create extension record
            var extension = new Extension
            {
                Id = Guid.NewGuid(),
                DeferralId = deferral.Id,
                DeferralNumber = deferral.DeferralNumber,
                CustomerName = deferral.CustomerName,
                CustomerNumber = deferral.CustomerNumber,
                DclNumber = deferral.DclNumber,
                LoanAmount = deferral.LoanAmount,
                NextDueDate = deferral.NextDueDate,
                NextDocumentDueDate = deferral.NextDocumentDueDate,
                SlaExpiry = deferral.SlaExpiry,
                CurrentDaysSought = deferral.DaysSought,
                RequestedDaysSought = totalDays,
                ExtensionReason = request.Comment ?? "Extension requested",
                Status = ExtensionStatus.PendingApproval,
                RequestedById = userId,
                RequestedDate = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            // Add approvers (copy from deferral approvers)
            if (deferral.Approvers != null && deferral.Approvers.Count > 0)
            {
                var approvers = OrderApproversForFlow(deferral.Approvers);
                foreach (var approver in approvers)
                {
                    extension.Approvers.Add(new ExtensionApprover
                    {
                        Id = Guid.NewGuid(),
                        ExtensionId = extension.Id,
                        UserId = approver.UserId,
                        User = approver.User,
                        Role = approver.Role,
                        ApprovalStatus = ApproverApprovalStatus.Pending,
                    });
                }
            }

            _context.Extensions.Add(extension);
            await _context.SaveChangesAsync();

            // Update deferral to reflect extension request
            deferral.ExtensionStatus = extension.Status.ToString();
            deferral.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("✅ [SubmitExtension] Extension created successfully: {ExtensionId}", extension.Id);

            return StatusCode(201, new
            {
                success = true,
                extension = new
                {
                    id = extension.Id,
                    deferralId = extension.DeferralId,
                    deferralNumber = extension.DeferralNumber,
                    status = extension.Status.ToString(),
                    requestedDaysSought = extension.RequestedDaysSought,
                    extendedDaysByDoc = request.ExtensionDaysByDocument,
                    comment = request.Comment,
                    createdAt = extension.CreatedAt,
                },
                message = "Extension submitted successfully",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error submitting extension for deferral {DeferralId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // REJECTION & RETURN OPERATIONS
    // ============================================

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectDeferral(Guid id, [FromBody] RejectDeferralRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request?.Reason))
                return BadRequest(new { error = "Rejection reason is required" });

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            deferral.Status = DeferralStatus.Rejected;
            deferral.RejectionReason = request.Reason.Trim();

            var rejectingApprover = deferral.Approvers.FirstOrDefault(a => a.UserId == userId);
            var rejectingApproverName =
                rejectingApprover?.Name
                ?? rejectingApprover?.User?.Name
                ?? "Approver";
            var rejectingApproverEmail = rejectingApprover?.User?.Email;

            if (string.IsNullOrWhiteSpace(rejectingApproverEmail))
            {
                var rejectingUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                rejectingApproverEmail = rejectingUser?.Email;
                if (!string.IsNullOrWhiteSpace(rejectingUser?.Name))
                {
                    rejectingApproverName = rejectingUser.Name;
                }
            }

            await _context.SaveChangesAsync();

            var rmEmailAttempted = false;
            var rmEmailSent = false;
            var approverEmailAttempted = false;
            var approverEmailSent = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deferral.CreatedBy?.Email))
                {
                    rmEmailAttempted = true;
                    await _emailService.SendDeferralRejectedToRmAsync(
                        deferral.CreatedBy.Email,
                        string.IsNullOrWhiteSpace(deferral.CreatedBy.Name) ? "RM" : deferral.CreatedBy.Name,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        deferral.RejectionReason ?? "No reason provided",
                        rejectingApproverName
                    );
                    rmEmailSent = true;
                }
            }
            catch (Exception rmEmailEx)
            {
                _logger.LogWarning(rmEmailEx, "⚠️ Deferral rejected but failed to send rejection email to RM for {DeferralNumber}", deferral.DeferralNumber);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(rejectingApproverEmail))
                {
                    approverEmailAttempted = true;
                    await _emailService.SendDeferralRejectConfirmationAsync(
                        rejectingApproverEmail,
                        rejectingApproverName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        deferral.RejectionReason ?? "No reason provided"
                    );
                    approverEmailSent = true;
                }
            }
            catch (Exception approverEmailEx)
            {
                _logger.LogWarning(approverEmailEx, "⚠️ Deferral rejected but failed to send confirmation email to rejecting approver for {DeferralNumber}", deferral.DeferralNumber);
            }

            _logger.LogInformation($"❌ Deferral {deferral.DeferralNumber} rejected");

            return Ok(new
            {
                message = "Rejected successfully",
                rmEmailAttempted,
                rmEmailSent,
                approverEmailAttempted,
                approverEmailSent
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error rejecting deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/return-for-rework")]
    public async Task<IActionResult> ReturnForRework(Guid id, [FromBody] ReturnForReworkRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request?.ReworkComment))
                return BadRequest(new { error = "Rework reason is required" });

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var userRoleRaw = (
                User.FindFirst("role")?.Value
                ?? User.FindFirst(ClaimTypes.Role)?.Value
                ?? string.Empty
            ).Trim();
            var userRole = userRoleRaw.ToLowerInvariant();
            var returnedByRole =
                userRole == "checker" || userRole == "cochecker" || userRole == "co_checker"
                    ? "checker"
                    : userRole == "creator" || userRole == "cocreator" || userRole == "co_creator"
                        ? "creator"
                        : null;

            var deferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            if (string.Equals(returnedByRole, "creator", StringComparison.OrdinalIgnoreCase))
            {
                var hasApprovers = deferral.Approvers.Any();
                var allApproversApproved = hasApprovers && deferral.Approvers.All(a => a.Approved);
                if (!allApproversApproved)
                {
                    return BadRequest(new { error = "CoCreator can return for rework only after all approvers have approved" });
                }
            }

            deferral.Status = DeferralStatus.ReturnedForRework;

            var reworkComment = request.ReworkComment.Trim();

            var store = ParseDeferralCommentStore(deferral.ReworkComments);
            store.ReworkComment = reworkComment;
            if (!string.IsNullOrWhiteSpace(returnedByRole))
            {
                store.LastReturnedByRole = returnedByRole;
            }

            var returningApprover = deferral.Approvers.FirstOrDefault(a => a.UserId == userId);
            var returningApproverName =
                User.FindFirst("name")?.Value
                ?? User.FindFirst(ClaimTypes.Name)?.Value
                ?? returningApprover?.Name
                ?? returningApprover?.User?.Name;
            var returningApproverEmail =
                User.FindFirst(ClaimTypes.Email)?.Value
                ?? returningApprover?.User?.Email;

            if (string.IsNullOrWhiteSpace(returningApproverEmail))
            {
                var returningUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                returningApproverEmail = returningUser?.Email;
                if (!string.IsNullOrWhiteSpace(returningUser?.Name))
                {
                    returningApproverName = returningUser.Name;
                }

                userRoleRaw = string.IsNullOrWhiteSpace(userRoleRaw)
                    ? returningUser?.Role.ToString() ?? string.Empty
                    : userRoleRaw;
            }

            returningApproverName = string.IsNullOrWhiteSpace(returningApproverName) ? "User" : returningApproverName.Trim();

            var commentAuthorRole = string.IsNullOrWhiteSpace(userRoleRaw)
                ? string.Equals(returnedByRole, "creator", StringComparison.OrdinalIgnoreCase)
                    ? "CoCreator"
                    : string.Equals(returnedByRole, "checker", StringComparison.OrdinalIgnoreCase)
                        ? "CoChecker"
                        : "User"
                : userRoleRaw;

            store.Comments ??= new List<DeferralCommentEntry>();
            store.Comments.Add(new DeferralCommentEntry
            {
                Text = reworkComment,
                CreatedAt = DateTime.UtcNow,
                AuthorName = returningApproverName,
                AuthorRole = commentAuthorRole,
                Author = new DeferralCommentAuthor
                {
                    Name = returningApproverName,
                    Role = commentAuthorRole,
                }
            });

            deferral.ReworkComments = SerializeDeferralCommentStore(store);

            await _context.SaveChangesAsync();

            var rmEmailAttempted = false;
            var rmEmailSent = false;
            var approverEmailAttempted = false;
            var approverEmailSent = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deferral.CreatedBy?.Email))
                {
                    rmEmailAttempted = true;
                    await _emailService.SendDeferralReturnedToRmAsync(
                        deferral.CreatedBy.Email,
                        string.IsNullOrWhiteSpace(deferral.CreatedBy.Name) ? "RM" : deferral.CreatedBy.Name,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        reworkComment,
                        returningApproverName
                    );
                    rmEmailSent = true;
                }
            }
            catch (Exception rmEmailEx)
            {
                _logger.LogWarning(rmEmailEx, "⚠️ Deferral returned for rework but failed to send notification email to RM for {DeferralNumber}", deferral.DeferralNumber);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(returningApproverEmail))
                {
                    approverEmailAttempted = true;
                    await _emailService.SendDeferralReturnConfirmationAsync(
                        returningApproverEmail,
                        returningApproverName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        reworkComment
                    );
                    approverEmailSent = true;
                }
            }
            catch (Exception approverEmailEx)
            {
                _logger.LogWarning(approverEmailEx, "⚠️ Deferral returned for rework but failed to send confirmation email to returning approver for {DeferralNumber}", deferral.DeferralNumber);
            }

            _logger.LogInformation($"🔄 Deferral {deferral.DeferralNumber} returned for rework");

            return Ok(new
            {
                message = "Returned for rework",
                rmEmailAttempted,
                rmEmailSent,
                approverEmailAttempted,
                approverEmailSent
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error returning for rework");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/close")]
    public async Task<IActionResult> CloseDeferral(Guid id, [FromBody] CloseDeferralRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var userRole = (User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty).Trim();
            var normalizedRole = userRole.ToLowerInvariant();

            var canRequestClose =
                normalizedRole == "rm" ||
                normalizedRole == "relationshipmanager" ||
                normalizedRole == "admin";

            if (!canRequestClose)
                return StatusCode(403, new { error = "Only RM can submit close request" });

            var deferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            var isNewCloseRequest = deferral.Status == DeferralStatus.Approved;
            var isExistingPendingCloseRequest = deferral.Status == DeferralStatus.CloseRequested;

            if (!isNewCloseRequest && !isExistingPendingCloseRequest)
                return BadRequest(new { error = "Close request can only be submitted for approved deferrals or updated while pending creator approval" });

            deferral.Status = DeferralStatus.CloseRequested;
            deferral.ClosedReason = request.Reason?.Trim();
            deferral.UpdatedAt = DateTime.UtcNow;

            var actorName = User.FindFirst(ClaimTypes.Name)?.Value ?? "RM";
            var store = ParseDeferralCommentStore(deferral.ReworkComments);

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = request.Comment.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = actorName,
                    AuthorRole = userRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = actorName,
                        Role = userRole,
                    }
                });
            }

            if (request?.DocumentComments != null)
            {
                foreach (var item in request.DocumentComments.Where(i => !string.IsNullOrWhiteSpace(i?.Comment)))
                {
                    var docName = string.IsNullOrWhiteSpace(item!.DocumentName) ? "Document" : item.DocumentName!.Trim();
                    store.Comments.Add(new DeferralCommentEntry
                    {
                        Text = $"[Close Request] {docName}: {item.Comment!.Trim()}",
                        CreatedAt = DateTime.UtcNow,
                        AuthorName = actorName,
                        AuthorRole = userRole,
                        Author = new DeferralCommentAuthor
                        {
                            Name = actorName,
                            Role = userRole,
                        }
                    });
                }
            }

            if (request?.CloseRequestDocuments != null)
            {
                store.CloseRequestDocuments = NormalizeCloseRequestDocuments(
                    request.CloseRequestDocuments.Select(item => new CloseRequestDocumentState
                    {
                        DocumentName = item?.DocumentName,
                        Comment = item?.Comment,
                        CreatorStatus = "pending",
                        CheckerStatus = "pending",
                        Files = (item?.Files ?? new List<CloseRequestUploadedFileRequest>())
                            .Select(file => new CloseRequestUploadedFile
                            {
                                DocumentId = file?.DocumentId,
                                FileName = file?.FileName,
                                Url = file?.Url,
                                UploadedAt = file?.UploadedAt ?? DateTime.UtcNow,
                            })
                            .ToList(),
                    }));

                foreach (var item in store.CloseRequestDocuments.Where(i => !string.IsNullOrWhiteSpace(i?.Comment)))
                {
                    store.Comments.Add(new DeferralCommentEntry
                    {
                        Text = $"[Close Request] {item.DocumentName}: {item.Comment!.Trim()}",
                        CreatedAt = DateTime.UtcNow,
                        AuthorName = actorName,
                        AuthorRole = userRole,
                        Author = new DeferralCommentAuthor
                        {
                            Name = actorName,
                            Role = userRole,
                        }
                    });
                }
            }

            deferral.ReworkComments = SerializeDeferralCommentStore(store);

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            // Notify RM (confirmation of submitted close request)
            try
            {
                var rm = deferral.CreatedBy ?? await _context.Users.FindAsync(deferral.CreatedById);
                if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                {
                    var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                    await _emailService.SendDeferralSubmittedAsync(
                        rm.Email,
                        rmName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        deferral.DaysSought,
                        "Close Request"
                    );
                }
            }
            catch (Exception rmCloseEx)
            {
                _logger.LogWarning(rmCloseEx, "⚠️ Failed to notify RM after close request submission for {DeferralNumber}", deferral.DeferralNumber);
            }

            _logger.LogInformation(
                isExistingPendingCloseRequest
                    ? "📨 Close request updated for deferral {DeferralNumber} by RM {UserId}"
                    : "📨 Close request submitted for deferral {DeferralNumber} by RM {UserId}",
                deferral.DeferralNumber,
                userId);

            return Ok(new
            {
                success = true,
                message = isExistingPendingCloseRequest
                    ? "Close request updated successfully"
                    : "Close request submitted successfully",
                deferral,
                status = deferral.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error closing deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/close-request/approve-creator")]
    public async Task<IActionResult> ApproveCloseRequestByCreator(Guid id, [FromBody] ApprovalRequest request)
    {
        try
        {
            var userRole = (User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty).Trim();
            var normalizedRole = userRole.ToLowerInvariant();

            var isCreatorRole =
                normalizedRole == "creator" ||
                normalizedRole == "cocreator" ||
                normalizedRole == "co_creator" ||
                normalizedRole == "admin";

            if (!isCreatorRole)
                return StatusCode(403, new { error = "Only creator can approve close request" });

            var deferral = await _context.Deferrals.FirstOrDefaultAsync(d => d.Id == id);
            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            if (deferral.Status != DeferralStatus.CloseRequested)
                return BadRequest(new { error = "Deferral is not awaiting creator close-request approval" });

            deferral.UpdatedAt = DateTime.UtcNow;

            var actorName = User.FindFirst(ClaimTypes.Name)?.Value ?? "Creator";
            var store = ParseDeferralCommentStore(deferral.ReworkComments);

            if (store.CloseRequestDocuments.Count > 0)
            {
                var decisionLookup = (request?.CreatorDocumentDecisions ?? new List<CloseRequestDocumentDecisionRequest>())
                    .Where(item => !string.IsNullOrWhiteSpace(item?.DocumentName))
                    .ToDictionary(
                        item => NormalizeCloseRequestDocumentKey(item!.DocumentName),
                        item => item!,
                        StringComparer.OrdinalIgnoreCase);

                    var hasRejectedDocuments = false;
                    var hasPendingDocuments = false;

                foreach (var closeRequestDocument in store.CloseRequestDocuments)
                {
                    var key = NormalizeCloseRequestDocumentKey(closeRequestDocument.DocumentName);
                    if (!decisionLookup.TryGetValue(key, out var decision))
                    {
                        return BadRequest(new { error = $"Please review the uploaded close document for {closeRequestDocument.DocumentName}" });
                    }

                    var normalizedDecision = string.Equals(decision.Status, "approved", StringComparison.OrdinalIgnoreCase)
                        ? "approved"
                        : string.Equals(decision.Status, "rejected", StringComparison.OrdinalIgnoreCase)
                            ? "rejected"
                            : "pending";

                    closeRequestDocument.CreatorStatus = normalizedDecision;
                    closeRequestDocument.CreatorComment = decision.Comment?.Trim();
                    closeRequestDocument.CreatorReviewedByName = actorName;
                    closeRequestDocument.CreatorReviewedAt = DateTime.UtcNow;

                    if (string.Equals(normalizedDecision, "rejected", StringComparison.OrdinalIgnoreCase))
                    {
                        hasRejectedDocuments = true;
                    }
                    else if (!string.Equals(normalizedDecision, "approved", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPendingDocuments = true;
                    }
                }

                if (hasPendingDocuments)
                {
                    return BadRequest(new { error = "Please approve or reject every close request document before submitting your review" });
                }

                deferral.Status = hasRejectedDocuments
                    ? DeferralStatus.CloseRequested
                    : DeferralStatus.CloseRequestedCreatorApproved;
            }
            else
            {
                deferral.Status = DeferralStatus.CloseRequestedCreatorApproved;
            }

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = request.Comment.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = actorName,
                    AuthorRole = userRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = actorName,
                        Role = userRole,
                    }
                });
            }

            deferral.ReworkComments = SerializeDeferralCommentStore(store);

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            // Notify RM only when the close request is fully approved and moved to checker
            if (deferral.Status == DeferralStatus.CloseRequestedCreatorApproved)
            {
                try
                {
                    var rm = await _context.Users.FindAsync(deferral.CreatedById);
                    if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                    {
                        var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                        await _emailService.SendDeferralApprovalConfirmationAsync(
                            rm.Email,
                            rmName,
                            deferral.DeferralNumber,
                            string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                            null,
                            false);
                    }
                }
                catch (Exception rmNotifyEx)
                {
                    _logger.LogWarning(rmNotifyEx, "⚠️ Failed to notify RM after close-request creator approval for {DeferralNumber}", deferral.DeferralNumber);
                }
            }

            return Ok(new
            {
                success = true,
                message = deferral.Status == DeferralStatus.CloseRequestedCreatorApproved
                    ? "Close request approved by creator and sent to checker"
                    : "Creator review saved. Rejected documents remain pending RM correction",
                deferral,
                status = deferral.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error approving close request by creator");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/close-request/approve-checker")]
    public async Task<IActionResult> ApproveCloseRequestByChecker(Guid id, [FromBody] ApprovalRequest request)
    {
        try
        {
            var userRole = (User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty).Trim();
            var normalizedRole = userRole.ToLowerInvariant();

            var isCheckerRole =
                normalizedRole == "checker" ||
                normalizedRole == "cochecker" ||
                normalizedRole == "co_checker" ||
                normalizedRole == "admin";

            if (!isCheckerRole)
                return StatusCode(403, new { error = "Only checker can approve close request" });

            var deferral = await _context.Deferrals.FirstOrDefaultAsync(d => d.Id == id);
            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            if (deferral.Status != DeferralStatus.CloseRequestedCreatorApproved)
                return BadRequest(new { error = "Deferral is not awaiting checker close-request approval" });

            deferral.Status = DeferralStatus.Closed;
            deferral.UpdatedAt = DateTime.UtcNow;

            var actorName = User.FindFirst(ClaimTypes.Name)?.Value ?? "Checker";
            var store = ParseDeferralCommentStore(deferral.ReworkComments);

            if (store.CloseRequestDocuments.Count > 0)
            {
                foreach (var closeRequestDocument in store.CloseRequestDocuments)
                {
                    closeRequestDocument.CheckerStatus = "approved";
                    closeRequestDocument.CheckerComment = request?.Comment?.Trim();
                    closeRequestDocument.CheckerReviewedByName = actorName;
                    closeRequestDocument.CheckerReviewedAt = DateTime.UtcNow;
                }
            }

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                store.Comments.Add(new DeferralCommentEntry
                {
                    Text = request.Comment.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    AuthorName = actorName,
                    AuthorRole = userRole,
                    Author = new DeferralCommentAuthor
                    {
                        Name = actorName,
                        Role = userRole,
                    }
                });
            }

            deferral.ReworkComments = SerializeDeferralCommentStore(store);

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            // Notify RM that close-request was approved by checker (final close)
            try
            {
                var rm = await _context.Users.FindAsync(deferral.CreatedById);
                if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                {
                    var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                    await _emailService.SendDeferralApprovalConfirmationAsync(
                        rm.Email,
                        rmName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        null,
                        true);
                }
            }
            catch (Exception rmNotifyEx)
            {
                _logger.LogWarning(rmNotifyEx, "⚠️ Failed to notify RM after close-request checker approval for {DeferralNumber}", deferral.DeferralNumber);
            }

            return Ok(new
            {
                success = true,
                message = "Close request approved by checker. Deferral closed.",
                deferral,
                status = deferral.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error approving close request by checker");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/recall")]
    public async Task<IActionResult> RecallDeferral(Guid id, [FromBody] RecallDeferralRequest? request = null)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            if (deferral.CreatedById.HasValue && deferral.CreatedById != userId)
                return StatusCode(403, new { error = "Only the RM who created the deferral can recall it" });

            var status = deferral.Status;
            if (status == DeferralStatus.Rejected)
                return BadRequest(new { error = "Rejected deferrals cannot be recalled" });

            foreach (var approver in deferral.Approvers)
            {
                approver.Approved = false;
                approver.ApprovedAt = null;
            }

            deferral.CurrentApproverIndex = 0;
            deferral.Status = DeferralStatus.Pending;
            deferral.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("↩️ Deferral {DeferralNumber} recalled by RM {UserId}", deferral.DeferralNumber, userId);

            HydrateDeferralComments(deferral);

            return Ok(new
            {
                success = true,
                message = "Deferral recalled successfully",
                deferral
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error recalling deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{id}/withdraw")]
    public async Task<IActionResult> WithdrawDeferral(Guid id, [FromBody] WithdrawDeferralRequest? request = null)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .Include(d => d.CreatedBy)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            if (deferral.CreatedById.HasValue && deferral.CreatedById != userId)
                return StatusCode(403, new { error = "Only the RM who created the deferral can withdraw it" });

            if (deferral.Status == DeferralStatus.Rejected)
                return BadRequest(new { error = "Rejected deferrals cannot be withdrawn" });

            var actorName = User.FindFirst(ClaimTypes.Name)?.Value ?? "RM";

            // Record close/withdraw metadata
            deferral.Status = DeferralStatus.Closed;
            deferral.ClosedReason = request?.Reason?.Trim();
            deferral.ClosedById = userId;
            deferral.ClosedByName = actorName;
            deferral.ClosedAt = DateTime.UtcNow;
            deferral.UpdatedAt = DateTime.UtcNow;

            // Add a comment entry indicating withdrawal
            var store = ParseDeferralCommentStore(deferral.ReworkComments);
            var note = string.IsNullOrWhiteSpace(request?.Comment)
                ? "Withdrawn by RM"
                : $"Withdrawn by RM: {request.Comment.Trim()}";
            store.Comments.Add(new DeferralCommentEntry
            {
                Text = note,
                CreatedAt = DateTime.UtcNow,
                AuthorName = actorName,
                AuthorRole = "RM",
                Author = new DeferralCommentAuthor { Name = actorName, Role = "RM" }
            });
            deferral.ReworkComments = SerializeDeferralCommentStore(store);

            await _context.SaveChangesAsync();

            // Notify RM (confirmation of withdrawal)
            try
            {
                var rm = deferral.CreatedBy ?? await _context.Users.FindAsync(deferral.CreatedById);
                if (rm != null && !string.IsNullOrWhiteSpace(rm.Email))
                {
                    var rmName = string.IsNullOrWhiteSpace(rm.Name) ? "Relationship Manager" : rm.Name;
                    await _emailService.SendDeferralSubmittedAsync(
                        rm.Email,
                        rmName,
                        deferral.DeferralNumber,
                        string.IsNullOrWhiteSpace(deferral.CustomerName) ? "Customer" : deferral.CustomerName,
                        deferral.DaysSought,
                        "Withdrawn"
                    );
                }
            }
            catch (Exception rmCloseEx)
            {
                _logger.LogWarning(rmCloseEx, "⚠️ Failed to notify RM after withdrawal for {DeferralNumber}", deferral.DeferralNumber);
            }

            HydrateDeferralComments(deferral);

            _logger.LogInformation("🛑 Deferral {DeferralNumber} withdrawn by RM {UserId}", deferral.DeferralNumber, userId);

            return Ok(new
            {
                success = true,
                message = "Deferral withdrawn and closed",
                deferral
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error withdrawing deferral");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // PDF & EXPORT OPERATIONS
    // ============================================

    [HttpGet("{id}/pdf")]
    public async Task<IActionResult> GeneratePDF(Guid id)
    {
        try
        {
            var deferral = await _context.Deferrals
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            // TODO: Implement PDF generation
            return Ok(new { message = "PDF generation not yet implemented", deferral });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error generating PDF");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ============================================
    // HELPER METHODS
    // ============================================

    private async Task<string> GenerateDeferralNumber()
    {
        try
        {
            var yy = DateTime.UtcNow.Year.ToString().Substring(2);
            var prefix = $"DEF-{yy}-";

            // Collect all existing deferral numbers for this year prefix and determine the max sequence
            var numbers = await _context.Deferrals
                .Where(d => d.DeferralNumber != null && d.DeferralNumber.StartsWith(prefix))
                .Select(d => d.DeferralNumber!)
                .ToListAsync();

            var maxSeq = 0;
            var rx = new System.Text.RegularExpressions.Regex(@"DEF-\d{2}-(\d{4})");
            foreach (var num in numbers)
            {
                if (string.IsNullOrWhiteSpace(num)) continue;
                var m = rx.Match(num);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var s))
                {
                    if (s > maxSeq) maxSeq = s;
                }
            }

            var seq = maxSeq + 1;
            return $"{prefix}{seq:D4}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error while generating deferral number; returning safe fallback");
            // Fallback to a predictable preview-style number to avoid 500s
            var yy = DateTime.UtcNow.Year.ToString().Substring(2);
            return $"DEF-{yy}-0001";
        }
    }

    private static List<string> GetApprovalMatrixRoles(List<SelectedDocumentRequest> documents, decimal loanAmount)
    {
        var hasPrimary = documents.Any(d => IsDocType(d.Type, "Primary"));
        var hasSecondary = documents.Any(d => IsDocType(d.Type, "Secondary"));
        var isAboveThreshold = loanAmount > LoanThreshold;

        if (hasPrimary)
        {
            return isAboveThreshold
                ? new List<string>
                {
                    "Head of Business Segment",
                    "Group Director of Business Unit",
                    "Senior Manager, Retail & Corporate Credit Approvals / Assistant General Manager Corporate Credit Approvals / Head of Retail/Corporate Credit approvals"
                }
                : new List<string>
                {
                    "Head of Business Segment / Corporate Sector head",
                    "Director of Business Unit",
                    "Senior Manager, Retail & Corporate Credit Approvals / Assistant General Manager Corporate Credit Approvals / Head of Retail/Corporate Credit approvals"
                };
        }

        if (hasSecondary)
        {
            return isAboveThreshold
                ? new List<string>
                {
                    "Head of Business Segment",
                    "Group Director of Business Unit",
                    "Head of Credit Operations"
                }
                : new List<string>
                {
                    "Head of Business Segment",
                    "Director of Business Unit",
                    "Head of Credit Operations"
                };
        }

        return new List<string>();
    }

    private static bool RolesMatch(IReadOnlyList<string> expected, IReadOnlyList<string> provided)
    {
        if (expected.Count == 0)
        {
            return provided.Count == 0;
        }

        if (provided.Count < expected.Count)
        {
            return false;
        }

        static string Normalize(string? role) => (role ?? string.Empty).Trim().ToLowerInvariant();

        var normalizedExpected = expected.Select(Normalize).ToList();
        var normalizedProvided = provided.Select(Normalize).ToList();

        if (normalizedProvided[0] != normalizedExpected[0])
        {
            return false;
        }

        if (normalizedProvided[^1] != normalizedExpected[^1])
        {
            return false;
        }

        var expectedIndex = 0;
        foreach (var role in normalizedProvided)
        {
            if (role == normalizedExpected[expectedIndex])
            {
                expectedIndex++;
                if (expectedIndex >= normalizedExpected.Count)
                {
                    break;
                }
            }
        }

        return expectedIndex == normalizedExpected.Count;
    }

    private static bool IsDocType(string? value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSafeCurrentApproverIndex(int currentApproverIndex, int approverCount)
    {
        if (approverCount <= 0)
        {
            return 0;
        }

        if (currentApproverIndex < 0)
        {
            return 0;
        }

        if (currentApproverIndex >= approverCount)
        {
            return approverCount - 1;
        }

        return currentApproverIndex;
    }

    private static Guid CreateOrderedApproverId(Guid deferralId, int approverIndex)
    {
        var deferralHex = deferralId.ToString("N");
        var sequenceHex = (approverIndex + 1).ToString("x8");
        var tail = deferralHex.Substring(8);
        var orderedGuidText =
            $"{sequenceHex}-{tail.Substring(0, 4)}-{tail.Substring(4, 4)}-{tail.Substring(8, 4)}-{tail.Substring(12, 12)}";

        return Guid.Parse(orderedGuidText);
    }

    private static Guid? TryGetGuidFromClientValue(object? raw)
    {
        if (raw == null) return null;

        // If caller already passed a Guid
        if (raw is Guid g) return g;

        // If it's a string, attempt to parse
        if (raw is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Guid.TryParse(s, out var parsedGuid) ? parsedGuid : null;
        }

        // If the JSON binder produced a JsonElement (edge cases), try to extract as string
        if (raw is System.Text.Json.JsonElement je)
        {
            try
            {
                var str = je.GetString();
                if (string.IsNullOrWhiteSpace(str)) return null;
                return Guid.TryParse(str, out var parsed) ? parsed : null;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static List<Approver> OrderApproversForFlow(IEnumerable<Approver> approvers)
    {
        return approvers
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

    private sealed record ApproverFlowParticipant(Guid? UserId, string? Name, string? Role, bool Approved);

    private async Task<(string Name, string Role)> ResolveActorAsync()
    {
        var actorName = User.FindFirst("name")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var actorRole = User.FindFirst("role")?.Value ?? User.FindFirst(ClaimTypes.Role)?.Value;

        if (string.IsNullOrWhiteSpace(actorName) || string.IsNullOrWhiteSpace(actorRole))
        {
            var actorUserIdText = User.FindFirst("id")?.Value;
            if (Guid.TryParse(actorUserIdText, out var actorUserId))
            {
                var actorUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == actorUserId);
                actorName ??= actorUser?.Name;
                actorRole ??= actorUser?.Role.ToString();
            }
        }

        return (
            string.IsNullOrWhiteSpace(actorName) ? "System" : actorName.Trim(),
            string.IsNullOrWhiteSpace(actorRole) ? "System" : actorRole.Trim()
        );
    }

    private async Task AppendApproverFlowChangeAuditAndNotificationsAsync(
        Deferral deferral,
        IReadOnlyList<ApproverFlowParticipant> previousApprovers,
        IReadOnlyList<ApproverFlowParticipant> updatedApprovers,
        Guid? previousCurrentApproverUserId,
        Guid? currentPendingApproverUserId,
        string? currentPendingApproverRole)
    {
        if (deferral == null)
        {
            return;
        }

        var (actorName, actorRole) = await ResolveActorAsync();
        var previousApproverIds = previousApprovers
            .Where(a => a.UserId.HasValue)
            .Select(a => a.UserId!.Value)
            .ToHashSet();
        var updatedApproverIds = updatedApprovers
            .Where(a => a.UserId.HasValue)
            .Select(a => a.UserId!.Value)
            .ToHashSet();

        var addedApprovers = updatedApprovers
            .Where(a => a.UserId.HasValue && !previousApproverIds.Contains(a.UserId.Value))
            .ToList();
        var removedApprovers = previousApprovers
            .Where(a => a.UserId.HasValue && !updatedApproverIds.Contains(a.UserId.Value))
            .ToList();

        var previousIndexByUser = previousApprovers
            .Select((approver, index) => new { approver, index })
            .Where(x => x.approver.UserId.HasValue)
            .ToDictionary(x => x.approver.UserId!.Value, x => x.index);
        var movedApprovers = updatedApprovers
            .Select((approver, index) => new { approver, index })
            .Where(x => x.approver.UserId.HasValue)
            .Where(x => previousIndexByUser.TryGetValue(x.approver.UserId!.Value, out var previousIndex) && previousIndex != x.index)
            .Select(x => x.approver)
            .ToList();

        var previousCurrentApprover = previousApprovers.FirstOrDefault(a => a.UserId == previousCurrentApproverUserId);
        var currentPendingApprover = updatedApprovers.FirstOrDefault(a => a.UserId == currentPendingApproverUserId);

        var auditParts = new List<string> { $"Approval flow updated by {actorName}." };
        if (addedApprovers.Count > 0)
        {
            auditParts.Add($"Added: {string.Join(", ", addedApprovers.Select(approver => FormatApproverParticipant(approver)))}.");
        }
        if (removedApprovers.Count > 0)
        {
            auditParts.Add($"Removed: {string.Join(", ", removedApprovers.Select(approver => FormatApproverParticipant(approver)))}.");
        }
        if (movedApprovers.Count > 0)
        {
            auditParts.Add($"Sequence updated for: {string.Join(", ", movedApprovers.Select(approver => FormatApproverParticipant(approver)).Distinct())}.");
        }
        if (previousCurrentApproverUserId != currentPendingApproverUserId && currentPendingApproverUserId.HasValue)
        {
            auditParts.Add($"Current approver changed from {FormatApproverParticipant(previousCurrentApprover)} to {FormatApproverParticipant(currentPendingApprover, currentPendingApproverRole)}.");
        }

        var store = ParseDeferralCommentStore(deferral.ReworkComments);
        store.Comments.Add(new DeferralCommentEntry
        {
            Text = string.Join(" ", auditParts.Where(part => !string.IsNullOrWhiteSpace(part))),
            CreatedAt = DateTime.UtcNow,
            AuthorName = actorName,
            AuthorRole = actorRole,
            Author = new DeferralCommentAuthor
            {
                Name = actorName,
                Role = actorRole,
            }
        });
        deferral.ReworkComments = SerializeDeferralCommentStore(store);

        var notifications = new List<Notification>();
        var notifiedUsers = new HashSet<Guid>();

        void AddNotification(Guid? userId, string message)
        {
            if (!userId.HasValue || !notifiedUsers.Add(userId.Value) || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                Message = message,
                Read = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (previousCurrentApproverUserId != currentPendingApproverUserId)
        {
            AddNotification(
                previousCurrentApproverUserId,
                $"Deferral {deferral.DeferralNumber} is no longer awaiting your approval because the approval flow changed."
            );
            AddNotification(
                currentPendingApproverUserId,
                $"Deferral {deferral.DeferralNumber} is now awaiting your approval as the current approver."
            );
        }

        foreach (var removedApprover in removedApprovers)
        {
            AddNotification(
                removedApprover.UserId,
                $"You were removed from the approval flow for deferral {deferral.DeferralNumber}."
            );
        }

        foreach (var addedApprover in addedApprovers)
        {
            AddNotification(
                addedApprover.UserId,
                $"You were added to the approval flow for deferral {deferral.DeferralNumber} as {NormalizeRoleLabel(addedApprover.Role)}."
            );
        }

        foreach (var movedApprover in movedApprovers)
        {
            AddNotification(
                movedApprover.UserId,
                $"Your approval step for deferral {deferral.DeferralNumber} was updated."
            );
        }

        if (notifications.Count > 0)
        {
            _context.Notifications.AddRange(notifications);
        }
    }

    private static string NormalizeRoleLabel(string? role)
    {
        return string.IsNullOrWhiteSpace(role) ? "Approver" : role.Trim();
    }

    private static string FormatApproverParticipant(ApproverFlowParticipant? approver, string? fallbackRole = null)
    {
        if (approver == null)
        {
            return "Approver";
        }

        var name = string.IsNullOrWhiteSpace(approver.Name) ? "Approver" : approver.Name.Trim();
        var role = NormalizeRoleLabel(approver.Role ?? fallbackRole);
        return $"{name} ({role})";
    }

    private static void ApplyApproverOrdering(Deferral deferral)
    {
        if (deferral == null)
        {
            return;
        }

        deferral.Approvers = OrderApproversForFlow(deferral.Approvers);
    }

    private static string? DecorateDocumentUrl(string? rawUrl, bool? isDcl, bool? isAdditional, string? documentName = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return rawUrl;
        }

        var finalUrl = rawUrl;

        if (!string.IsNullOrWhiteSpace(documentName))
        {
            var trimmedName = documentName.Trim();
            var encodedName = Uri.EscapeDataString(trimmedName);
            finalUrl = finalUrl.Contains('?', StringComparison.Ordinal)
                ? $"{finalUrl}&docTarget={encodedName}"
                : $"{finalUrl}?docTarget={encodedName}";
        }

        var section = isDcl == true
            ? "dcl"
            : isAdditional == true
                ? "additional"
                : null;

        if (string.IsNullOrWhiteSpace(section))
        {
            return finalUrl;
        }

        const string marker = "#docSection=";
        var markerIndex = finalUrl.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return finalUrl.Substring(0, markerIndex) + marker + section;
        }

        return finalUrl + marker + section;
    }

    private static DeferralCommentStore ParseDeferralCommentStore(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new DeferralCommentStore();

        try
        {
            var parsed = JsonSerializer.Deserialize<DeferralCommentStore>(raw);
            return parsed ?? new DeferralCommentStore();
        }
        catch
        {
            return new DeferralCommentStore { ReworkComment = raw };
        }
    }

    private static string SerializeDeferralCommentStore(DeferralCommentStore store)
    {
        return JsonSerializer.Serialize(store);
    }

    private static string NormalizeCloseRequestDocumentKey(string? value)
    {
        return string.Join(" ", (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<CloseRequestDocumentState> NormalizeCloseRequestDocuments(IEnumerable<CloseRequestDocumentState>? documents)
    {
        return (documents ?? Enumerable.Empty<CloseRequestDocumentState>())
            .Where(document => !string.IsNullOrWhiteSpace(document?.DocumentName))
            .GroupBy(document => NormalizeCloseRequestDocumentKey(document!.DocumentName))
            .Select(group =>
            {
                var latest = group.Last();
                return new CloseRequestDocumentState
                {
                    DocumentName = latest.DocumentName?.Trim(),
                    Comment = latest.Comment?.Trim(),
                    CreatorStatus = string.IsNullOrWhiteSpace(latest.CreatorStatus)
                        ? "pending"
                        : latest.CreatorStatus.Trim().ToLowerInvariant(),
                    CreatorComment = latest.CreatorComment?.Trim(),
                    CreatorReviewedByName = latest.CreatorReviewedByName,
                    CreatorReviewedAt = latest.CreatorReviewedAt,
                    CheckerStatus = string.IsNullOrWhiteSpace(latest.CheckerStatus)
                        ? "pending"
                        : latest.CheckerStatus.Trim().ToLowerInvariant(),
                    CheckerComment = latest.CheckerComment?.Trim(),
                    CheckerReviewedByName = latest.CheckerReviewedByName,
                    CheckerReviewedAt = latest.CheckerReviewedAt,
                    Files = (latest.Files ?? new List<CloseRequestUploadedFile>())
                        .Where(file => !string.IsNullOrWhiteSpace(file?.Url) || !string.IsNullOrWhiteSpace(file?.FileName))
                        .Select(file => new CloseRequestUploadedFile
                        {
                            DocumentId = file?.DocumentId,
                            FileName = file?.FileName,
                            Url = file?.Url,
                            UploadedAt = file?.UploadedAt,
                        })
                        .ToList(),
                };
            })
            .OrderBy(document => document.DocumentName)
            .ToList();
    }

    private static void HydrateDeferralComments(Deferral deferral)
    {
        if (deferral == null) return;

        var store = ParseDeferralCommentStore(deferral.ReworkComments);
        deferral.RmReason = store.RmReason;
        deferral.LastReturnedByRole = store.LastReturnedByRole;
        deferral.CloseRequestDocuments = NormalizeCloseRequestDocuments(store.CloseRequestDocuments);
        var hydratedComments = (store.Comments ?? new List<DeferralCommentEntry>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .OrderBy(c => c.CreatedAt ?? DateTime.MinValue)
            .ToList();

        if (!string.IsNullOrWhiteSpace(store.ReworkComment))
        {
            var normalizedReworkComment = store.ReworkComment.Trim();
            var hasReworkCommentInTrail = hydratedComments.Any(c =>
                string.Equals((c.Text ?? string.Empty).Trim(), normalizedReworkComment, StringComparison.OrdinalIgnoreCase));

            if (!hasReworkCommentInTrail)
            {
                var roleLabel = string.IsNullOrWhiteSpace(store.LastReturnedByRole)
                    ? "Approver"
                    : store.LastReturnedByRole.Trim().Equals("creator", StringComparison.OrdinalIgnoreCase)
                        ? "Creator"
                        : store.LastReturnedByRole.Trim().Equals("checker", StringComparison.OrdinalIgnoreCase)
                            ? "Checker"
                            : "Approver";

                hydratedComments.Add(new DeferralCommentEntry
                {
                    Text = normalizedReworkComment,
                    CreatedAt = deferral.UpdatedAt,
                    AuthorName = roleLabel,
                    AuthorRole = roleLabel,
                    Author = new DeferralCommentAuthor
                    {
                        Name = roleLabel,
                        Role = roleLabel,
                    }
                });
            }
        }

        deferral.Comments = hydratedComments
            .OrderBy(c => c.CreatedAt ?? DateTime.MinValue)
            .ToList();

        HydrateDeferralWorkflowFlags(deferral);
    }

    private static void HydrateDeferralWorkflowFlags(Deferral deferral)
    {
        if (deferral == null) return;

        deferral.CreatorApprovalStatus = "pending";
        deferral.CheckerApprovalStatus = "pending";
        deferral.DeferralApprovalStatus = "pending";
        deferral.CreatorApprovalDate = null;
        deferral.CheckerApprovalDate = null;

        switch (deferral.Status)
        {
            case DeferralStatus.PartiallyApproved:
                deferral.CreatorApprovalStatus = "approved";
                deferral.CheckerApprovalStatus = "pending";
                deferral.DeferralApprovalStatus = "pending";
                deferral.CreatorApprovalDate = deferral.UpdatedAt;
                break;

            case DeferralStatus.Approved:
            case DeferralStatus.CloseRequested:
            case DeferralStatus.CloseRequestedCreatorApproved:
            case DeferralStatus.Closed:
                deferral.CreatorApprovalStatus = "approved";
                deferral.CheckerApprovalStatus = "approved";
                deferral.DeferralApprovalStatus = "approved";
                deferral.CreatorApprovalDate = deferral.UpdatedAt;
                deferral.CheckerApprovalDate = deferral.UpdatedAt;
                break;

            case DeferralStatus.Rejected:
                deferral.DeferralApprovalStatus = "rejected";
                break;

            case DeferralStatus.ReturnedForRework:
                deferral.DeferralApprovalStatus = "returned";
                break;
        }
    }

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
        catch (Exception)
        {
            // If deserialization fails, return empty list
            deferral.SelectedDocuments = new List<SelectedDocumentData>();
        }
    }

    private static void DeserializeExtensionSelectedDocuments(IEnumerable<Extension>? extensions)
    {
        if (extensions == null)
        {
            return;
        }

        foreach (var extension in extensions)
        {
            DeserializeExtensionSelectedDocuments(extension);
        }
    }

    private static void DeserializeExtensionSelectedDocuments(Extension? extension)
    {
        if (extension == null || string.IsNullOrWhiteSpace(extension.SelectedDocumentsJson))
        {
            if (extension != null)
            {
                extension.SelectedDocuments = new List<SelectedDocumentData>();
            }
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
}

// ============================================
// REQUEST/RESPONSE MODELS
// ============================================

public class CreateDeferralRequest
{
    public string? CustomerNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? BusinessName { get; set; }
    public string? LoanType { get; set; }
    public decimal? LoanAmount { get; set; }
    public int? DaysSought { get; set; }
    public string? DclNumber { get; set; }
    public string? DeferralDescription { get; set; }
    public List<FacilityRequest>? Facilities { get; set; }
    public List<SelectedDocumentRequest>? SelectedDocuments { get; set; }
    public List<ApproverRequest>? Approvers { get; set; }
    public List<DeferralCommentRequest>? Comments { get; set; }
}

public class UpdateDeferralRequest
{
    public string? DeferralDescription { get; set; }
    public List<FacilityRequest>? Facilities { get; set; }
    public List<UpdateApproverRequest>? Approvers { get; set; }
    public List<UpdateApproverRequest>? ApproverFlow { get; set; }
    public string? ResubmissionComments { get; set; }
    public DeferralStatus? Status { get; set; }
    public List<SelectedDocumentRequest>? SelectedDocuments { get; set; }
}

public class UpdateApproverRequest
{
    public string? Name { get; set; }
    public string? Role { get; set; }
    public string? UserId { get; set; }
    public string? User { get; set; }
}

public class FacilityRequest
{
    public string? Type { get; set; }
    public decimal Sanctioned { get; set; }
    public decimal Balance { get; set; }
    public decimal Headroom { get; set; }
}

public class AddDeferralDocumentRequest
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public bool? IsDCL { get; set; }
    public bool? IsAdditional { get; set; }
}

public class ApproverRequest
{
    public string? Name { get; set; }
    public string? Role { get; set; }
    public Guid? UserId { get; set; }
    public Guid? User { get; set; }
}

public class SelectedDocumentRequest
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Category { get; set; }
    // Per-document deferral metadata
    public int? DaysSought { get; set; }
    public DateTime? NextDocumentDueDate { get; set; }
}

public class DeferralCommentRequest
{
    public string? Text { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorRole { get; set; }
    public DeferralCommentAuthorRequest? Author { get; set; }
}

public class DeferralCommentAuthorRequest
{
    public string? Name { get; set; }
    public string? Role { get; set; }
}

public class DeferralCommentStore
{
    public string? RmReason { get; set; }
    public string? ReworkComment { get; set; }
    public string? LastReturnedByRole { get; set; }
    public List<DeferralCommentEntry> Comments { get; set; } = new();
    public List<CloseRequestDocumentState> CloseRequestDocuments { get; set; } = new();
}

public class ApprovalRequest
{
    public string? Comment { get; set; }
    public List<string>? ApprovedDocuments { get; set; }
    public List<CloseRequestDocumentDecisionRequest>? CreatorDocumentDecisions { get; set; }
    public List<CloseRequestDocumentDecisionRequest>? CheckerDocumentDecisions { get; set; }
}

public class RejectDeferralRequest
{
    public string? Reason { get; set; }
}

public class ReturnForReworkRequest
{
    public string? ReworkComment { get; set; }
}

public class CloseDeferralRequest
{
    public string? Reason { get; set; }
    public string? Comment { get; set; }
    public List<CloseDocumentCommentRequest>? DocumentComments { get; set; }
    public List<CloseRequestDocumentSubmitRequest>? CloseRequestDocuments { get; set; }
}

public class CloseDocumentCommentRequest
{
    public string? DocumentName { get; set; }
    public string? Comment { get; set; }
}

public class CloseRequestDocumentSubmitRequest
{
    public string? DocumentName { get; set; }
    public string? Comment { get; set; }
    public List<CloseRequestUploadedFileRequest>? Files { get; set; }
}

public class CloseRequestUploadedFileRequest
{
    public string? DocumentId { get; set; }
    public string? FileName { get; set; }
    public string? Url { get; set; }
    public DateTime? UploadedAt { get; set; }
}

public class CloseRequestDocumentDecisionRequest
{
    public string? DocumentName { get; set; }
    public string? Status { get; set; }
    public string? Comment { get; set; }
}

public class RecallDeferralRequest
{
    public string? Reason { get; set; }
}

public class WithdrawDeferralRequest
{
    public string? Reason { get; set; }
    public string? Comment { get; set; }
}

public class SubmitExtensionRequest
{
    public Dictionary<string, int>? ExtensionDaysByDocument { get; set; }
    public string? Comment { get; set; }
    public List<string>? FileUrls { get; set; }
}