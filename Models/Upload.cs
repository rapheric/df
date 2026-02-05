using System.ComponentModel.DataAnnotations;

namespace NCBA.DCL.Models;

public class Upload
{
    public Guid Id { get; set; }

    public Guid? ChecklistId { get; set; }
    public Guid? DocumentId { get; set; }

    public string? DocumentName { get; set; }
    public string? Category { get; set; }

    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public string? FileUrl { get; set; }

    public long FileSize { get; set; }
    public string? FileType { get; set; }

    public string? UploadedBy { get; set; }
    public string? Status { get; set; } = "active";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
