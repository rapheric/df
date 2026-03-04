// using System.ComponentModel.DataAnnotations;

// namespace NCBA.DCL.Models;

// public class Deferral
// {
//     public Guid Id { get; set; }

//     [Required]
//     public string DeferralNumber { get; set; } = string.Empty;

//     public string? CustomerNumber { get; set; }

//     public string? CustomerName { get; set; }

//     public string? BusinessName { get; set; }

//     public string? LoanType { get; set; }

//     public int DaysSought { get; set; }

//     public string? DclNumber { get; set; }

//     public DeferralStatus Status { get; set; } = DeferralStatus.Pending;

//     public string? RejectionReason { get; set; }

//     public string? DeferralDescription { get; set; }

//     public string? ReworkComments { get; set; }

//     public string? ClosedReason { get; set; }

//     public int CurrentApproverIndex { get; set; } = 0;

//     public Guid? CreatedById { get; set; }
//     public User? CreatedBy { get; set; }

//     public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

//     public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

//     // Navigation properties
//     public ICollection<Facility> Facilities { get; set; } = new List<Facility>();
//     public ICollection<DeferralDocument> Documents { get; set; } = new List<DeferralDocument>();
//     public ICollection<Approver> Approvers { get; set; } = new List<Approver>();
// }

// public class Facility
// {
//     public Guid Id { get; set; }

//     public string? Type { get; set; }

//     public decimal Sanctioned { get; set; }

//     public decimal Balance { get; set; }

//     public decimal Headroom { get; set; }

//     public Guid DeferralId { get; set; }
//     public Deferral Deferral { get; set; } = null!;
// }

// public class DeferralDocument
// {
//     public Guid Id { get; set; }

//     public string? Name { get; set; }

//     public string? Url { get; set; }

//     public Guid? UploadedById { get; set; }
//     public User? UploadedBy { get; set; }

//     public Guid DeferralId { get; set; }
//     public Deferral Deferral { get; set; } = null!;
// }

// public class Approver
// {
//     public Guid Id { get; set; }

//     public Guid? UserId { get; set; }
//     public User? User { get; set; }

//     public string? Name { get; set; }

//     public string? Role { get; set; }

//     public bool Approved { get; set; } = false;

//     public DateTime? ApprovedAt { get; set; }

//     public Guid DeferralId { get; set; }
//     public Deferral Deferral { get; set; } = null!;
// }

// public enum DeferralStatus
// {
//     Pending,
//     InReview,
//     Approved,
//     Rejected,
//     ReturnedForRework
// }

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

    public Guid DeferralId { get; set; }
    public Deferral Deferral { get; set; } = null!;
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