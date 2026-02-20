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

            // Read file as binary
            byte[] fileData;
            using (var stream = new MemoryStream())
            {
                await dto.File.CopyToAsync(stream);
                fileData = stream.ToArray();
            }

            // Get user's role from claims
            var role = User?.FindFirst("role")?.Value ?? User?.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")?.Value ?? "RM";

            var upload = new Upload
            {
                Id = Guid.NewGuid(),
                ChecklistId = Guid.TryParse(checklistIdStr, out var cId) ? cId : (Guid?)null,
                DocumentId = Guid.TryParse(documentIdStr, out var dId) ? dId : (Guid?)null,
                DocumentName = documentName,
                Category = category,
                FileName = dto.File.FileName,
                FileData = fileData, // Store binary data in database
                FilePath = null, // No longer using filesystem
                FileUrl = null, // Will be set after saving
                FileSize = dto.File.Length,
                FileType = dto.File.ContentType,
                UploadedBy = User?.Identity?.Name ?? "RM",
                UploadedByRole = role,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Uploads.Add(upload);
            await _context.SaveChangesAsync();

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
        // Fetch from Upload table
        var uploads = await _context.Uploads
            .Where(u => u.ChecklistId == checklistId)
            .ToListAsync();

        // Fetch from SupportingDoc table (RM uploads)
        var supportingDocs = await _context.SupportingDocs
            .Where(s => s.ChecklistId == checklistId)
            .ToListAsync();

        // Map Upload table to response DTOs
        var uploadResponses = uploads.Select(u => new UploadResponseDto
        {
            Id = u.Id,
            ChecklistId = u.ChecklistId,
            DocumentId = u.DocumentId,
            DocumentName = u.DocumentName,
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
            FileName = s.FileName,
            FileUrl = s.FileUrl,
            FileSize = s.FileSize,
            FileType = s.FileType,
            UploadedByRole = s.UploadedByRole,
            Status = "active",
            CreatedAt = s.UploadedAt,
            UpdatedAt = s.UploadedAt
        }).ToList();

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
