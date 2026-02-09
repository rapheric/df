using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.Models;
using NCBA.DCL.Services;
using NCBA.DCL.Helpers;

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
                    foreach (var docUpdate in updatedCat.DocList)
                    {
                        var docId = docUpdate.DocumentId ?? docUpdate.Id;
                        var doc = cat.DocList.FirstOrDefault(d => d.Id == docId);
                        if (doc == null) continue;
                        if (docUpdate.FileUrl != null) doc.FileUrl = docUpdate.FileUrl;
                        if (docUpdate.Comment != null) doc.Comment = docUpdate.Comment;
                        if (docUpdate.Status.HasValue) doc.Status = docUpdate.Status.Value;
                        if (docUpdate.DeferralReason != null) doc.DeferralReason = docUpdate.DeferralReason;
                        if (docUpdate.DeferralNumber != null) doc.DeferralNumber = docUpdate.DeferralNumber;
                    }
                }
            }
            if (request.Status.HasValue) checklist.Status = request.Status.Value;
            if (!string.IsNullOrEmpty(request.GeneralComment))
            {
                checklist.Logs.Add(new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = $"Co-Creator comment: {request.GeneralComment}",
                    UserId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty),
                    Timestamp = DateTime.UtcNow
                });
            }
            checklist.Logs.Add(new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Checklist updated by Co-Creator",
                UserId = Guid.Parse(User.FindFirst("id")?.Value ?? string.Empty),
                Timestamp = DateTime.UtcNow
            });
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
                Documents = original.Documents.Select(cat => new DocumentCategory
                {
                    Id = Guid.NewGuid(),
                    Category = cat.Category,
                    ChecklistId = cat.ChecklistId,
                    DocList = cat.DocList.Select(doc => new Document
                    {
                        Id = Guid.NewGuid(),
                        Name = doc.Name,
                        Status = doc.Status,
                        FileUrl = doc.FileUrl,
                        ExpiryDate = doc.ExpiryDate,
                        Comment = doc.Comment,
                        DeferralReason = doc.DeferralReason,
                        DeferralNumber = doc.DeferralNumber,
                        CheckerStatus = doc.CheckerStatus,

                        RmStatus = doc.RmStatus,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    }).ToList()
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
            _context.Checklists.Add(revived);
            await _context.SaveChangesAsync();
            // TODO: Audit log (implement as needed)
            return StatusCode(201, new { message = $"Checklist revived as {newDclNo}", checklist = revived });
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
                CustomerNumber = request.CustomerNumber,
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
                        Category = catDto.Category,
                        ChecklistId = checklist.Id,
                        DocList = new List<Document>() // Initialize doc list
                    };

                    foreach (var docDto in catDto.DocList ?? new List<DocumentCreateDto>())
                    {
                        // Parse status string to DocumentStatus enum, default to PendingRM if parsing fails
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
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            fileUrl = d.FileUrl,
                            comment = d.Comment,
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
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .Include(c => c.SupportingDocs)
                    .ThenInclude(sd => sd.UploadedBy)
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Select(c => new
                {
                    id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    ibpsNo = c.IbpsNo,
                    status = c.Status.ToString(),
                    assignedToRMId = c.AssignedToRMId,
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
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            fileUrl = d.FileUrl,
                            comment = d.Comment,
                            deferralNumber = d.DeferralNumber,
                            deferralNo = d.DeferralNumber
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
                .ToListAsync();

            return Ok(checklists);
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
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                        .ThenInclude(d => d.CoCreatorFiles)
                .Include(c => c.SupportingDocs)
                .Include(c => c.Logs)
                    .ThenInclude(l => l.User)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                _logger.LogWarning($"? Checklist not found: {id}");
                return NotFound(new { message = "Checklist not found" });
            }

            _logger.LogInformation($"? Found checklist: {checklist.DclNo}, IBPS: {checklist.IbpsNo}, RM: {checklist.AssignedToRM?.Name}, Docs: {checklist.Documents.Count}");

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
                        rmStatus = d.RmStatus.ToString().ToLower(),
                        fileUrl = d.FileUrl,
                        comment = d.Comment,
                        deferralReason = d.DeferralReason,
                        deferralNumber = d.DeferralNumber,
                        coCreatorFiles = (d.CoCreatorFiles ?? new List<CoCreatorFile>()).Select(cf => new
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
                        rmStatus = d.RmStatus.ToString().ToLower(),
                        fileUrl = d.FileUrl,
                        comment = d.Comment,
                        deferralNumber = d.DeferralNumber
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
            var logs = await _context.ChecklistLogs
                .Where(l => l.ChecklistId == checklistId)
                .Include(l => l.User)
                .OrderByDescending(l => l.Timestamp)
                .Select(l => new
                {
                    _id = l.Id,
                    id = l.Id,
                    message = l.Message,
                    userId = l.User != null ? new
                    {
                        id = l.User.Id,
                        name = l.User.Name,
                        role = l.User.Role.ToString() // Include role as string representation
                    } : null,
                    user = l.User != null ? new
                    {
                        id = l.User.Id,
                        name = l.User.Name,
                        role = l.User.Role.ToString()
                    } : null,
                    createdAt = l.Timestamp,
                    timestamp = l.Timestamp
                })
                .ToListAsync();

            return Ok(logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching comments");
            return StatusCode(500, new { message = "Internal server error" });
        }
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
                .Include(c => c.AssignedToRM)
                .Include(c => c.AssignedToCoChecker)
                .Include(c => c.Documents)
                    .ThenInclude(dc => dc.DocList)
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Select(c => new
                {
                    id = c.Id,
                    dclNo = c.DclNo,
                    customerNumber = c.CustomerNumber,
                    customerName = c.CustomerName,
                    loanType = c.LoanType,
                    ibpsNo = c.IbpsNo,
                    status = c.Status.ToString(),
                    assignedToRMId = c.AssignedToRMId,
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt,
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
                            rmStatus = d.RmStatus.ToString().ToLower(),
                            fileUrl = d.FileUrl,
                            comment = d.Comment,
                            deferralNumber = d.DeferralNumber
                        }).ToList()
                    }).ToList()
                })
                .ToListAsync();

            return Ok(checklists);
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

            /* ============================================================
               DOCUMENT UPDATE � UPDATE IN PLACE (NO REPLACEMENT)
            ============================================================ */
            if (request.Documents != null && request.Documents.Any())
            {
                var existingDocsMap = new Dictionary<Guid, Document>();

                foreach (var cat in checklist.Documents)
                {
                    foreach (var doc in cat.DocList)
                    {
                        existingDocsMap[doc.Id] = doc;
                    }
                }

                foreach (var docFromRequest in request.Documents)
                {
                    var docId = docFromRequest.Id ?? docFromRequest._id;

                    if (docId.HasValue && existingDocsMap.ContainsKey(docId.Value))
                    {
                        // UPDATE EXISTING DOCUMENT IN PLACE
                        var existingDoc = existingDocsMap[docId.Value];

                        existingDoc.Name = docFromRequest.Name ?? existingDoc.Name;
                        existingDoc.Status = TryParseDocumentStatus(docFromRequest.Status?.ToString() ?? existingDoc.Status.ToString()) ?? existingDoc.Status;
                        existingDoc.Comment = docFromRequest.Comment ?? existingDoc.Comment;
                        existingDoc.FileUrl = docFromRequest.FileUrl ?? existingDoc.FileUrl;
                        existingDoc.DeferralReason = docFromRequest.DeferralReason ?? existingDoc.DeferralReason;

                        // Update deferral number if provided
                        if (!string.IsNullOrWhiteSpace(docFromRequest.DeferralNo))
                        {
                            existingDoc.DeferralNumber = docFromRequest.DeferralNo;
                        }

                        existingDoc.UpdatedAt = DateTime.UtcNow;

                        // Mark for update
                        _context.Documents.Update(existingDoc);
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
            else
            {
                // Find least busy Co-Checker (optional - you may implement this logic)
                var leastBusyChecker = await _context.Users
                    .Where(u => u.Role == UserRole.CoChecker)
                    .FirstOrDefaultAsync();

                if (leastBusyChecker == null)
                {
                    return BadRequest(new { error = "No available Co-Checker found" });
                }

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
                    if (doc.CheckerStatus == CheckerStatus.Pending || !Enum.IsDefined(typeof(CheckerStatus), doc.CheckerStatus))
                    {
                        doc.CheckerStatus = CheckerStatus.Pending;
                    }
                    // Ensure deferralNumber is preserved
                    if (doc.Status == DocumentStatus.Deferred && string.IsNullOrWhiteSpace(doc.DeferralNumber))
                    {
                        doc.DeferralNumber = doc.DeferralNumber ?? null;
                    }
                }
            }

            /* ============================================================
               COMMENTS / ATTACHMENTS / AUDIT
            ============================================================ */
            checklist.FinalComment = request.FinalComment ?? "";
            checklist.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

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
               ADD LOG ENTRY
            ============================================================ */
            var log = new ChecklistLog
            {
                Id = Guid.NewGuid(),
                Message = "Submitted to Co-Checker for review",
                UserId = userId,
                ChecklistId = checklist.Id,
                Timestamp = DateTime.UtcNow
            };
            _context.ChecklistLogs.Add(log);

            // If CoCreator provided a comment, add it as a separate log entry
            if (!string.IsNullOrWhiteSpace(request.FinalComment))
            {
                var commentLog = new ChecklistLog
                {
                    Id = Guid.NewGuid(),
                    Message = $"CoCreator Comment: {request.FinalComment}",
                    UserId = userId,
                    ChecklistId = checklist.Id,
                    Timestamp = DateTime.UtcNow
                };
                _context.ChecklistLogs.Add(commentLog);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation($"? Assigned Co-Checker: {checklist.AssignedToCoCheckerId}");

            return Ok(new
            {
                message = "Checklist submitted to Co-Checker successfully",
                checklist = new
                {
                    id = checklist.Id,
                    dclNo = checklist.DclNo,
                    status = checklist.Status.ToString(),
                    assignedToCoCheckerId = checklist.AssignedToCoCheckerId,
                    assignedToCoChecker = checklist.AssignedToCoChecker != null ? new
                    {
                        id = checklist.AssignedToCoChecker.Id,
                        name = checklist.AssignedToCoChecker.Name
                    } : null,
                    finalComment = checklist.FinalComment,
                    updatedAt = checklist.UpdatedAt
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
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
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

                        if (!string.IsNullOrEmpty(docUpdate.Status) && Enum.TryParse<DocumentStatus>(docUpdate.Status, ignoreCase: true, out var status))
                            doc.Status = status;

                        if (!string.IsNullOrEmpty(docUpdate.FileUrl))
                            doc.FileUrl = docUpdate.FileUrl;

                        if (!string.IsNullOrEmpty(docUpdate.DeferralNumber))
                            doc.DeferralNumber = docUpdate.DeferralNumber;

                        if (!string.IsNullOrEmpty(docUpdate.DeferralReason))
                            doc.DeferralReason = docUpdate.DeferralReason;

                        doc.UpdatedAt = DateTime.UtcNow;

                        _logger.LogInformation($"  ? Updated doc: Status={doc.Status}, Comment={doc.Comment}");
                    }
                }
            }

            checklist.Status = ChecklistStatus.RMReview;
            checklist.UpdatedAt = DateTime.UtcNow;

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

            _logger.LogInformation($"? Checklist {checklist.DclNo} submitted to RM with updated documents");

            // Return only essential data to avoid serialization issues with full object graph
            return Ok(new
            {
                message = "Checklist submitted to RM successfully",
                checklistId = id,
                dclNo = checklist.DclNo,
                status = checklist.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting to RM");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
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
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            checklist.Status = ChecklistStatus.CoCheckerReview;
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

            // Return only essential data to avoid serialization issues
            return Ok(new
            {
                message = "Checklist submitted to Co-Checker successfully",
                checklistId = id,
                dclNo = checklist.DclNo,
                status = checklist.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting to co-checker");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
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
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var category = checklist.Documents.FirstOrDefault(dc => dc.Category == request.Category);

            var document = new Document
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Category = request.Category,
                Status = DocumentStatus.PendingRM,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (category == null)
            {
                category = new DocumentCategory
                {
                    Id = Guid.NewGuid(),
                    Category = request.Category,
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
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
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
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
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
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
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

    // POST /api/cocreatorChecklist/:id/upload
    [HttpPost("{id}/upload")]

    public async Task<IActionResult> UploadSupportingDocs(Guid id, List<IFormFile> files)
    {
        try
        {
            if (files == null || files.Count == 0)
            {
                return BadRequest(new { message = "No files provided" });
            }

            var checklist = await _context.Checklists.FindAsync(id);
            if (checklist == null)
            {
                return NotFound(new { message = "Checklist not found" });
            }

            var uploadsPath = Path.Combine(_environment.ContentRootPath, "uploads", id.ToString());
            var uploadedFiles = new List<object>();

            foreach (var file in files)
            {
                var fileName = await FileUploadHelper.SaveFileAsync(file, uploadsPath);
                uploadedFiles.Add(new
                {
                    name = file.FileName,
                    url = $"/uploads/{id}/{fileName}"
                });
            }

            return Ok(new
            {
                message = "Files uploaded successfully",
                files = uploadedFiles
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading files");
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

public class SubmitToRMRequest
{
    public List<DocumentCategoryUpdateDto>? Documents { get; set; }
    public string? CreatorComment { get; set; }
}

public class DocumentCategoryUpdateDto
{
    public string? Category { get; set; }
    public List<DocumentUpdateInSubmitDto>? DocList { get; set; }
}

public class DocumentUpdateInSubmitDto
{
    public Guid? Id { get; set; }
    public Guid? _id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? Comment { get; set; }
    public string? FileUrl { get; set; }
    public string? DeferralReason { get; set; }
    public string? DeferralNumber { get; set; }
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

