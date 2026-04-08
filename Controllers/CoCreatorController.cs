using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.Models;
using NCBA.DCL.Services;
using NCBA.DCL.Helpers;
using NCBA.DCL.DTOs;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/cocreatorChecklist")]
[Authorize]
public class CoCreatorController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CoCreatorController> _logger;
    private readonly IEmailService _emailService;
    private readonly IWebHostEnvironment _environment;

    public CoCreatorController(
        ApplicationDbContext context,
        ILogger<CoCreatorController> logger,
        IEmailService emailService,
        IWebHostEnvironment environment)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
        _environment = environment;
    }
    [HttpGet("{id}/draft")]

    public async Task<IActionResult> GetChecklistDraft(Guid id)
    {
        try
        {
            var checklist = await _context.Checklists.FirstOrDefaultAsync(c => c.Id == id);
            if (checklist == null)
                return NotFound(new { message = "Checklist not found" });

            return Ok(new
            {
                draftData = checklist.DraftDataJson,
                isDraft = checklist.IsDraft,
                expiresAt = checklist.DraftExpiresAt,
                lastSaved = checklist.DraftLastSaved
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading draft");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST /api/cocreatorChecklist/save-draft
    [HttpPost("save-draft")]
    public async Task<IActionResult> SaveChecklistDraft([FromBody] SaveChecklistDraftRequest request)
    {
        try
        {
            if (request.ChecklistId == null || request.ChecklistId == Guid.Empty)
                return BadRequest(new { message = "ChecklistId is required in the request body." });

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var checklist = await _context.Checklists.FirstOrDefaultAsync(c => c.Id == request.ChecklistId);
            if (checklist == null)
                return NotFound(new { message = "Checklist not found" });

            checklist.DraftDataJson = request.DraftDataJson;
            checklist.IsDraft = request.IsDraft ?? true;
            checklist.DraftExpiresAt = request.DraftExpiresAt ?? DateTime.UtcNow.AddDays(1);
            checklist.DraftLastSaved = DateTime.UtcNow;
            checklist.UpdatedAt = DateTime.UtcNow;

            checklist.Logs.Add(new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Draft saved",
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                ChecklistId = checklist.Id
            });

            await _context.SaveChangesAsync();
            return Ok(new
            {
                message = "Draft saved successfully",
                checklist = new
                {
                    id = checklist.Id,
                    dclNo = checklist.DclNo,
                    status = checklist.Status.ToString(),
                    lastSaved = checklist.DraftLastSaved,
                    expiresAt = checklist.DraftExpiresAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving draft");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PATCH /api/cocreatorChecklist/{id}/update-status-with-docs
    [HttpPatch("{id}/update-status-with-docs")]
    public async Task<IActionResult> UpdateChecklistStatusWithDocs(Guid id, [FromBody] UpdateChecklistWithDocsRequest request)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.Documents).ThenInclude(dc => dc.DocList)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (checklist == null)
                return NotFound(new { message = "Checklist not found" });

            // Only allow update if status is CoCreatorReview
            if (checklist.Status != ChecklistStatus.CoCreatorReview)
                return StatusCode(403, new { message = "You can only update this checklist after RM sends it back for correction." });

            // Update documents
            if (request.Documents != null)
            {
                foreach (var updatedCat in request.Documents)
                {
                    var cat = checklist.Documents.FirstOrDefault(c => c.Category == updatedCat.Category);
                    if (cat == null) continue;
                        foreach (var docUpdate in updatedCat.DocList ?? new List<DocumentDto>())
                    {
                        var docId = docUpdate.DocumentId ?? docUpdate.Id;
                        var doc = cat.DocList.FirstOrDefault(d => d.Id == docId);
                        if (doc == null) continue;
                        if (docUpdate.FileUrl != null) doc.FileUrl = docUpdate.FileUrl;
                        if (docUpdate.Comment != null) doc.Comment = docUpdate.Comment;
                        if (docUpdate.Status.HasValue) doc.Status = docUpdate.Status.Value;
                        if (docUpdate.DeferralReason != null) doc.DeferralReason = docUpdate.DeferralReason;
                        if (docUpdate.DeferralNumber != null) doc.DeferralNumber = docUpdate.DeferralNumber;

                        // Explicitly mark as modified to ensure persistence
                        _context.Entry(doc).State = EntityState.Modified;
                    }
                }
            }
            if (request.Status.HasValue) checklist.Status = request.Status.Value;

            if (!string.IsNullOrEmpty(request.GeneralComment))
            {
                checklist.GeneralComment = request.GeneralComment;
                var commentLog = new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = request.GeneralComment,  // NO PREFIX - store raw message
                    UserId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty),
                    ChecklistId = id,
                    Timestamp = DateTime.UtcNow
                };
                _context.ChecklistLogs.Add(commentLog);
                _logger.LogInformation($"✅ Co-Creator comment saved: ChecklistId={id}, Message length={request.GeneralComment.Length}");
            }

            var updateLog = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Checklist updated by Co-Creator",
                UserId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty),
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(updateLog);
            checklist.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Checklist updated successfully", checklist });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating checklist");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST /api/cocreatorChecklist/{id}/revive
    [HttpPost("{id}/revive")]
    public async Task<IActionResult> ReviveChecklist(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var original = await _context.Checklists
                .Include(c => c.Documents).ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (original == null)
                return NotFound(new { message = "Checklist not found" });

            // Only allow revival of approved or completed checklists
            if (!(original.Status == ChecklistStatus.Approved || original.Status == ChecklistStatus.Completed))
                return BadRequest(new { message = "Only approved or completed checklists can be revived", currentStatus = original.Status.ToString() });

            // Generate new DCL number with copy suffix
            var baseDclNo = original.DclNo.Split(" copy ")[0];
            var existingCopies = await _context.Checklists.Where(c => c.DclNo.StartsWith(baseDclNo + " copy ")).OrderByDescending(c => c.CreatedAt).ToListAsync();
            int copyNumber = 1;
            if (existingCopies.Count > 0)
            {
                var last = existingCopies[0].DclNo;
                var match = System.Text.RegularExpressions.Regex.Match(last, @" copy (\d+)$");
                if (match.Success) copyNumber = int.Parse(match.Groups[1].Value) + 1;
            }
            var newDclNo = $"{baseDclNo} copy {copyNumber}";

            var originalUploads = await _context.Uploads
                .Where(u => u.ChecklistId == original.Id && (u.Status == null || u.Status != "deleted"))
                .ToListAsync();

            var revivedDocumentIdMap = new Dictionary<Guid, Guid>();
            var revivedCategories = new List<DocumentCategory>();

            foreach (var originalCategory in original.Documents)
            {
                var revivedCategory = new DocumentCategory
                {
                    Id = Guid.NewGuid(),
                    Category = originalCategory.Category,
                    ChecklistId = Guid.Empty,
                    DocList = new List<Document>()
                };

                foreach (var originalDoc in originalCategory.DocList)
                {
                    var revivedDoc = new Document
                    {
                        Id = Guid.NewGuid(),
                        Name = originalDoc.Name,
                        Category = originalDoc.Category,
                        Status = originalDoc.Status,
                        FileUrl = originalDoc.FileUrl,
                        ExpiryDate = originalDoc.ExpiryDate,
                        Comment = originalDoc.Comment,
                        CreatorComment = originalDoc.CreatorComment,
                        CheckerComment = originalDoc.CheckerComment,
                        RmComment = originalDoc.RmComment,
                        CreatorStatus = originalDoc.CreatorStatus,
                        CheckerStatus = originalDoc.CheckerStatus,
                        RmStatus = originalDoc.RmStatus,
                        DeferralReason = originalDoc.DeferralReason,
                        DeferralNumber = originalDoc.DeferralNumber,
                        CreatedAt = originalDoc.CreatedAt,
                        UpdatedAt = originalDoc.UpdatedAt,
                    };

                    revivedCategory.DocList.Add(revivedDoc);
                    revivedDocumentIdMap[originalDoc.Id] = revivedDoc.Id;
                }

                revivedCategories.Add(revivedCategory);
            }

            // Clone checklist and documents
            var revived = new Checklist
            {
                Id = Guid.NewGuid(),
                DclNo = newDclNo,
                CustomerId = original.CustomerId,
                CustomerNumber = original.CustomerNumber,
                CustomerName = original.CustomerName,
                LoanType = original.LoanType,
                AssignedToRMId = original.AssignedToRMId,
                CreatedById = userId,
                Status = ChecklistStatus.CoCreatorReview,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Documents = revivedCategories,
                SupportingDocs = original.SupportingDocs.Select(sd => new SupportingDoc
                {
                    Id = Guid.NewGuid(),
                    FileName = sd.FileName,
                    FileUrl = sd.FileUrl,
                    FileSize = sd.FileSize,
                    FileType = sd.FileType,
                    UploadedById = sd.UploadedById,
                    UploadedByRole = sd.UploadedByRole,
                    UploadedAt = sd.UploadedAt,
                    ChecklistId = Guid.Empty,
                }).ToList(),
                Logs = new List<ChecklistLog> {
                        new ChecklistLog {
                            Id = Guid.NewGuid(),
                            Message = $"Revived from {original.DclNo}",
                            UserId = userId,
                            Timestamp = DateTime.UtcNow
                        }
                    }
            };

            foreach (var revivedCategory in revived.Documents)
            {
                revivedCategory.ChecklistId = revived.Id;
            }

            foreach (var revivedSupportingDoc in revived.SupportingDocs)
            {
                revivedSupportingDoc.ChecklistId = revived.Id;
            }

            var clonedUploads = originalUploads.Select(upload => new Upload
            {
                Id = Guid.NewGuid(),
                ChecklistId = revived.Id,
                DocumentId = upload.DocumentId.HasValue && revivedDocumentIdMap.TryGetValue(upload.DocumentId.Value, out var revivedDocumentId)
                    ? revivedDocumentId
                    : null,
                DocumentName = upload.DocumentName,
                Category = upload.Category,
                FileName = upload.FileName,
                FilePath = upload.FilePath,
                FileUrl = upload.FileUrl,
                FileData = upload.FileData,
                FileSize = upload.FileSize,
                FileType = upload.FileType,
                UploadedBy = upload.UploadedBy,
                UploadedByRole = upload.UploadedByRole,
                Status = upload.Status,
                CreatedAt = upload.CreatedAt,
                UpdatedAt = upload.UpdatedAt,
            }).ToList();

            _context.Checklists.Add(revived);
            if (clonedUploads.Count > 0)
            {
                _context.Uploads.AddRange(clonedUploads);
            }
            await _context.SaveChangesAsync();

            _logger.LogInformation($"✅ Checklist revived successfully. Original: {original.DclNo} ({original.Id}), New: {newDclNo} ({revived.Id})");

            // Return response with newChecklistId for clarity
            return StatusCode(201, new
            {
                message = $"Checklist revived as {newDclNo}",
                newChecklistId = revived.Id,
                checklist = new
                {
                    id = revived.Id,
                    _id = revived.Id,
                    dclNo = revived.DclNo,
                    status = revived.Status.ToString(),
                    createdAt = revived.CreatedAt,
                    updatedAt = revived.UpdatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviving checklist");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST /api/cocreatorChecklist
    [HttpPost]
    public async Task<IActionResult> CreateChecklist([FromBody] CreateChecklistRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            // Generate DCL number with format YYYY-NNNN
            var currentYear = DateTime.Now.Year;
            var checklistsThisYear = await _context.Checklists
                .Where(c => c.DclNo.StartsWith(currentYear.ToString()))
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            int sequenceNumber = checklistsThisYear.Count() + 1;
            var dclNo = $"{currentYear}-{sequenceNumber:D4}";

            _logger.LogInformation($"?? Creating checklist: {dclNo}");
            _logger.LogInformation($"?? Request Documents Count: {request.Documents?.Count() ?? 0}");

            var checklist = new Checklist
            {
                Id = Guid.NewGuid(),
                DclNo = dclNo,
                CustomerNumber = request.CustomerNumber ?? string.Empty,
                CustomerName = request.CustomerName,
                LoanType = request.LoanType,
                IbpsNo = request.IbpsNo,
                AssignedToRMId = request.AssignedToRMId,
                CreatedById = userId,
                Status = ChecklistStatus.Pending, // Start as pending until submitted for review
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Documents = new List<DocumentCategory>(), // Initialize documents collection
                Logs = new List<ChecklistLog>() // Initialize logs collection
            };

            // Handle documents if provided
            if (request.Documents != null && request.Documents.Any())
            {
                _logger.LogInformation($"?? Processing {request.Documents.Count()} document categories");

                foreach (var catDto in request.Documents)
                {
                    _logger.LogInformation($"  ?? Category: {catDto.Category}, DocList Count: {catDto.DocList?.Count() ?? 0}");

                    var category = new DocumentCategory
                    {
                        Id = Guid.NewGuid(),
                        Category = catDto.Category ?? string.Empty,
                        ChecklistId = checklist.Id,
                        DocList = new List<Document>() // Initialize doc list
                    };

                    foreach (var docDto in catDto.DocList ?? new List<DocumentCreateDto>())
                    {

                        // Parse status string to DocumentStatus enum, only default to PendingRM if truly missing
                        DocumentStatus docStatus = DocumentStatus.PendingRM;
                        if (!string.IsNullOrEmpty(docDto.Status) && Enum.TryParse<DocumentStatus>(docDto.Status, ignoreCase: true, out var parsedStatus))
                        {
                            docStatus = parsedStatus;
                        }

                        var doc = new Document
                        {
                            Id = Guid.NewGuid(),
                            Name = docDto.Name ?? $"{category.Category} Document",
                            Status = docStatus,
                            FileUrl = docDto.FileUrl,
                            Comment = docDto.Comment,
                            DeferralReason = docDto.DeferralReason,
                            DeferralNumber = docDto.DeferralNumber,
                            CategoryId = category.Id,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        category.DocList.Add(doc);
                        _logger.LogInformation($"    ? Added doc: {doc.Name} (Status: {doc.Status})");
                    }
                    checklist.Documents.Add(category);
                    _logger.LogInformation($"  ?? Category {catDto.Category} added with {category.DocList.Count} documents");
                }
            }
            else
            {
                _logger.LogWarning($"?? No documents provided in request");
            }

            _logger.LogInformation($"?? Checklist before save - Documents count: {checklist.Documents.Count}");
            foreach (var doc in checklist.Documents)
            {
                _logger.LogInformation($"   Category: {doc.Category}, Docs: {doc.DocList.Count}");
            }

            _context.Checklists.Add(checklist);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"? Checklist saved to DB: {checklist.Id}");
            _logger.LogInformation($"?? AssignedToRMId: {checklist.AssignedToRMId}");

            // Reload the checklist with all related data
            var createdChecklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .FirstOrDefaultAsync(c => c.Id == checklist.Id);

            if (createdChecklist == null)
            {
                _logger.LogError("Failed to reload checklist {ChecklistId} after creation", checklist.Id);
                return StatusCode(500, new { message = "Checklist was created but could not be reloaded" });
            }

            _logger.LogInformation($"?? Checklist retrieved after save - Documents count: {createdChecklist.Documents.Count}");
            _logger.LogInformation($"?? RM Loaded: {(createdChecklist.AssignedToRM != null ? createdChecklist.AssignedToRM.Name : "NULL")}");
            foreach (var doc in createdChecklist.Documents)
            {
                _logger.LogInformation($"   Category: {doc.Category}, Docs: {doc.DocList.Count}");
            }

            // Debug: Log RM assignment info
            _logger.LogInformation($"?? DEBUG: AssignedToRMId = {createdChecklist.AssignedToRMId}");
            _logger.LogInformation($"?? DEBUG: AssignedToRM is null = {createdChecklist.AssignedToRM == null}");
            if (createdChecklist.AssignedToRM != null)
            {
                _logger.LogInformation($"?? DEBUG: RM ID = {createdChecklist.AssignedToRM.Id}, RM Name = {createdChecklist.AssignedToRM.Name}");
            }

            return StatusCode(201, new
            {
                message = "Checklist created successfully",
                checklist = new
                {
                    id = createdChecklist.Id,
                    dclNo = createdChecklist.DclNo,
                    customerNumber = createdChecklist.CustomerNumber,
                    customerName = createdChecklist.CustomerName,
                    loanType = createdChecklist.LoanType,
                    ibpsNo = createdChecklist.IbpsNo,
                    status = createdChecklist.Status.ToString(),
                    assignedToRMId = createdChecklist.AssignedToRMId,
                    assignedToRM = createdChecklist.AssignedToRM != null ? new
                    {
                        id = createdChecklist.AssignedToRM.Id,
                        name = createdChecklist.AssignedToRM.Name
                    } : null,
                    documents = createdChecklist.Documents.Select(dc => new
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
                            checkerComment = d.CheckerComment,
                            deferralNumber = d.DeferralNumber
                        }).ToList()
                    }).ToList(),
                    createdAt = createdChecklist.CreatedAt,
                    updatedAt = createdChecklist.UpdatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error creating checklist");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist
    [HttpGet]
    public async Task<IActionResult> GetAllChecklists()
    {
        try
        {
            var checklists = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.LockedByUser)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .ToListAsync();

            var uploadLookup = await GetLatestDocumentUploadsLookupAsync(checklists.Select(c => c.Id));

            var response = checklists.Select(c => new
                {
                    id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    ibpsNo = c.IbpsNo,
                    status = c.Status.ToString(),
                    assignedToRMId = c.AssignedToRMId,
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
                            var upload = GetLatestDocumentUpload(uploadLookup, c.Id, dc.Category, d);
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
                                checkerComment = d.CheckerComment,
                                deferralNumber = d.DeferralNumber,
                                deferralNo = d.DeferralNumber,
                                createdAt = d.CreatedAt,
                                updatedAt = d.UpdatedAt,
                                uploadedAt = BuildDocumentUploadedAt(d, upload, c.AssignedToRM),
                                uploadedBy = BuildDocumentUploader(upload, d, c.AssignedToRM),
                                uploadedByRole = BuildDocumentUploaderRole(upload, d, c.AssignedToRM)
                            };
                        }).ToList()
                    }).ToList(),
                    supportingDocs = c.SupportingDocs.Select(sd => new
                    {
                        id = sd.Id,
                        fileName = sd.FileName,
                        fileUrl = sd.FileUrl,
                        fileSize = sd.FileSize,
                        fileType = sd.FileType,
                        uploadedAt = sd.UploadedAt,
                        uploadedBy = sd.UploadedBy != null ? new { id = sd.UploadedBy.Id, name = sd.UploadedBy.Name } : null,
                        uploadedByRole = sd.UploadedByRole
                    }).ToList(),
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt
                })
                .ToList();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching checklists");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist/:id
    [HttpGet("{id}")]
    public async Task<IActionResult> GetChecklistById(Guid id)
    {
        try
        {
            _logger.LogInformation($"?? Fetching checklist: {id}");

            var checklist = await _context.Checklists
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

            if (checklist == null)
            {
                _logger.LogWarning($"? Checklist not found: {id}");
                return NotFound(new { message = "Checklist not found" });
            }

            _logger.LogInformation($"? Found checklist: {checklist.DclNo}, IBPS: {checklist.IbpsNo}, RM: {checklist.AssignedToRM?.Name}, Docs: {checklist.Documents.Count}");

            var supportingDocs = await CombineSupportingDocsWithUploadsAsync(checklist.Id, checklist);
            var uploadLookup = await GetLatestDocumentUploadsLookupAsync(new[] { checklist.Id });

            if ((!uploadLookup.TryGetValue(checklist.Id, out var checklistUploads) || checklistUploads.Count == 0) &&
                checklist.DclNo.Contains(" copy ", StringComparison.OrdinalIgnoreCase))
            {
                var baseDclNo = checklist.DclNo.Split(" copy ", StringSplitOptions.None)[0].Trim();
                var originalChecklistId = await _context.Checklists
                    .AsNoTracking()
                    .Where(c => c.DclNo == baseDclNo)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync();

                if (originalChecklistId.HasValue)
                {
                    var originalUploadLookup = await GetLatestDocumentUploadsLookupAsync(new[] { originalChecklistId.Value });
                    if (originalUploadLookup.TryGetValue(originalChecklistId.Value, out var originalUploads) && originalUploads.Count > 0)
                    {
                        uploadLookup[checklist.Id] = originalUploads;
                    }
                }
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
                generalComment = checklist.GeneralComment,
                assignedToRMId = checklist.AssignedToRMId,
                lockedByUserId = checklist.LockedByUserId,
                lockedByUserName = checklist.LockedByUser != null ? checklist.LockedByUser.Name : null,
                lockedBy = checklist.LockedByUser != null ? new { id = checklist.LockedByUser.Id, name = checklist.LockedByUser.Name } : null,
                createdBy = checklist.CreatedBy != null ? new { id = checklist.CreatedBy.Id, name = checklist.CreatedBy.Name } : null,
                assignedToRM = checklist.AssignedToRM != null ? new { id = checklist.AssignedToRM.Id, name = checklist.AssignedToRM.Name } : null,
                assignedToCoChecker = checklist.AssignedToCoChecker != null ? new { id = checklist.AssignedToCoChecker.Id, name = checklist.AssignedToCoChecker.Name } : null,
                documents = checklist.Documents.Select(dc => new
                {
                    id = dc.Id,
                    category = dc.Category,
                    docList = dc.DocList.Select(d =>
                    {
                        var upload = GetLatestDocumentUpload(uploadLookup, checklist.Id, dc.Category, d);
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
                            checkerComment = d.CheckerComment,
                            deferralReason = d.DeferralReason,
                            deferralNumber = d.DeferralNumber,
                            deferralNo = d.DeferralNumber,
                            expiryDate = d.ExpiryDate,
                            uploadedAt = BuildDocumentUploadedAt(d, upload, checklist.AssignedToRM),
                            uploadedBy = BuildDocumentUploader(upload, d, checklist.AssignedToRM),
                            uploadedByRole = BuildDocumentUploaderRole(upload, d, checklist.AssignedToRM),
                            coCreatorFiles = (d.CoCreatorFiles ?? new List<CoCreatorFile>()).Select(cf => new
                            {
                                id = cf.Id,
                                url = cf.Url,
                                name = cf.Name
                            }).ToList()
                        };
                    }).ToList()
                }).ToList(),
                supportingDocs = supportingDocs,
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
            _logger.LogError(ex, "? Error fetching checklist");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist/dcl/:dclNo
    // [HttpGet("dcl/{dclNo}")]
    // GET /api/cocreatorChecklist/dcl/:dclNo
    [HttpGet("dcl/{dclNo}")]
    public async Task<IActionResult> GetChecklistByDclNo(string dclNo)
    {
        try
        {
            _logger.LogInformation($"?? Fetching checklist by DCL: {dclNo}");

            var checklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                .FirstOrDefaultAsync(c => c.DclNo == dclNo);

            if (checklist == null)
            {
                _logger.LogWarning($"? Checklist not found: {dclNo}");
                return NotFound(new { message = "Checklist not found" });
            }

            _logger.LogInformation($"? Found checklist: {checklist.DclNo}, IBPS: {checklist.IbpsNo}, RM: {checklist.AssignedToRM?.Name}, Docs: {checklist.Documents.Count}");

            var supportingDocs = await CombineSupportingDocsWithUploadsAsync(checklist.Id, checklist);

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
                        creatorStatus = d.CreatorStatus.HasValue ? d.CreatorStatus.Value.ToString().ToLowerInvariant() : null,
                        checkerStatus = d.CheckerStatus.ToString().ToLower(),
                        rmStatus = d.RmStatus.ToString().ToLower(),
                        fileUrl = d.FileUrl,
                        comment = d.Comment,
                        checkerComment = d.CheckerComment,
                        deferralNumber = d.DeferralNumber
                    }).ToList()
                }).ToList(),
                supportingDocs = supportingDocs,
                generalComment = checklist.GeneralComment,
                finalComment = checklist.FinalComment,
                createdAt = checklist.CreatedAt,
                updatedAt = checklist.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error fetching checklist by DCL number");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/cocreatorChecklist/:id
    [HttpPut("{id}")]

    public async Task<IActionResult> UpdateChecklist(Guid id, [FromBody] UpdateChecklistRequest request)
    {
        try
        {
            var checklist = await _context.Checklists.FindAsync(id);
            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            if (request.CustomerName != null)
                checklist.CustomerName = request.CustomerName;
            if (request.LoanType != null)
                checklist.LoanType = request.LoanType;
            if (request.AssignedToRMId.HasValue)
                checklist.AssignedToRMId = request.AssignedToRMId;
            if (request.AssignedToCoCheckerId.HasValue)
                checklist.AssignedToCoCheckerId = request.AssignedToCoCheckerId;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Checklist updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating checklist");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist/:checklistId/comments
    [HttpGet("{checklistId}/comments")]
    public async Task<IActionResult> GetChecklistComments(Guid checklistId)
    {
        try
        {
            _logger.LogInformation($"📝 Fetching comments for checklist: {checklistId}");

            // Validate checklistId first
            if (checklistId == Guid.Empty)
            {
                return BadRequest(new { message = "Invalid checklist ID" });
            }

            // Check if checklist exists
            var checklistExists = await _context.Checklists.AnyAsync(c => c.Id == checklistId);
            if (!checklistExists)
            {
                _logger.LogWarning($"⚠️  Checklist not found: {checklistId}");
                return NotFound(new { message = "Checklist not found" });
            }

            // Fetch all logs for this checklist with user info eagerly loaded
            var logs = await _context.ChecklistLogs
                .Where(l => l.ChecklistId == checklistId)
                .Include(l => l.User)
                .OrderBy(l => l.Timestamp) // Chronological order: oldest to newest
                .ToListAsync();

            _logger.LogInformation($"   Found {logs.Count} comment logs for checklist {checklistId}");

            // 🔍 DEBUG: Log what we fetched from database
            for (int i = 0; i < logs.Count; i++)
            {
                var roleText = logs[i].User?.Role.ToString() ?? "NO_USER";
                var messageText = logs[i].Message;
                var messagePreview = messageText == null ? "[NULL]" : messageText.Substring(0, Math.Min(40, messageText.Length));
                _logger.LogInformation($"   DB[{i}] Role: {roleText} | User: {logs[i].User?.Name ?? "NULL"} | Message: '{messagePreview}'");
            }

            // Map logs to response format
            var result = new List<dynamic>();

            foreach (var l in logs)
            {
                try
                {
                    // 🔧 CRITICAL FIX: Handle NULL User relationships with fallbacks
                    var user = l.User;
                    var userRole = user?.Role ?? UserRole.CoCreator;  // Default to CoCreator if user null
                    var hasValidUser = user != null;
                    var mappedUserId = user?.Id ?? l.UserId;
                    var mappedUserName = user?.Name ?? "Unknown User";
                    var mappedUserEmail = user?.Email ?? string.Empty;

                    var commentData = new
                    {
                        _id = l.Id,
                        id = l.Id,
                        message = l.Message ?? "",
                        userId = new
                        {
                            id = mappedUserId,
                            name = mappedUserName,
                            email = mappedUserEmail,
                            role = userRole.ToString(),
                            _warning = hasValidUser ? null : "User lookup failed - using UserId fallback"
                        },
                        user = new
                        {
                            id = mappedUserId,
                            name = mappedUserName,
                            email = mappedUserEmail,
                            role = userRole.ToString(),
                            _warning = hasValidUser ? null : "User lookup failed - using UserId fallback"
                        },
                        roleInfo = new
                        {
                            role = userRole.ToString(),
                            color = GetRoleColor(userRole),
                            badge = GetRoleBadge(userRole),
                            _warning = hasValidUser ? null : "User lookup failed - using default role"
                        },
                        createdAt = l.Timestamp,
                        timestamp = l.Timestamp,
                        _debug = hasValidUser ? null : $"NULL User detected - UserId: {l.UserId}, Message length: {l.Message?.Length ?? 0}"
                    };
                    result.Add(commentData);

                    // Log if user load failed
                    if (!hasValidUser)
                    {
                        _logger.LogWarning($"⚠️  Log entry {l.Id} has NULL User - UserId: {l.UserId}. Message: '{l.Message?.Substring(0, Math.Min(50, l.Message?.Length ?? 0))}'");
                    }
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, $"❌ Error mapping log entry {l.Id}: {logEx.GetType().Name} - {logEx.Message}");
                    // Don't fail the entire response, log the error and continue
                    result.Add(new
                    {
                        _id = l.Id,
                        id = l.Id,
                        message = $"[Error loading comment: {logEx.Message}]",
                        userId = new { id = l.UserId, name = "Error", email = "", role = "Unknown" },
                        user = new { id = l.UserId, name = "Error", email = "", role = "Unknown" },
                        roleInfo = new { role = "Unknown", color = "#999", badge = "ERROR" },
                        createdAt = l.Timestamp,
                        timestamp = l.Timestamp,
                        error = logEx.Message
                    });
                }
            }

            _logger.LogInformation($"✅ Successfully mapped {result.Count} comments for checklist {checklistId}");

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ CRITICAL ERROR fetching comments for {checklistId}: {ex.GetType().Name} - {ex.Message}");
            if (ex.InnerException != null)
            {
                _logger.LogError(ex.InnerException, $"   Inner Exception: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
            }
            return StatusCode(500, new
            {
                message = "Internal server error fetching comments",
                error = ex.Message,
                type = ex.GetType().Name,
                checklistId = checklistId.ToString()
            });
        }
    }

    // Helper method to get role color for UI display
    private string GetRoleColor(UserRole role)
    {
        return role switch
        {
            UserRole.CoCreator => "#3B82F6", // Blue
            UserRole.RM => "#8B5CF6", // Purple
            UserRole.CoChecker => "#10B981", // Green
            UserRole.Admin => "#EF4444", // Red
            _ => "#6B7280" // Gray
        };
    }

    // Helper method to get role badge text
    private string GetRoleBadge(UserRole role)
    {
        return role switch
        {
            UserRole.CoCreator => "CREATOR",
            UserRole.RM => "RM",
            UserRole.CoChecker => "CHECKER",
            UserRole.Admin => "ADMIN",
            _ => role.ToString().ToUpper()
        };
    }

    // GET /api/cocreatorChecklist/search/customer
    [HttpGet("search/customer")]
    public async Task<IActionResult> SearchCustomer([FromQuery] string q)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return BadRequest(new { message = "Search query is required" });
            }

            var customers = await _context.Users
                .Where(u => u.Role == UserRole.Customer &&
                           (u.CustomerNumber!.Contains(q) ||
                            u.Name.Contains(q) ||
                            u.CustomerId!.Contains(q) ||
                            u.Email.Contains(q)))
                .Select(u => new
                {
                    id = u.Id,
                    name = u.Name,
                    customerNumber = u.CustomerNumber,
                    customerId = u.CustomerId,
                    email = u.Email
                })
                .Take(10)
                .ToListAsync();

            return Ok(customers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching customers");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist/creator/:creatorId
    [HttpGet("creator/{creatorId}")]
    public async Task<IActionResult> GetChecklistsByCreator(Guid creatorId)
    {
        try
        {
            var checklists = await _context.Checklists
                .Where(c => c.CreatedById == creatorId)
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.LockedByUser)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .ToListAsync();

            var uploadLookup = await GetLatestDocumentUploadsLookupAsync(checklists.Select(c => c.Id));

            var response = checklists.Select(c => new
                {
                    id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    ibpsNo = c.IbpsNo,
                    status = c.Status.ToString(),
                    assignedToRMId = c.AssignedToRMId,
                    lockedByUserId = c.LockedByUserId,
                    lockedByUserName = c.LockedByUser != null ? c.LockedByUser.Name : null,
                    lockedBy = c.LockedByUser != null ? new { id = c.LockedByUser.Id, name = c.LockedByUser.Name } : null,
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt,
                    createdBy = c.CreatedBy != null ? new { id = c.CreatedBy.Id, name = c.CreatedBy.Name, role = c.CreatedBy.Role.ToString() } : null,
                    assignedToRM = c.AssignedToRM != null ? new { id = c.AssignedToRM.Id, name = c.AssignedToRM.Name } : null,
                    assignedToCoChecker = c.AssignedToCoChecker != null ? new { id = c.AssignedToCoChecker.Id, name = c.AssignedToCoChecker.Name } : null,
                    documents = c.Documents.Select(dc => new
                    {
                        id = dc.Id,
                        category = dc.Category,
                        docList = dc.DocList.Select(d =>
                        {
                            var upload = GetLatestDocumentUpload(uploadLookup, c.Id, dc.Category, d);
                            return new
                            {
                                id = d.Id,
                                name = d.Name,
                                status = d.Status.ToString().ToLower(),
                                creatorStatus = d.CreatorStatus.HasValue ? d.CreatorStatus.Value.ToString().ToLowerInvariant() : null,
                                checkerStatus = d.CheckerStatus.ToString().ToLower(),
                                checkerComment = d.CheckerComment,
                                rmStatus = d.RmStatus.ToString().ToLower(),
                                fileUrl = d.FileUrl,
                                comment = d.Comment,
                                deferralNumber = d.DeferralNumber,
                                createdAt = d.CreatedAt,
                                updatedAt = d.UpdatedAt,
                                uploadedAt = BuildDocumentUploadedAt(d, upload, c.AssignedToRM),
                                uploadedBy = BuildDocumentUploader(upload, d, c.AssignedToRM),
                                uploadedByRole = BuildDocumentUploaderRole(upload, d, c.AssignedToRM)
                            };
                        }).ToList()
                    }).ToList()
                })
                .ToList();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching checklists by creator");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/cocreatorChecklist/:id/co-create
    [HttpPut("{id}/co-create")]

    public async Task<IActionResult> CoCreatorReview(Guid id, [FromBody] CoCreatorReviewRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var checklist = await _context.Checklists.FindAsync(id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            // Update status based on review
            checklist.Status = request.Approved ? ChecklistStatus.RMReview : ChecklistStatus.Pending;

            // Add log
            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = request.Approved ? "Approved by Co-Creator" : $"Returned by Co-Creator: {request.Comment}",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = request.Approved ? "Checklist approved" : "Checklist returned for revision",
                status = checklist.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in co-creator review");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/cocreatorChecklist/:id/co-check
    [HttpPut("{id}/co-check")]

    public async Task<IActionResult> CoCheckerApproval(Guid id, [FromBody] CoCheckerApprovalRequest request)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var checklist = await _context.Checklists.FindAsync(id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            checklist.Status = request.Approved ? ChecklistStatus.Approved : ChecklistStatus.Rejected;

            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = request.Approved ? "Approved by Co-Checker" : $"Rejected by Co-Checker: {request.Comment}",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = request.Approved ? "Checklist approved" : "Checklist rejected",
                status = checklist.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in co-checker approval");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT /api/cocreatorChecklist/update-document
    [HttpPut("update-document")]

    public async Task<IActionResult> UpdateDocumentAdmin([FromBody] AdminUpdateDocumentRequest request)
    {
        try
        {
            var document = await _context.Documents.FindAsync(request.DocumentId);
            if (document == null)
            {
                return NotFound(new { message = "Document not found" });
            }

            if (request.Status.HasValue)
                document.Status = request.Status.Value;
            if (request.Comment != null)
                document.Comment = request.Comment;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Document updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating document");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PATCH /api/cocreatorChecklist/update-status
    [HttpPatch("update-status")]

    public async Task<IActionResult> UpdateChecklistStatus([FromBody] CoCreatorSubmitToCCRequest request)
    {
        try
        {
            _logger.LogInformation($"🟢 SUBMIT TO CHECKER - Received payload:");
            _logger.LogInformation($"   DclNo: {request.DclNo}");
            _logger.LogInformation($"   Documents count: {request.Documents?.Count}");

            // Log each document's status info
            if (request.Documents != null)
            {
                foreach (var doc in request.Documents)
                {
                    _logger.LogInformation($"   📄 Doc: {doc.Name}");
                    _logger.LogInformation($"      Status: {doc.Status}");
                    _logger.LogInformation($"      CreatorStatus (from request): {doc.CreatorStatus}");
                }
            }

            if (string.IsNullOrWhiteSpace(request.DclNo))
            {
                return BadRequest(new { error = "DCL No is required" });
            }

            // Parse user ID more safely
            var userIdString = User.FindFirst("id")?.Value;
            if (string.IsNullOrWhiteSpace(userIdString) || !Guid.TryParse(userIdString, out var userId))
            {
                _logger.LogError("? Invalid or missing user ID in token");
                return Unauthorized(new { error = "Invalid user ID in token" });
            }

            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(cat => cat.DocList)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.AssignedToRM)
                .Include(c => c.CreatedBy)
                .FirstOrDefaultAsync(c => c.DclNo == request.DclNo);

            if (checklist == null)
            {
                return NotFound(new { error = "Checklist not found" });
            }

            var submittedDocumentIds = request.Documents?
                .Select(doc => doc.Id ?? doc._id)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet() ?? new HashSet<Guid>();

            if (submittedDocumentIds.Count > 0)
            {
                RemoveMissingChecklistDocuments(checklist, submittedDocumentIds);
            }

            /* ============================================================
               DOCUMENT UPDATE � UPDATE IN PLACE (NO REPLACEMENT)
            ============================================================ */
            // 🔍 CRITICAL DEBUG: Log incoming document updates
            _logger.LogWarning($"📥 SUBMISSION FROM CO-CREATOR: DCL {request.DclNo}");
            _logger.LogWarning($"   📦 Documents in request: {request.Documents?.Count ?? 0}");
            if (request.Documents != null)
            {
                foreach (var doc in request.Documents)
                {
                    _logger.LogWarning($"      - {doc.Id ?? doc._id}: Category={doc.Category}, Status={doc.Status}, CreatorStatus={doc.CreatorStatus}");
                }
            }

            if (request.Documents != null && request.Documents.Any())
            {
                // Build a map of all existing documents for quick lookup
                var existingDocsMap = new Dictionary<Guid, Document>();
                foreach (var category in checklist.Documents)
                {
                    foreach (var doc in category.DocList)
                    {
                        existingDocsMap[doc.Id] = doc;
                    }
                }

                // Process FLAT list of documents from frontend
                foreach (var docFromRequest in request.Documents)
                {
                    var docId = docFromRequest.Id ?? docFromRequest._id;

                    if (docId.HasValue && existingDocsMap.ContainsKey(docId.Value))
                    {
                        // UPDATE EXISTING DOCUMENT IN PLACE
                        var existingDoc = existingDocsMap[docId.Value];

                        _logger.LogInformation($"🔧 UPDATING: {existingDoc.Name} (ID: {docId})");
                        _logger.LogInformation($"   Old CreatorStatus: {existingDoc.CreatorStatus}");
                        _logger.LogInformation($"   Old Status: {existingDoc.Status}");

                        existingDoc.Name = docFromRequest.Name ?? existingDoc.Name;

                        // Only update status if provided
                        DocumentStatus? newStatus = null;
                        if (docFromRequest.Status != null)
                        {
                            var parsedStatus = TryParseDocumentStatus(docFromRequest.Status?.ToString());
                            if (parsedStatus.HasValue)
                            {
                                existingDoc.Status = parsedStatus.Value;
                                newStatus = parsedStatus.Value;
                                _logger.LogInformation($"   New Status: {existingDoc.Status}");
                            }
                        }

                        // CRITICAL: Set CreatorStatus from request or derive from Status
                        if (docFromRequest.CreatorStatus != null)
                        {
                            var parsedCreatorStatus = TryParseCreatorStatus(docFromRequest.CreatorStatus?.ToString());
                            if (parsedCreatorStatus.HasValue)
                            {
                                existingDoc.CreatorStatus = parsedCreatorStatus.Value;
                                _logger.LogInformation($"   ✅ Set CreatorStatus from request: {existingDoc.CreatorStatus}");
                            }
                        }
                        else if (newStatus.HasValue)
                        {
                            existingDoc.CreatorStatus = MapDocumentStatusToCreatorStatus(newStatus.Value);
                            _logger.LogInformation($"   ✅ Mapped CreatorStatus from Status: {existingDoc.CreatorStatus}");
                        }
                        else if (!existingDoc.CreatorStatus.HasValue && existingDoc.Status != DocumentStatus.Pending && existingDoc.Status != DocumentStatus.PendingRM)
                        {
                            existingDoc.CreatorStatus = MapDocumentStatusToCreatorStatus(existingDoc.Status);
                            _logger.LogInformation($"   ✅ Initialized CreatorStatus: {existingDoc.CreatorStatus}");
                        }

                        existingDoc.Comment = docFromRequest.Comment ?? existingDoc.Comment;
                        existingDoc.FileUrl = docFromRequest.FileUrl ?? existingDoc.FileUrl;
                        existingDoc.DeferralReason = docFromRequest.DeferralReason ?? existingDoc.DeferralReason;

                        if (!string.IsNullOrWhiteSpace(docFromRequest.DeferralNo))
                        {
                            existingDoc.DeferralNumber = docFromRequest.DeferralNo;
                        }

                        existingDoc.UpdatedAt = DateTime.UtcNow;
                        _context.Entry(existingDoc).State = EntityState.Modified;
                    }
                    else if (docId.HasValue)
                    {
                        _logger.LogWarning($"⚠️  Document {docId} from request not found in checklist!");
                    }
                }
            }
            else
            {
                // ⚠️ WARNING: No documents sent from frontend!
                _logger.LogWarning($"⚠️  NO DOCUMENTS IN SUBMISSION REQUEST!");
                _logger.LogWarning($"   Existing documents in checklist: {checklist.Documents.Sum(cat => cat.DocList.Count)}");
                _logger.LogWarning($"   These documents will be PRESERVED but their statuses might not update");

                // Safety: Mark all documents as viewed by creator if they don't have CreatorStatus
                foreach (var category in checklist.Documents)
                {
                    foreach (var doc in category.DocList)
                    {
                        if (!doc.CreatorStatus.HasValue)
                        {
                            doc.CreatorStatus = CreatorStatus.Sighted;
                            _context.Entry(doc).State = EntityState.Modified;
                        }
                    }
                }
            }

            /* ============================================================
               WORKFLOW STATUS (STAGE, NOT DECISION)
            ============================================================ */
            checklist.Status = ChecklistStatus.CoCheckerReview; // workflow stage

            /* ============================================================
               ASSIGN CO-CHECKER (STRICT & SAFE)
            ============================================================ */
            Guid? checkerId = request.AssignedToCoChecker;

            if (checkerId.HasValue)
            {
                var checker = await _context.Users.FirstOrDefaultAsync(u =>
                    u.Id == checkerId.Value && u.Role == UserRole.CoChecker);

                if (checker == null)
                {
                    return BadRequest(new { error = "Assigned user is not a valid Co-Checker" });
                }

                checklist.AssignedToCoCheckerId = checkerId.Value;
            }
            else if (checklist.AssignedToCoCheckerId.HasValue)
            {
                // ✅ PRESERVE existing checker if already assigned (e.g., returned from checker for rework)
                _logger.LogInformation($"✅ Preserving existing Co-Checker: {checklist.AssignedToCoCheckerId}");
                // Keep the existing checker - don't reassign to a new one
            }
            else
            {
                // Find least busy Co-Checker (only if never assigned before)
                var leastBusyChecker = await _context.Users
                    .Where(u => u.Role == UserRole.CoChecker)
                    .FirstOrDefaultAsync();

                if (leastBusyChecker == null)
                {
                    return BadRequest(new { error = "No available Co-Checker found" });
                }

                _logger.LogInformation($"✅ Assigning to new Co-Checker: {leastBusyChecker.Id}");
                checklist.AssignedToCoCheckerId = leastBusyChecker.Id;
            }

            checklist.SubmittedToCoChecker = request.SubmittedToCoChecker ?? true;

            /* ============================================================
               ENSURE CHECKER STATUS EXISTS (DOCUMENT LEVEL)
            ============================================================ */
            foreach (var category in checklist.Documents)
            {
                foreach (var doc in category.DocList)
                {
                    bool docModified = false;

                    if (doc.CheckerStatus == CheckerStatus.Pending || !Enum.IsDefined(typeof(CheckerStatus), doc.CheckerStatus))
                    {
                        doc.CheckerStatus = CheckerStatus.Pending;
                        docModified = true;
                    }
                    // Ensure deferralNumber is preserved
                    if (doc.Status == DocumentStatus.Deferred && string.IsNullOrWhiteSpace(doc.DeferralNumber))
                    {
                        doc.DeferralNumber = doc.DeferralNumber ?? null;
                        docModified = true;
                    }

                    // Mark as modified if we changed anything
                    if (docModified)
                    {
                        _context.Entry(doc).State = EntityState.Modified;
                    }
                }
            }

            var deferredValidationError = await ValidateDeferredDocumentsForCoCheckerAsync(
                checklist,
                request.Documents);
            if (deferredValidationError != null)
            {
                return BadRequest(new
                {
                    message = deferredValidationError,
                    error = deferredValidationError,
                    code = "DEFERRED_DEFERRAL_INVALID"
                });
            }

            /* ============================================================
               COMMENTS / ATTACHMENTS / AUDIT
            ============================================================ */
            checklist.FinalComment = request.FinalComment ?? checklist.FinalComment;
            checklist.GeneralComment = request.FinalComment ?? checklist.GeneralComment; // Store general comment for visibility
            checklist.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation($"💾 SaveChangesAsync called...");
            await _context.SaveChangesAsync();
            _logger.LogInformation($"✅ SaveChangesAsync completed successfully!");

            /* ============================================================
               LOG AUDIT
            ============================================================ */
            var deferralDocsCount = request.Documents?
                .Where(d => !string.IsNullOrWhiteSpace(d.DeferralNo))
                .Count() ?? 0;

            var auditLog = new AuditLog
            {
                Id = Guid.NewGuid(),
                PerformedById = userId,
                Action = "SUBMIT_TO_COCHECKER",
                Resource = "CHECKLIST",
                ResourceId = checklist.Id.ToString(),
                TargetUserId = checklist.AssignedToCoCheckerId,
                Details = $"DCL: {checklist.DclNo}, Deferral Docs: {deferralDocsCount}",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(auditLog);

            /* ============================================================
               CREATE NOTIFICATION FOR CO-CHECKER
            ============================================================ */
            if (checklist.AssignedToCoCheckerId.HasValue)
            {
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = checklist.AssignedToCoCheckerId.Value,
                    Message = $"DCL {checklist.DclNo} has been submitted for your approval",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Notifications.Add(notification);
            }

            /* ============================================================
               ADD LOG ENTRY & COMMENTS
            ============================================================ */

            // 🔧 CRITICAL: Add comment FIRST if provided, then add workflow log
            _logger.LogInformation($"🔍 CHECK: request.FinalComment = '{request.FinalComment}' | IsNullOrWhiteSpace = {string.IsNullOrWhiteSpace(request.FinalComment)}");

            if (!string.IsNullOrWhiteSpace(request.FinalComment))
            {
                var commentLog = new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = request.FinalComment,  // NO PREFIX - store raw message
                    UserId = userId,
                    ChecklistId = checklist.Id,
                    Timestamp = DateTime.UtcNow
                };
                _context.ChecklistLogs.Add(commentLog);
                _logger.LogInformation($"✅ Co-Creator comment added to DB: ChecklistId={checklist.Id}, UserId={userId}, Message='{request.FinalComment}'");
            }
            else
            {
                _logger.LogWarning($"⚠️  No Co-Creator comment provided - FinalComment is null/empty");
            }

            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Submitted to Co-Checker for review",
                UserId = userId,
                ChecklistId = checklist.Id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            await _context.SaveChangesAsync();
            _logger.LogInformation($"✅ SaveChangesAsync completed: Both comment and workflow log saved");

            _logger.LogInformation($"? Assigned Co-Checker: {checklist.AssignedToCoCheckerId}");

            // Reload checklist with all related data for response
            var updatedChecklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.CreatedBy)
                .FirstOrDefaultAsync(c => c.Id == checklist.Id);

            if (updatedChecklist == null)
            {
                _logger.LogError("Failed to reload checklist {ChecklistId} after Co-Checker submission", checklist.Id);
                return StatusCode(500, new { message = "Checklist was submitted but could not be reloaded" });
            }

            var updatedDocuments = updatedChecklist.Documents ?? new List<DocumentCategory>();

            // 🔍 LOG WHAT'S BEING RETURNED
            _logger.LogWarning($"📤 RESPONSE TO FRONTEND: {updatedDocuments.Count} document categories");
            foreach (var category in updatedDocuments)
            {
                foreach (var doc in category.DocList)
                {
                    _logger.LogWarning($"   📄 {doc.Name}: Status={doc.Status}, CreatorStatus={doc.CreatorStatus}, CheckerStatus={doc.CheckerStatus}");
                }
            }

            var uploadLookup = await GetLatestDocumentUploadsLookupAsync(new[] { updatedChecklist.Id });

            // Return flat response to avoid circular references
            return Ok(new
            {
                message = "Checklist submitted to Co-Checker successfully",
                checklist = new
                {
                    id = updatedChecklist.Id,
                    dclNo = updatedChecklist.DclNo,
                    status = updatedChecklist.Status.ToString(),
                    assignedToCoCheckerId = updatedChecklist.AssignedToCoCheckerId,
                    assignedToCoChecker = updatedChecklist.AssignedToCoChecker != null ? new
                    {
                        id = updatedChecklist.AssignedToCoChecker.Id,
                        name = updatedChecklist.AssignedToCoChecker.Name
                    } : null,
                    documents = updatedDocuments.Select(dc => new
                    {
                        id = dc.Id,
                        category = dc.Category,
                        docList = dc.DocList.Select(d =>
                        {
                            var upload = GetLatestDocumentUpload(uploadLookup, updatedChecklist.Id, dc.Category, d);
                            return new
                            {
                                id = d.Id,
                                name = d.Name,
                                status = d.Status.ToString().ToLower(),
                                creatorStatus = d.CreatorStatus.HasValue ? d.CreatorStatus.Value.ToString().ToLowerInvariant() : null,
                                checkerStatus = d.CheckerStatus.ToString().ToLower(),
                                checkerComment = d.CheckerComment,
                                rmStatus = d.RmStatus.ToString().ToLower(),
                                fileUrl = d.FileUrl,
                                comment = d.Comment,
                                deferralNumber = d.DeferralNumber,
                                deferralNo = d.DeferralNumber,
                                createdAt = d.CreatedAt,
                                updatedAt = d.UpdatedAt,
                                uploadedAt = BuildDocumentUploadedAt(d, upload, updatedChecklist.AssignedToRM),
                                uploadedBy = BuildDocumentUploader(upload, d, updatedChecklist.AssignedToRM),
                                uploadedByRole = BuildDocumentUploaderRole(upload, d, updatedChecklist.AssignedToRM)
                            };
                        }).ToList()
                    }).ToList(),
                    supportingDocs = updatedChecklist.SupportingDocs.Select(sd => new
                    {
                        id = sd.Id,
                        fileName = sd.FileName,
                        fileUrl = sd.FileUrl,
                        fileSize = sd.FileSize,
                        fileType = sd.FileType,
                        uploadedBy = sd.UploadedBy != null ? sd.UploadedBy.Name : null,
                        uploadedById = sd.UploadedById,
                        uploadedByRole = sd.UploadedByRole,
                        uploadedAt = sd.UploadedAt
                    }).ToList(),
                    updatedAt = updatedChecklist.UpdatedAt
                }
            });
        }
        catch (DbUpdateConcurrencyException dbEx)
        {
            _logger.LogError(dbEx, "?? Concurrency exception - checklist may have been modified elsewhere");
            return StatusCode(409, new
            {
                error = "Checklist has been modified. Please reload and try again.",
                message = "Concurrency conflict detected"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error updating checklist status - Type: {ExceptionType}, Message: {ExceptionMessage}, Inner: {InnerMessage}",
                ex.GetType().Name, ex.Message, ex.InnerException?.Message);

            // Return more detailed error info for debugging
            return StatusCode(500, new
            {
                error = "Failed to update checklist status",
                message = ex.Message,
                details = ex.InnerException?.Message
            });
        }
    }

    // Helper method to parse DocumentStatus
    private DocumentStatus? TryParseDocumentStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        if (Enum.TryParse<DocumentStatus>(status, ignoreCase: true, out var result))
            return result;

        return status.Trim().ToLowerInvariant() switch
        {
            "submittedforreview" => DocumentStatus.SubmittedForReview,
            "deferralrequested" => DocumentStatus.DeferralRequested,
            "pendingfromcustomer" => DocumentStatus.PendingFromCustomer,
            "pendingrm" => DocumentStatus.PendingRM,
            "pendingco" => DocumentStatus.PendingCo,
            _ => null
        };
    }

    private CreatorStatus? TryParseCreatorStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        if (Enum.TryParse<CreatorStatus>(status, ignoreCase: true, out var result))
            return result;

        return NormalizeStatusToken(status) switch
        {
            "deferralrequested" => CreatorStatus.Deferred,
            "pendingrm" => CreatorStatus.PendingRM,
            "pendingco" => CreatorStatus.PendingCo,
            _ => null
        };
    }

    private static string NormalizeStatusToken(string status)
    {
        return string.Concat(status.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private CreatorStatus? MapDocumentStatusToCreatorStatus(DocumentStatus? status)
    {
        return status switch
        {
            DocumentStatus.Submitted => CreatorStatus.Submitted,
            DocumentStatus.PendingRM => CreatorStatus.PendingRM,
            DocumentStatus.PendingCo => CreatorStatus.PendingCo,
            DocumentStatus.Deferred => CreatorStatus.Deferred,
            DocumentStatus.DeferralRequested => CreatorStatus.Deferred,
            DocumentStatus.TBO => CreatorStatus.TBO,
            DocumentStatus.Waived => CreatorStatus.Waived,
            DocumentStatus.Sighted => CreatorStatus.Sighted,
            _ => null
        };
    }

    private void RemoveMissingChecklistDocuments(Checklist checklist, HashSet<Guid> submittedDocumentIds)
    {
        var removedCount = 0;

        foreach (var category in checklist.Documents.ToList())
        {
            var documentsToRemove = category.DocList
                .Where(doc => !submittedDocumentIds.Contains(doc.Id))
                .ToList();

            foreach (var document in documentsToRemove)
            {
                category.DocList.Remove(document);
                _context.Documents.Remove(document);
                removedCount++;
                _logger.LogInformation($"🗑️ Removing document omitted from submission: {document.Name} ({document.Id})");
            }

            if (!category.DocList.Any())
            {
                checklist.Documents.Remove(category);
                _context.DocumentCategories.Remove(category);
                _logger.LogInformation($"🗑️ Removing empty document category: {category.Category}");
            }
        }

        if (removedCount > 0)
        {
            _logger.LogInformation($"✅ Removed {removedCount} document(s) missing from submission payload for checklist {checklist.DclNo}");
        }
    }

    private static string NormalizeLookupValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static bool IsDeferredStatus(DocumentStatus? status)
    {
        return status.HasValue && (
            status.Value == DocumentStatus.Deferred ||
            status.Value == DocumentStatus.DeferralRequested);
    }

    private static bool IsDeferralFullyApproved(Deferral deferral)
    {
        if (deferral.Status != DeferralStatus.Approved)
        {
            return false;
        }

        var approvers = deferral.Approvers ?? Array.Empty<Approver>();
        return !approvers.Any() || approvers.All(a => a.Approved && !a.Rejected && !a.Returned);
    }

    private async Task<string?> ValidateDeferredDocumentsForCoCheckerAsync(
        Checklist checklist,
        IEnumerable<DocumentDto>? requestDocuments = null)
    {
        var deferredEntries = (requestDocuments ?? Enumerable.Empty<DocumentDto>())
            .Where(d => IsDeferredStatus(d.Status))
            .Select(d => new
            {
                Name = d.Name ?? "Document",
                DeferralNumber = d.DeferralNo ?? d.DeferralNumber,
            })
            .ToList();

        if (!deferredEntries.Any())
        {
            deferredEntries = checklist.Documents
                .SelectMany(category => category.DocList)
                .Where(d => IsDeferredStatus(d.Status))
                .Select(d => new
                {
                    Name = d.Name ?? "Document",
                    DeferralNumber = d.DeferralNumber,
                })
                .ToList();
        }

        foreach (var deferredEntry in deferredEntries)
        {
            var deferralNumber = deferredEntry.DeferralNumber?.Trim();
            if (string.IsNullOrWhiteSpace(deferralNumber))
            {
                return $"Deferred document '{deferredEntry.Name}' must have a deferral number before submission to Co-Checker.";
            }

            var deferral = await _context.Deferrals
                .Include(d => d.Approvers)
                .FirstOrDefaultAsync(d => d.DeferralNumber == deferralNumber);

            if (deferral == null)
            {
                return $"Deferral number {deferralNumber} was not found.";
            }

            var checklistCustomerNumber = NormalizeLookupValue(checklist.CustomerNumber);
            var checklistCustomerName = NormalizeLookupValue(checklist.CustomerName);
            var deferralCustomerNumber = NormalizeLookupValue(deferral.CustomerNumber);
            var deferralCustomerName = NormalizeLookupValue(deferral.CustomerName);

            var belongsToChecklistCustomer =
                (!string.IsNullOrEmpty(checklistCustomerNumber) && checklistCustomerNumber == deferralCustomerNumber) ||
                (!string.IsNullOrEmpty(checklistCustomerName) && checklistCustomerName == deferralCustomerName);

            if (!belongsToChecklistCustomer)
            {
                return $"Deferral number {deferralNumber} does not belong to checklist customer {checklist.CustomerNumber}.";
            }

            if (!IsDeferralFullyApproved(deferral))
            {
                return $"Deferral number {deferralNumber} is not yet fully approved by all approvers, co-creator and co-checker.";
            }
        }

        return null;
    }

    // POST /api/cocreatorChecklist/:id/submit-to-rm
    [HttpPost("{id}/submit-to-rm")]

    public async Task<IActionResult> SubmitToRM(Guid id, [FromBody] SubmitToRMRequest? request = null)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var checklist = await _context.Checklists
                .Include(c => c.AssignedToRM)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var submittedDocumentIds = request?.Documents?
                .SelectMany(category => category.DocList ?? new List<DocumentUpdateInSubmitDto>())
                .Select(doc => doc.Id ?? doc._id)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet() ?? new HashSet<Guid>();

            if (submittedDocumentIds.Count > 0)
            {
                RemoveMissingChecklistDocuments(checklist, submittedDocumentIds);
            }

            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "submit to RM");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            /* ============================================================
               SAVE DOCUMENT STATUS UPDATES FROM CO-CREATOR
            ============================================================ */
            if (request?.Documents != null && request.Documents.Any())
            {
                _logger.LogInformation($"?? Updating {request.Documents.Count()} document categories before RM submission");

                foreach (var catUpdate in request.Documents)
                {
                    var category = checklist.Documents.FirstOrDefault(c => c.Category == catUpdate.Category);
                    if (category == null) continue;

                    foreach (var docUpdate in catUpdate.DocList ?? new List<DocumentUpdateInSubmitDto>())
                    {
                        var docId = docUpdate.Id ?? docUpdate._id;
                        if (!docId.HasValue) continue;

                        var doc = category.DocList.FirstOrDefault(d => d.Id == docId.Value);
                        if (doc == null) continue;

                        // Update document with CO-CREATOR changes
                        if (!string.IsNullOrEmpty(docUpdate.Comment))
                            doc.Comment = docUpdate.Comment;

                        var parsedDocumentStatus = TryParseDocumentStatus(docUpdate.Status);
                        if (parsedDocumentStatus.HasValue)
                            doc.Status = parsedDocumentStatus.Value;

                        // Preserve CreatorStatus (coStatus)
                        if (!string.IsNullOrEmpty(docUpdate.CreatorStatus))
                        {
                            var parsedCreatorStatus = TryParseCreatorStatus(docUpdate.CreatorStatus);
                            if (parsedCreatorStatus.HasValue)
                                doc.CreatorStatus = parsedCreatorStatus.Value;
                        }
                        else if (!doc.CreatorStatus.HasValue && doc.Status != DocumentStatus.Pending && doc.Status != DocumentStatus.PendingRM)
                        {
                            // Initialize CreatorStatus from DocumentStatus if not set and status is meaningful
                            doc.CreatorStatus = MapDocumentStatusToCreatorStatus(doc.Status);
                        }

                        if (!string.IsNullOrEmpty(docUpdate.FileUrl))
                            doc.FileUrl = docUpdate.FileUrl;

                        if (!string.IsNullOrEmpty(docUpdate.DeferralNumber))
                            doc.DeferralNumber = docUpdate.DeferralNumber;

                        if (!string.IsNullOrEmpty(docUpdate.DeferralReason))
                            doc.DeferralReason = docUpdate.DeferralReason;

                        doc.UpdatedAt = DateTime.UtcNow;

                        // Explicitly mark the entity as modified to ensure EF Core tracks the change
                        _context.Entry(doc).State = EntityState.Modified;

                        _logger.LogInformation($"  ? Updated doc: Status={doc.Status}, CreatorStatus={doc.CreatorStatus}, Comment={doc.Comment}");
                    }
                }
            }

            checklist.Status = ChecklistStatus.RMReview;
            checklist.LockedByUserId = null;
            checklist.UpdatedAt = DateTime.UtcNow;

            // Store creator comment for visibility across modals
            if (!string.IsNullOrWhiteSpace(request?.CreatorComment))
            {
                checklist.GeneralComment = request.CreatorComment;
            }

            // Store creator comment if provided
            if (!string.IsNullOrEmpty(request?.CreatorComment))
            {
                checklist.GeneralComment = request.CreatorComment;
                var commentLog = new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = request.CreatorComment, // Store raw message without "Creator comment:" prefix
                    UserId = userId,
                    ChecklistId = id,
                    Timestamp = DateTime.UtcNow
                };
                _context.ChecklistLogs.Add(commentLog);
                _logger.LogInformation($"✅ Co-Creator comment added to RM submission: {request.CreatorComment}");
            }

            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Submitted to RM for review",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // Create notification for RM
            if (checklist.AssignedToRMId.HasValue)
            {
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = checklist.AssignedToRMId.Value,
                    Message = $"DCL {checklist.DclNo} has been submitted for your review",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Notifications.Add(notification);
            }

            await _context.SaveChangesAsync();

            try
            {
                if (checklist.AssignedToRM != null && !string.IsNullOrWhiteSpace(checklist.AssignedToRM.Email))
                {
                    var submittedByName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "Co-Creator";
                    var emailSent = await _emailService.SendDclSubmittedToRmAsync(
                        checklist.AssignedToRM.Email,
                        checklist.AssignedToRM.Name,
                        checklist.DclNo,
                        submittedByName);

                    if (!emailSent)
                    {
                        _logger.LogWarning("⚠️ Checklist submitted to RM but email service reported no delivery for {DclNo}", checklist.DclNo);
                    }
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "⚠️ Checklist submitted to RM but email delivery failed for {DclNo}", checklist.DclNo);
            }

            _logger.LogInformation($"? Checklist {checklist.DclNo} submitted to RM with updated documents");

            // Reload the checklist with all includes to return updated data to client
            var updatedChecklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (updatedChecklist == null)
            {
                return NotFound(new { message = "Checklist not found after update" });
            }

            return Ok(new
            {
                message = "Checklist submitted to RM successfully",
                checklistId = id,
                dclNo = updatedChecklist.DclNo,
                assignedToRM = updatedChecklist.AssignedToRM != null ? new
                {
                    id = updatedChecklist.AssignedToRM.Id,
                    name = updatedChecklist.AssignedToRM.Name,
                    email = updatedChecklist.AssignedToRM.Email
                } : null,
                documentsCount = updatedChecklist.Documents?.Count ?? 0,
                updatedAt = updatedChecklist.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting to RM");
            return StatusCode(500, new
            {
                message = "Error submitting checklist to RM",
                error = ex.GetType().Name,
                details = ex.Message?.Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "'") ?? "Unknown error"
            });
        }
    }

    // POST /api/cocreatorChecklist/:id/submit-to-cochecker
    [HttpPost("{id}/submit-to-cochecker")]

    public async Task<IActionResult> SubmitToCoChecker(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var checklist = await _context.Checklists
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var deferredValidationError = await ValidateDeferredDocumentsForCoCheckerAsync(checklist);
            if (deferredValidationError != null)
            {
                return BadRequest(new
                {
                    message = deferredValidationError,
                    error = deferredValidationError,
                    code = "DEFERRED_DEFERRAL_INVALID"
                });
            }

            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "submit to Co-Checker");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            checklist.Status = ChecklistStatus.CoCheckerReview;
            checklist.LockedByUserId = null;
            checklist.UpdatedAt = DateTime.UtcNow;

            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Submitted to Co-Checker for final approval",
                UserId = userId,
                ChecklistId = id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // Create notification for CoChecker
            if (checklist.AssignedToCoCheckerId.HasValue)
            {
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = checklist.AssignedToCoCheckerId.Value,
                    Message = $"DCL {checklist.DclNo} has been submitted for your approval",
                    Read = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Notifications.Add(notification);
            }

            await _context.SaveChangesAsync();

            try
            {
                if (checklist.AssignedToCoChecker != null && !string.IsNullOrWhiteSpace(checklist.AssignedToCoChecker.Email))
                {
                    var submittedByName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "Co-Creator";
                    var emailSent = await _emailService.SendDclSubmittedToCoCheckerAsync(
                        checklist.AssignedToCoChecker.Email,
                        checklist.AssignedToCoChecker.Name,
                        checklist.DclNo,
                        submittedByName);

                    if (!emailSent)
                    {
                        _logger.LogWarning("⚠️ Checklist submitted to Co-Checker but email service reported no delivery for {DclNo}", checklist.DclNo);
                    }
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "⚠️ Checklist submitted to Co-Checker but email delivery failed for {DclNo}", checklist.DclNo);
            }

            // Reload the checklist with all includes to return updated data to client
            var updatedChecklist = await _context.Checklists
                .Include(c => c.CreatedBy)
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (updatedChecklist == null)
            {
                return NotFound(new { message = "Checklist not found after update" });
            }

            return Ok(new
            {
                message = "Checklist submitted to Co-Checker successfully",
                checklistId = id,
                dclNo = updatedChecklist.DclNo,
                status = updatedChecklist.Status.ToString(),
                assignedToCoChecker = updatedChecklist.AssignedToCoChecker != null ? new
                {
                    id = updatedChecklist.AssignedToCoChecker.Id,
                    name = updatedChecklist.AssignedToCoChecker.Name,
                    email = updatedChecklist.AssignedToCoChecker.Email
                } : null,
                documentsCount = updatedChecklist.Documents?.Count ?? 0,
                updatedAt = updatedChecklist.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting to co-checker");
            return StatusCode(500, new
            {
                message = "Error submitting checklist to Co-Checker",
                error = ex.GetType().Name,
                details = ex.Message?.Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "'") ?? "Unknown error"
            });
        }
    }

    // PATCH /api/cocreatorChecklist/:checklistId/checklist-status
    [HttpPatch("{checklistId}/checklist-status")]
    public async Task<IActionResult> UpdateStatus(Guid checklistId, [FromBody] UpdateStatusRequest request)
    {
        try
        {
            var checklist = await _context.Checklists.FindAsync(checklistId);
            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            checklist.Status = request.Status;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Status updated successfully", status = checklist.Status.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating status");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST /api/cocreatorChecklist/:id/documents
    [HttpPost("{id}/documents")]

    public async Task<IActionResult> AddDocument(Guid id, [FromBody] AddDocumentRequest request)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "add a document");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            var category = checklist.Documents.FirstOrDefault(dc => dc.Category == request.Category);

            var document = new Document
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Category = request.Category ?? string.Empty,
                Status = DocumentStatus.PendingRM,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (category == null)
            {
                category = new DocumentCategory
                {
                    Id = Guid.NewGuid(),
                    Category = request.Category ?? string.Empty,
                    ChecklistId = id
                };
                _context.DocumentCategories.Add(category);
                await _context.SaveChangesAsync();
            }

            document.CategoryId = category.Id;
            _context.Documents.Add(document);
            await _context.SaveChangesAsync();

            return StatusCode(201, new
            {
                message = "Document added successfully",
                document = new
                {
                    id = document.Id,
                    name = document.Name,
                    category = document.Category,
                    status = document.Status.ToString()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding document");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PATCH /api/cocreatorChecklist/:id/documents/:docId
    [HttpPatch("{id}/documents/{docId}")]

    public async Task<IActionResult> UpdateDocument(Guid id, Guid docId, [FromBody] UpdateDocumentRequest request)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "update a document");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            var document = checklist.Documents
                .SelectMany(dc => dc.DocList)
                .FirstOrDefault(d => d.Id == docId);

            if (document == null)
            {
                return NotFound(new { message = "Document not found" });
            }

            if (request.Status.HasValue)
                document.Status = request.Status.Value;
            if (request.CheckerComment != null)
                document.CheckerComment = request.CheckerComment;
            if (request.CreatorComment != null)
                document.CreatorComment = request.CreatorComment;
            if (request.RmComment != null)
                document.RmComment = request.RmComment;
            if (request.FileUrl != null)
                document.FileUrl = request.FileUrl;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Document updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating document");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // DELETE /api/cocreatorChecklist/:id/documents/:docId
    [HttpDelete("{id}/documents/{docId}")]

    public async Task<IActionResult> DeleteDocument(Guid id, Guid docId)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "delete a document");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            var document = checklist.Documents
                .SelectMany(dc => dc.DocList)
                .FirstOrDefault(d => d.Id == docId);

            if (document == null)
            {
                return NotFound(new { message = "Document not found" });
            }

            _context.Documents.Remove(document);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Document deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting document");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST /api/cocreatorChecklist/:id/documents/:docId/upload
    [HttpPost("{id}/documents/{docId}/upload")]

    public async Task<IActionResult> UploadDocumentFile(Guid id, Guid docId, IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file provided" });
            }

            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "upload a document file");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            var document = checklist.Documents
                .SelectMany(dc => dc.DocList)
                .FirstOrDefault(d => d.Id == docId);

            if (document == null)
            {
                return NotFound(new { message = "Document not found" });
            }

            // Save file
            var uploadsPath = Path.Combine(_environment.ContentRootPath, "uploads", id.ToString());
            var fileName = await FileUploadHelper.SaveFileAsync(file, uploadsPath);

            // Update document with file URL
            document.FileUrl = $"/uploads/{id}/{fileName}";
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "File uploaded successfully",
                fileUrl = document.FileUrl
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST /api/cocreatorChecklist/:id/upload - Accepts both single file and multiple files
    [HttpPost("{id}/upload")]
    public async Task<IActionResult> UploadSupportingDocs(Guid id, IFormFile? file, [FromForm] List<IFormFile>? files)
    {
        try
        {
            // Handle single file upload
            if (file != null && file.Length > 0)
            {
                files = new List<IFormFile> { file };
            }

            if (files == null || files.Count == 0)
            {
                return BadRequest(new { message = "No files provided" });
            }

            var checklist = await _context.Checklists
                .Include(c => c.SupportingDocs)
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            // Get current user info
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var lockConflict = await EnsureChecklistEditableByCurrentUserAsync(checklist, userId, "upload supporting documents");
            if (lockConflict != null)
            {
                return lockConflict;
            }

            var userName = User.FindFirst("name")?.Value ?? "Unknown User";
            var userRole = User.FindFirst("role")?.Value ?? "co_creator";

            var uploadsPath = Path.Combine(_environment.ContentRootPath, "uploads", id.ToString());
            var uploadedDocs = new List<object>();

            foreach (var f in files)
            {
                var fileName = await FileUploadHelper.SaveFileAsync(f, uploadsPath);
                var fileUrl = $"/uploads/{id}/{fileName}";

                // Create SupportingDoc entity in the database
                var supportingDoc = new SupportingDoc
                {
                    Id = Guid.NewGuid(),
                    FileName = f.FileName,
                    FileUrl = fileUrl,
                    FileSize = f.Length,
                    FileType = f.ContentType,
                    UploadedById = userId,
                    UploadedByRole = userRole,
                    UploadedAt = DateTime.UtcNow,
                    ChecklistId = id
                };

                _context.SupportingDocs.Add(supportingDoc);
                checklist.SupportingDocs.Add(supportingDoc);

                uploadedDocs.Add(new
                {
                    id = supportingDoc.Id,
                    fileName = supportingDoc.FileName,
                    fileUrl = supportingDoc.FileUrl,
                    fileSize = supportingDoc.FileSize,
                    fileType = supportingDoc.FileType,
                    uploadedBy = userName,
                    uploadedById = userId,
                    uploadedByRole = userRole,
                    uploadedAt = supportingDoc.UploadedAt
                });
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Supporting documents uploaded successfully",
                supportingDocs = uploadedDocs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading supporting documents");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist/:id/download
    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadChecklist(Guid id)
    {
        try
        {
            var checklist = await _context.Checklists
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            // TODO: Implement archive/zip creation of all checklist documents
            return Ok(new
            {
                message = "Download functionality not yet implemented",
                checklistId = id,
                dclNo = checklist.DclNo
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading checklist");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/cocreatorChecklist/cocreator/active
    [HttpGet("cocreator/active")]

    public async Task<IActionResult> GetActiveChecklists()
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);

            var activeChecklists = await _context.Checklists
                .Where(c => c.CreatedById == userId &&
                           (c.Status == ChecklistStatus.Pending ||
                            c.Status == ChecklistStatus.CoCreatorReview ||
                            c.Status == ChecklistStatus.RMReview))
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.LockedByUser)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            return Ok(activeChecklists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching active checklists");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    [HttpPost("{id}/lock")]
    public async Task<IActionResult> LockChecklist(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var userName = User.FindFirst("name")?.Value ?? User.FindFirst("unique_name")?.Value ?? "Current User";

            var checklist = await _context.Checklists
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            if (checklist.LockedByUserId.HasValue && checklist.LockedByUserId.Value != userId)
            {
                return Conflict(BuildLockConflictResponse(checklist, "This checklist is already locked by another user."));
            }

            checklist.LockedByUserId = userId;
            checklist.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Checklist locked successfully.",
                checklistId = checklist.Id,
                lockedByUserId = userId,
                lockedByUserName = userName,
                lockedBy = new { id = userId, name = userName }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error locking checklist {ChecklistId}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    [HttpPost("{id}/unlock")]
    public async Task<IActionResult> UnlockChecklist(Guid id)
    {
        try
        {
            var userId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty);
            var checklist = await _context.Checklists
                .Include(c => c.LockedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            if (!checklist.LockedByUserId.HasValue)
            {
                return Ok(new { message = "Checklist is already unlocked.", checklistId = checklist.Id });
            }

            if (checklist.LockedByUserId.Value != userId)
            {
                return Conflict(BuildLockConflictResponse(checklist, "Only the user holding this checklist lock can unlock it."));
            }

            checklist.LockedByUserId = null;
            checklist.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Checklist unlocked successfully.", checklistId = checklist.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unlocking checklist {ChecklistId}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // Helper method to combine supporting docs from both legacy SupportingDocs table and Uploads table
    private IEnumerable<object> CombineSupportingDocs(Checklist checklist)
    {
        var combinedDocs = new List<object>();

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

        return combinedDocs;
    }

    private async Task<IActionResult?> EnsureChecklistEditableByCurrentUserAsync(Checklist checklist, Guid userId, string actionDescription)
    {
        if (checklist.LockedByUserId.HasValue && checklist.LockedByUserId.Value != userId)
        {
            if (checklist.LockedByUser == null)
            {
                await _context.Entry(checklist).Reference(c => c.LockedByUser).LoadAsync();
            }

            return Conflict(BuildLockConflictResponse(
                checklist,
                $"This checklist is currently being edited by another user and cannot be used to {actionDescription}."));
        }

        if (!checklist.LockedByUserId.HasValue)
        {
            checklist.LockedByUserId = userId;
        }

        return null;
    }

    private object BuildLockConflictResponse(Checklist checklist, string message)
    {
        return new
        {
            message,
            lockedByUserId = checklist.LockedByUserId,
            lockedByUserName = checklist.LockedByUser?.Name,
            lockedBy = checklist.LockedByUser != null ? new { id = checklist.LockedByUser.Id, name = checklist.LockedByUser.Name } : null
        };
    }

    private async Task<Dictionary<Guid, List<Upload>>> GetLatestDocumentUploadsLookupAsync(IEnumerable<Guid> checklistIds)
    {
        var checklistIdList = checklistIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (checklistIdList.Count == 0)
        {
            return new Dictionary<Guid, List<Upload>>();
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
            .ToDictionary(checklistGroup => checklistGroup.Key, checklistGroup => checklistGroup.ToList());
    }

    private static Upload? GetLatestDocumentUpload(
        IReadOnlyDictionary<Guid, List<Upload>> uploadLookup,
        Guid checklistId,
        string? category,
        Document document)
    {
        if (!uploadLookup.TryGetValue(checklistId, out var checklistUploads) || checklistUploads.Count == 0)
        {
            return null;
        }

        var exactMatch = checklistUploads.FirstOrDefault(u => u.DocumentId == document.Id);
        if (exactMatch != null)
        {
            return exactMatch;
        }

        var normalizedDocumentName = (document.Name ?? string.Empty).Trim();
        var normalizedCategory = (category ?? document.Category ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalizedDocumentName))
        {
            return null;
        }

        return checklistUploads.FirstOrDefault(u =>
            string.Equals((u.DocumentName ?? string.Empty).Trim(), normalizedDocumentName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((u.Category ?? string.Empty).Trim(), normalizedCategory, StringComparison.OrdinalIgnoreCase));
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

    // Async helper method that includes Uploads table
    private async Task<List<object>> CombineSupportingDocsWithUploadsAsync(Guid checklistId, Checklist checklist)
    {
        var combinedDocs = new List<object>();

        // Fetch supporting documents from Uploads table
        var uploads = await _context.Uploads
            .Where(u => u.ChecklistId == checklistId && u.Category == "Supporting Documents")
            .ToListAsync();

        _logger.LogInformation($"📎 Found {uploads.Count} supporting documents from Uploads table for checklist {checklistId}");

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

        _logger.LogInformation($"📎 Total supporting docs to return: {combinedDocs.Count}");

        return combinedDocs;
    }
}

// ============================================
// REQUEST/RESPONSE MODELS
// ============================================

public class SaveChecklistDraftRequest
{
    public Guid? ChecklistId { get; set; }
    public string? DraftDataJson { get; set; }
    public bool? IsDraft { get; set; }
    public DateTime? DraftExpiresAt { get; set; }
}

public class UpdateChecklistWithDocsRequest
{
    public string? Category { get; set; }
    public List<DocumentDto>? Documents { get; set; }
    public ChecklistStatus? Status { get; set; }
    public string? GeneralComment { get; set; }
}

public class DocumentDto
{
    public Guid? Id { get; set; }
    public Guid? _id { get; set; }
    public Guid? DocumentId { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public DocumentStatus? Status { get; set; }
    public CreatorStatus? CreatorStatus { get; set; }
    public string? Comment { get; set; }
    public string? FileUrl { get; set; }
    public string? DeferralReason { get; set; }
    public string? DeferralNumber { get; set; }
    public string? DeferralNo { get; set; }
    public List<DocumentDto>? DocList { get; set; }
}

public class CreateChecklistRequest
{
    public Guid? CustomerId { get; set; }
    public string? CustomerNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? LoanType { get; set; }
    public Guid? AssignedToRMId { get; set; }
    public List<DocumentCategoryCreateDto>? Documents { get; set; }
    public string? IbpsNo { get; set; }
}

public class DocumentCategoryCreateDto
{
    public string? Category { get; set; }
    public List<DocumentCreateDto>? DocList { get; set; }
}

public class DocumentCreateDto
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? FileUrl { get; set; }
    public string? Comment { get; set; }
    public string? DeferralReason { get; set; }
    public string? DeferralNumber { get; set; }
}

public class UpdateChecklistRequest
{
    public string? CustomerName { get; set; }
    public string? LoanType { get; set; }
    public Guid? AssignedToRMId { get; set; }
    public Guid? AssignedToCoCheckerId { get; set; }
}

public class CoCreatorReviewRequest
{
    public bool Approved { get; set; }
    public string? Comment { get; set; }
}

public class CoCheckerApprovalRequest
{
    public bool Approved { get; set; }
    public string? Comment { get; set; }
}

public class AdminUpdateDocumentRequest
{
    public Guid DocumentId { get; set; }
    public DocumentStatus? Status { get; set; }
    public string? Comment { get; set; }
}

public class CoCreatorSubmitToCCRequest
{
    public string? DclNo { get; set; }
    public List<DocumentDto>? Documents { get; set; }
    public bool? SubmittedToCoChecker { get; set; }
    public Guid? AssignedToCoChecker { get; set; }
    public string? FinalComment { get; set; }
}

public class UpdateStatusRequest
{
    public ChecklistStatus Status { get; set; }
}

public class AddDocumentRequest
{
    public string? Category { get; set; }
    public string? Name { get; set; }
}

public class UpdateDocumentRequest
{
    public DocumentStatus? Status { get; set; }
    public string? CheckerComment { get; set; }
    public string? CreatorComment { get; set; }
    public string? RmComment { get; set; }
    public string? FileUrl { get; set; }
}

