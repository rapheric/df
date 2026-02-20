using NCBA.DCL.Models;
using System.Text.Json.Serialization;

namespace NCBA.DCL.DTOs;

// Create Extension Request
public class CreateExtensionRequest
{
    public Guid DeferralId { get; set; }
    public int RequestedDaysSought { get; set; }
    public string ExtensionReason { get; set; } = string.Empty;
    public List<ExtensionFileDto>? AdditionalFiles { get; set; }
}

// Extension Response
public class ExtensionResponse
{
    public Guid Id { get; set; }
    public Guid? DeferralId { get; set; }
    public string? DeferralNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerNumber { get; set; }
    public string? DclNumber { get; set; }
    public decimal? LoanAmount { get; set; }
    public DateTime? NextDueDate { get; set; }
    public DateTime? NextDocumentDueDate { get; set; }
    public DateTime? SlaExpiry { get; set; }
    public int CurrentDaysSought { get; set; }
    public int RequestedDaysSought { get; set; }
    public string ExtensionReason { get; set; } = string.Empty;
    public ExtensionStatus Status { get; set; }
    public List<ExtensionApproverDto> Approvers { get; set; } = new();
    public int CurrentApproverIndex { get; set; }
    public CreatorApprovalStatus CreatorApprovalStatus { get; set; }
    public Guid? CreatorApprovedById { get; set; }
    public DateTime? CreatorApprovalDate { get; set; }
    public string? CreatorApprovalComment { get; set; }
    public CheckerApprovalStatus CheckerApprovalStatus { get; set; }
    public Guid? CheckerApprovedById { get; set; }
    public DateTime? CheckerApprovalDate { get; set; }
    public string? CheckerApprovalComment { get; set; }
    public List<ExtensionHistoryDto> History { get; set; } = new();
    public List<ExtensionCommentDto> Comments { get; set; } = new();
    public List<ExtensionFileDto> AdditionalFiles { get; set; } = new();
    public Guid RequestedById { get; set; }
    public string? RequestedByName { get; set; }
    public DateTime RequestedDate { get; set; }
    public string? RejectionReason { get; set; }
    public bool AllApproversApproved { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Approve Extension Request
public class ApproveExtensionRequest
{
    public string? Comment { get; set; }
}

// Reject Extension Request
public class RejectExtensionRequest
{
    public string Reason { get; set; } = string.Empty;
}

// Extension Approver DTO
public class ExtensionApproverDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? Role { get; set; }
    public ApproverApprovalStatus ApprovalStatus { get; set; }
    public DateTime? ApprovalDate { get; set; }
    public string? ApprovalComment { get; set; }
    public bool IsCurrent { get; set; }
}

// Extension History DTO
public class ExtensionHistoryDto
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserRole { get; set; }
    public DateTime Date { get; set; }
    public string? Notes { get; set; }
    public string? Comment { get; set; }
}

// Extension Comment DTO
public class ExtensionCommentDto
{
    public Guid Id { get; set; }
    public Guid? AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

// Add Comment Request
public class AddExtensionCommentRequest
{
    public string Text { get; set; } = string.Empty;
}

// Extension File DTO
public class ExtensionFileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime UploadedAt { get; set; }
}
