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

            if (request.Approvers.Any(a => (a.UserId ?? a.User) == null))
            {
                return BadRequest(new { error = "All approver slots must be assigned to a user" });
            }

            var approverUserIds = request.Approvers
                .Select(a => (a.UserId ?? a.User)!.Value)
                .ToList();

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
                        DeferralId = deferral.Id
                    });
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
            return StatusCode(500, new { error = ex.Message });
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
                .OrderByDescending(d => d.UpdatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);

            _logger.LogInformation($"✅ Fetched {deferrals.Count} approved deferrals");
            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching approved deferrals");
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
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var deferrals = await _context.Deferrals
                .Where(d => d.CreatedById == userId)
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                    .ThenInclude(doc => doc.UploadedBy)
                .Include(d => d.Approvers)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);

            return Ok(deferrals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching my deferrals");
            return StatusCode(500, new { error = ex.Message });
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

            var deferrals = await _context.Deferrals
                .Where(d => d.Approvers.Any(a => a.UserId == userId && a.Approved))
                .Include(d => d.CreatedBy)
                .Include(d => d.Facilities)
                .Include(d => d.Documents)
                .Include(d => d.Approvers)
                    .ThenInclude(a => a.User)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            deferrals.ForEach(ApplyApproverOrdering);
            deferrals.ForEach(HydrateDeferralComments);

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
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            ApplyApproverOrdering(deferral);
            HydrateDeferralComments(deferral);

            return Ok(deferral);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 Error fetching deferral");
            return StatusCode(500, new { error = ex.Message });
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
            _logger.LogError(ex, "🔥 Error generating deferral number");
            return StatusCode(500, new { error = ex.Message });
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
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

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

            var requestedApprovers = request.ApproverFlow ?? request.Approvers;
            Guid? previousFirstApproverUserId = null;
            Guid? updatedFirstApproverUserId = null;
            Guid? currentPendingApproverUserId = null;
            string? currentPendingApproverRole = null;
            var approverFlowUpdated = false;
            if (requestedApprovers != null && requestedApprovers.Count > 0)
            {
                approverFlowUpdated = true;
                var orderedExistingApprovers = OrderApproversForFlow(deferral.Approvers);
                previousFirstApproverUserId = orderedExistingApprovers.FirstOrDefault()?.UserId;
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

            deferral.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

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
                .FirstOrDefaultAsync(d => d.Id == id);

            if (deferral == null)
                return NotFound(new { error = "Deferral not found" });

            _context.Approvers.RemoveRange(deferral.Approvers);

            for (var approverIndex = 0; approverIndex < approvers.Count; approverIndex++)
            {
                var approverReq = approvers[approverIndex];
                var approver = new Approver
                {
                    Id = CreateOrderedApproverId(id, approverIndex),
                    UserId = approverReq.UserId ?? approverReq.User,
                    Name = approverReq.Name,
                    Role = approverReq.Role,
                    Approved = false,
                    DeferralId = id
                };
                _context.Approvers.Add(approver);
            }

            deferral.CurrentApproverIndex = 0;
            await _context.SaveChangesAsync();

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
                    await _emailService.SendDeferralApprovedToRmAsync(
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

            if (deferral.Status != DeferralStatus.Approved)
                return BadRequest(new { error = "Close request can only be submitted for approved deferrals" });

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

            deferral.ReworkComments = SerializeDeferralCommentStore(store);

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            _logger.LogInformation("📨 Close request submitted for deferral {DeferralNumber} by RM {UserId}", deferral.DeferralNumber, userId);

            return Ok(new
            {
                success = true,
                message = "Close request submitted successfully",
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

            deferral.Status = DeferralStatus.CloseRequestedCreatorApproved;
            deferral.UpdatedAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                var actorName = User.FindFirst(ClaimTypes.Name)?.Value ?? "Creator";
                var store = ParseDeferralCommentStore(deferral.ReworkComments);
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
                deferral.ReworkComments = SerializeDeferralCommentStore(store);
            }

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

            return Ok(new
            {
                success = true,
                message = "Close request approved by creator",
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

            if (!string.IsNullOrWhiteSpace(request?.Comment))
            {
                var actorName = User.FindFirst(ClaimTypes.Name)?.Value ?? "Checker";
                var store = ParseDeferralCommentStore(deferral.ReworkComments);
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
                deferral.ReworkComments = SerializeDeferralCommentStore(store);
            }

            await _context.SaveChangesAsync();
            HydrateDeferralComments(deferral);

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

    private static Guid? TryGetGuidFromClientValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Guid.TryParse(raw, out var parsedGuid) ? parsedGuid : null;
    }

    private static List<Approver> OrderApproversForFlow(IEnumerable<Approver> approvers)
    {
        return approvers
            .OrderBy(a => a.Id)
            .ToList();
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

    private static void HydrateDeferralComments(Deferral deferral)
    {
        if (deferral == null) return;

        var store = ParseDeferralCommentStore(deferral.ReworkComments);
        deferral.RmReason = store.RmReason;
        deferral.LastReturnedByRole = store.LastReturnedByRole;
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
}

public class ApprovalRequest
{
    public string? Comment { get; set; }
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
}

public class CloseDocumentCommentRequest
{
    public string? DocumentName { get; set; }
    public string? Comment { get; set; }
}

public class RecallDeferralRequest
{
    public string? Reason { get; set; }
}