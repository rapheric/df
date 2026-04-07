using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBA.DCL.Data;
using NCBA.DCL.Models;
using NCBA.DCL.DTOs;
using Microsoft.EntityFrameworkCore;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/uploads")]
[Authorize]
public class UploadsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<UploadsController> _logger;

    public UploadsController(ApplicationDbContext context, IWebHostEnvironment env, ILogger<UploadsController> logger)
    {
        _context = context;
        _env = env;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Test() => Ok(new { message = "Upload API is working!" });

    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Upload([FromForm] NCBA.DCL.DTOs.FileUploadDto dto)
    {
        try
        {
            if (dto.File == null || dto.File.Length == 0)
                return BadRequest(new { success = false, error = "No file uploaded" });

            var checklistIdStr = dto.ChecklistId;
            var documentIdStr = dto.DocumentId;
            var documentName = dto.DocumentName ?? dto.File.FileName;
            var category = dto.Category;

            _logger.LogInformation($"📤 Upload request received:");
            _logger.LogInformation($"   - ChecklistId (raw): '{checklistIdStr}'");
            _logger.LogInformation($"   - DocumentId (raw): '{documentIdStr}'");
            _logger.LogInformation($"   - DocumentName: '{documentName}'");
            _logger.LogInformation($"   - Category: '{category}'");
            _logger.LogInformation($"   - FileName: '{dto.File.FileName}'");

            // Read file as binary
            byte[] fileData;
            using (var stream = new MemoryStream())
            {
                await dto.File.CopyToAsync(stream);
                fileData = stream.ToArray();
            }

            var userName =
                User?.FindFirst("name")?.Value ??
                User?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name")?.Value ??
                User?.Identity?.Name ??
                "Unknown User";

            // Get user's role from claims
            var role = User?.FindFirst("role")?.Value ?? User?.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")?.Value ?? "RM";

            var parsedChecklistId = Guid.TryParse(checklistIdStr, out var cId) ? cId : (Guid?)null;
            var parsedDocumentId = Guid.TryParse(documentIdStr, out var dId) ? dId : (Guid?)null;

            _logger.LogInformation($"📤 Parsed IDs:");
            _logger.LogInformation($"   - ChecklistId (parsed): {parsedChecklistId?.ToString() ?? "NULL"}");
            _logger.LogInformation($"   - DocumentId (parsed): {parsedDocumentId?.ToString() ?? "NULL"}");

            var upload = new Upload
            {
                Id = Guid.NewGuid(),
                ChecklistId = parsedChecklistId,
                DocumentId = parsedDocumentId,
                DocumentName = documentName,
                Category = category,
                FileName = dto.File.FileName,
                FileData = fileData, // Store binary data in database
                FilePath = null, // No longer using filesystem
                FileUrl = null, // Will be set after saving
                FileSize = dto.File.Length,
                FileType = dto.File.ContentType,
                UploadedBy = userName,
                UploadedByRole = role,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Uploads.Add(upload);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"✅ Upload saved to database:");
            _logger.LogInformation($"   - Upload ID: {upload.Id}");
            _logger.LogInformation($"   - ChecklistId in DB: {upload.ChecklistId?.ToString() ?? "NULL"}");

            // Set the fileUrl with the actual upload ID
            upload.FileUrl = $"/api/uploads/{upload.Id}";
            _context.Uploads.Update(upload);
            await _context.SaveChangesAsync();

            // Map to response DTO (excludes FileData)
            var response = new UploadResponseDto
            {
                Id = upload.Id,
                ChecklistId = upload.ChecklistId,
                DocumentId = upload.DocumentId,
                DocumentName = upload.DocumentName,
                Name = upload.DocumentName ?? upload.FileName, // NEW: Add Name field for frontend compatibility
                Category = upload.Category,
                FileName = upload.FileName,
                FileUrl = upload.FileUrl,
                FileSize = upload.FileSize,
                FileType = upload.FileType,
                UploadedBy = upload.UploadedBy,
                UploadedByRole = upload.UploadedByRole,
                Status = upload.Status,
                CreatedAt = upload.CreatedAt,
                UpdatedAt = upload.UpdatedAt
            };

            _logger.LogInformation($"✅ File uploaded successfully: {upload.FileName} (ID: {upload.Id})");

            return StatusCode(201, new { success = true, data = response });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload error");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(Guid id)
    {
        try
        {
            var upload = await _context.Uploads.FindAsync(id);
            if (upload == null) return NotFound(new { success = false, error = "File not found" });

            if (upload.FileData == null || upload.FileData.Length == 0)
                return NotFound(new { success = false, error = "File data not found" });

            // Return file as binary with appropriate content type
            return File(upload.FileData, upload.FileType ?? "application/octet-stream", upload.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get file error");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("checklist/{checklistId}")]
    public async Task<IActionResult> GetByChecklist(Guid checklistId)
    {
        _logger.LogInformation($"🔍 GetByChecklist called with checklistId: {checklistId}");

        // Fetch from Upload table
        var uploads = await _context.Uploads
            .Where(u => u.ChecklistId == checklistId)
            .ToListAsync();

        // Fetch from SupportingDoc table (RM uploads)
        var supportingDocs = await _context.SupportingDocs
            .Where(s => s.ChecklistId == checklistId)
            .ToListAsync();

        _logger.LogInformation($"📋 Fetching supporting docs for checklist {checklistId}:");
        _logger.LogInformation($"   - Uploads table: {uploads.Count} records");
        _logger.LogInformation($"   - SupportingDocs table: {supportingDocs.Count} records");

        // Log each upload for debugging
        foreach (var upload in uploads)
        {
            _logger.LogInformation($"   Upload: Id={upload.Id}, FileName={upload.FileName}, DocumentName={upload.DocumentName}, ChecklistId={upload.ChecklistId}");
        }

        // Log each supporting doc for debugging
        foreach (var doc in supportingDocs)
        {
            _logger.LogInformation($"   SupportingDoc: Id={doc.Id}, FileName={doc.FileName}, ChecklistId={doc.ChecklistId}");
        }

        // Map Upload table to response DTOs
        var uploadResponses = uploads.Select(u => new UploadResponseDto
        {
            Id = u.Id,
            ChecklistId = u.ChecklistId,
            DocumentId = u.DocumentId,
            DocumentName = u.DocumentName,
            Name = u.DocumentName ?? u.FileName,
            Category = u.Category,
            FileName = u.FileName,
            FileUrl = u.FileUrl,
            FileSize = u.FileSize,
            FileType = u.FileType,
            UploadedBy = u.UploadedBy,
            UploadedByRole = u.UploadedByRole,
            Status = u.Status,
            CreatedAt = u.CreatedAt,
            UpdatedAt = u.UpdatedAt
        }).ToList();

        // Map SupportingDoc table to response DTOs
        var supportingResponses = supportingDocs.Select(s => new UploadResponseDto
        {
            Id = s.Id,
            ChecklistId = s.ChecklistId,
            DocumentName = s.FileName,
            Name = s.FileName,
            Category = "Supporting Documents",
            FileName = s.FileName,
            FileUrl = s.FileUrl,
            FileSize = s.FileSize,
            FileType = s.FileType,
            UploadedBy = s.UploadedBy?.Name ?? "Unknown",
            UploadedByRole = s.UploadedByRole,
            Status = "active",
            CreatedAt = s.UploadedAt,
            UpdatedAt = s.UploadedAt
        }).ToList();

        _logger.LogInformation($"✅ Returning {uploadResponses.Count + supportingResponses.Count} total documents");

        // Log the final response data structure
        _logger.LogInformation($"📤 Response structure: success=true, data=array[{uploadResponses.Count + supportingResponses.Count}]");

        // Combine both sources
        var allDocs = uploadResponses.Concat(supportingResponses).ToList();

        return Ok(new { success = true, data = allDocs });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var upload = await _context.Uploads.FindAsync(id);
            if (upload == null) return NotFound(new { success = false, error = "Upload not found" });

            if (!string.IsNullOrEmpty(upload.FilePath))
            {
                var fullPath = Path.Combine(_env.ContentRootPath, upload.FilePath.TrimStart('/', '\\'));
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            }

            _context.Uploads.Remove(upload);
            await _context.SaveChangesAsync();

            return Ok(new { success = true, message = "File deleted successfully", data = new { id = upload.Id, fileName = upload.FileName } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete error");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}
