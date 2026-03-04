using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NCBA.DCL.Models;

public class Extension
{
    public Guid Id { get; set; }

    // Reference to original deferral
    public Guid? DeferralId { get; set; }
    public Deferral? Deferral { get; set; }

    // Deferral details for easy reference
    public string? DeferralNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerNumber { get; set; }
    public string? DclNumber { get; set; }
    [NotMapped]
    public decimal? LoanAmount { get; set; }
    public DateTime? NextDueDate { get; set; }
    public DateTime? NextDocumentDueDate { get; set; }
    public DateTime? SlaExpiry { get; set; }

    // Extension details
    [Required]
    public int CurrentDaysSought { get; set; }

    [Required]
    public int RequestedDaysSought { get; set; }

    [Required]
    public string ExtensionReason { get; set; } = string.Empty;

    // Status
    public ExtensionStatus Status { get; set; } = ExtensionStatus.PendingApproval;

    // Approvers (similar to Deferral approvers)
    public ICollection<ExtensionApprover> Approvers { get; set; } = new List<ExtensionApprover>();

    // Current approver index
    public int CurrentApproverIndex { get; set; } = 0;

    // Creator approval
    public CreatorApprovalStatus CreatorApprovalStatus { get; set; } = Models.CreatorApprovalStatus.Pending;
    public Guid? CreatorApprovedById { get; set; }
    public User? CreatorApprovedBy { get; set; }
    public DateTime? CreatorApprovalDate { get; set; }
    public string? CreatorApprovalComment { get; set; }

    // Checker approval
    public CheckerApprovalStatus CheckerApprovalStatus { get; set; } = Models.CheckerApprovalStatus.Pending;
    public Guid? CheckerApprovedById { get; set; }
    public User? CheckerApprovedBy { get; set; }
    public DateTime? CheckerApprovalDate { get; set; }
    public string? CheckerApprovalComment { get; set; }

    // History
    public ICollection<ExtensionHistory> History { get; set; } = new List<ExtensionHistory>();

    // Comments
    public ICollection<ExtensionComment> Comments { get; set; } = new List<ExtensionComment>();

    // Request metadata
    [Required]
    public Guid RequestedById { get; set; }
    public User? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public DateTime RequestedDate { get; set; } = DateTime.UtcNow;

    // Rejection/return info
    public string? RejectionReason { get; set; }
    public string? RejectedBy { get; set; }
    public Guid? RejectedById { get; set; }
    public DateTime? RejectedDate { get; set; }

    public string? ReworkRequestedBy { get; set; }
    public Guid? ReworkRequestedById { get; set; }
    public DateTime? ReworkRequestedDate { get; set; }
    public string? ReworkComments { get; set; }

    // Approval flow
    public bool AllApproversApproved { get; set; } = false;

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // File Attachments
    public ICollection<ExtensionFile> AdditionalFiles { get; set; } = new List<ExtensionFile>();
}

public class ExtensionFile
{
    public Guid Id { get; set; }

    public Guid ExtensionId { get; set; }
    public Extension? Extension { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class ExtensionApprover
{
    public Guid Id { get; set; }

    public Guid ExtensionId { get; set; }
    public Extension? Extension { get; set; }

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public string? Role { get; set; }

    public ApproverApprovalStatus ApprovalStatus { get; set; } = ApproverApprovalStatus.Pending;

    public DateTime? ApprovalDate { get; set; }

    public string? ApprovalComment { get; set; }

    // Explicit sequence/order for approvers. Lower = earlier in the flow.
    public int Sequence { get; set; } = 0;

    public bool IsCurrent { get; set; } = false;
}

public class ExtensionHistory
{
    public Guid Id { get; set; }

    public Guid ExtensionId { get; set; }
    public Extension? Extension { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public string? UserName { get; set; }
    public string? UserRole { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    public string? Notes { get; set; }
    public string? Comment { get; set; }
}

public class ExtensionComment
{
    public Guid Id { get; set; }

    public Guid ExtensionId { get; set; }
    public Extension? Extension { get; set; }

    public Guid? AuthorId { get; set; }
    public User? Author { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ExtensionStatus
{
    PendingApproval,
    InReview,
    Approved,
    Rejected,
    ReturnedForRework,
    Withdrawn
}

public enum CreatorApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    ReturnedForRework
}

public enum CheckerApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    ReturnedForRework
}

public enum ApproverApprovalStatus
{
    Pending,
    Approved,
    Rejected
}
