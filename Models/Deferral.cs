using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NCBA.DCL.Models;

public class Deferral
{
    public Guid Id { get; set; }

    [Required]
    public string DeferralNumber { get; set; } = string.Empty;

    public string? CustomerNumber { get; set; }

    public string? CustomerName { get; set; }

    public string? BusinessName { get; set; }

    public string? LoanType { get; set; }

    [NotMapped]
    public decimal? LoanAmount { get; set; }

    public int DaysSought { get; set; }

    public string? DclNumber { get; set; }

    public DateTime? NextDueDate { get; set; }

    public DateTime? NextDocumentDueDate { get; set; }

    public DateTime? SlaExpiry { get; set; }

    public DeferralStatus Status { get; set; } = DeferralStatus.Pending;

    public string? RejectionReason { get; set; }

    public string? DeferralDescription { get; set; }

    public string? ReworkComments { get; set; }

    public string? ClosedReason { get; set; }

    // Who closed/withdrew the deferral (nullable)
    public Guid? ClosedById { get; set; }
    public string? ClosedByName { get; set; }
    public DateTime? ClosedAt { get; set; }

    public int CurrentApproverIndex { get; set; } = 0;

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Facility> Facilities { get; set; } = new List<Facility>();
    public ICollection<DeferralDocument> Documents { get; set; } = new List<DeferralDocument>();
    public ICollection<Approver> Approvers { get; set; } = new List<Approver>();

    // Store selected/requested documents as JSON to persist what was originally requested
    public string? SelectedDocumentsJson { get; set; }

    [NotMapped]
    public List<SelectedDocumentData>? SelectedDocuments { get; set; }

    [NotMapped]
    public string? RmReason { get; set; }

    [NotMapped]
    public List<DeferralCommentEntry> Comments { get; set; } = new();

    [NotMapped]
    public string CreatorApprovalStatus { get; set; } = "pending";

    [NotMapped]
    public string CheckerApprovalStatus { get; set; } = "pending";

    [NotMapped]
    public string DeferralApprovalStatus { get; set; } = "pending";

    [NotMapped]
    public DateTime? CreatorApprovalDate { get; set; }

    [NotMapped]
    public DateTime? CheckerApprovalDate { get; set; }

    [NotMapped]
    public string? LastReturnedByRole { get; set; }

    [NotMapped]
    public List<CloseRequestDocumentState> CloseRequestDocuments { get; set; } = new();

    // Extension references
    public string? ExtensionStatus { get; set; }
    public ICollection<Extension> Extensions { get; set; } = new List<Extension>();
}

public class DeferralCommentEntry
{
    public string? Text { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DeferralCommentAuthor? Author { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorRole { get; set; }
}

public class DeferralCommentAuthor
{
    public string? Name { get; set; }
    public string? Role { get; set; }
}

public class Facility
{
    public Guid Id { get; set; }

    public string? Type { get; set; }

    public decimal Sanctioned { get; set; }

    public decimal Balance { get; set; }

    public decimal Headroom { get; set; }

    public Guid DeferralId { get; set; }
    public Deferral Deferral { get; set; } = null!;
}

public class DeferralDocument
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? Url { get; set; }

    public Guid? UploadedById { get; set; }
    public User? UploadedBy { get; set; }

    public Guid DeferralId { get; set; }
    public Deferral Deferral { get; set; } = null!;

    // Per-document deferral metadata
    public int? DaysSought { get; set; }

    public DateTime? NextDocumentDueDate { get; set; }
}

public class Approver
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public string? Name { get; set; }

    public string? Role { get; set; }

    public bool Approved { get; set; } = false;

    public DateTime? ApprovedAt { get; set; }

    public bool Rejected { get; set; } = false;

    public DateTime? RejectedAt { get; set; }

    public bool Returned { get; set; } = false;

    public DateTime? ReturnedAt { get; set; }

    public Guid DeferralId { get; set; }
    public Deferral Deferral { get; set; } = null!;
}

public class SelectedDocumentData
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Category { get; set; }
    public int? DaysSought { get; set; }
    public DateTime? NextDocumentDueDate { get; set; }
}

public class CloseRequestDocumentState
{
    public string? DocumentName { get; set; }
    public string? Comment { get; set; }
    public string CreatorStatus { get; set; } = "pending";
    public string? CreatorComment { get; set; }
    public string? CreatorReviewedByName { get; set; }
    public DateTime? CreatorReviewedAt { get; set; }
    public string CheckerStatus { get; set; } = "pending";
    public string? CheckerComment { get; set; }
    public string? CheckerReviewedByName { get; set; }
    public DateTime? CheckerReviewedAt { get; set; }
    public List<CloseRequestUploadedFile> Files { get; set; } = new();
}

public class CloseRequestUploadedFile
{
    public string? DocumentId { get; set; }
    public string? FileName { get; set; }
    public string? Url { get; set; }
    public DateTime? UploadedAt { get; set; }
}

public enum DeferralStatus
{
    Pending,
    InReview,
    PartiallyApproved,
    Approved,
    Rejected,
    ReturnedForRework,
    CloseRequested,
    CloseRequestedCreatorApproved,
    Closed
}