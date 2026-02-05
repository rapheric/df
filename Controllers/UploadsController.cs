using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBA.DCL.Data;
using NCBA.DCL.Models;
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
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, error = "No file uploaded" });

            var checklistIdStr = Request.Form["checklistId"].FirstOrDefault();
            var documentIdStr = Request.Form["documentId"].FirstOrDefault();
            var documentName = Request.Form["documentName"].FirstOrDefault() ?? file.FileName;
            var category = Request.Form["category"].FirstOrDefault();

            var uploadsDir = Path.Combine(_env.ContentRootPath, "uploads");
            if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

            var uniqueName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(uploadsDir, uniqueName);

            await using (var stream = System.IO.File.Create(filePath))
            {
                await file.CopyToAsync(stream);
            }

            var upload = new Upload
            {
                Id = Guid.NewGuid(),
                ChecklistId = Guid.TryParse(checklistIdStr, out var cId) ? cId : (Guid?)null,
                DocumentId = Guid.TryParse(documentIdStr, out var dId) ? dId : (Guid?)null,
                DocumentName = documentName,
                Category = category,
                FileName = file.FileName,
                FilePath = $"/uploads/{uniqueName}",
                FileUrl = $"/uploads/{uniqueName}",
                FileSize = file.Length,
                FileType = file.ContentType,
                UploadedBy = User?.Identity?.Name ?? "RM",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Uploads.Add(upload);
            await _context.SaveChangesAsync();

            return StatusCode(201, new
            {
                success = true,
                data = new
                {
                    _id = upload.Id,
                    checklistId = upload.ChecklistId,
                    documentId = upload.DocumentId,
                    documentName = upload.DocumentName,
                    category = upload.Category,
                    fileName = upload.FileName,
                    fileUrl = upload.FileUrl,
                    fileSize = upload.FileSize,
                    fileType = upload.FileType,
                    uploadedBy = upload.UploadedBy,
                    status = upload.Status,
                    createdAt = upload.CreatedAt,
                    updatedAt = upload.UpdatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload error");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("checklist/{checklistId}")]
    public async Task<IActionResult> GetByChecklist(Guid checklistId)
    {
        var uploads = await _context.Uploads
            .Where(u => u.ChecklistId == checklistId && u.Status == "active")
            .ToListAsync();

        return Ok(new { success = true, data = uploads });
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
