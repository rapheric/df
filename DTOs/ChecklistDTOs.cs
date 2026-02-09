using NCBA.DCL.Models;
using System.Text.Json.Serialization;

namespace NCBA.DCL.DTOs;

public class SaveChecklistDraftRequest
{
    public Guid ChecklistId { get; set; }
    public string? DraftDataJson { get; set; }
    public bool? IsDraft { get; set; }
    public DateTime? DraftExpiresAt { get; set; }
}

// Checklist DTOs
public class CreateChecklistRequest
{
    public string CustomerNumber { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string? LoanType { get; set; }
    public string? IbpsNo { get; set; }
    public Guid? AssignedToRMId { get; set; }
    public List<DocumentCategoryDto>? Documents { get; set; }
}

public class UpdateChecklistRequest
{
    public string? CustomerName { get; set; }
    public string? LoanType { get; set; }
    public Guid? AssignedToRMId { get; set; }
    public Guid? AssignedToCoCheckerId { get; set; }
}

public class UpdateStatusRequest
{
    public ChecklistStatus Status { get; set; }
}

// Document DTOs
public class AddDocumentRequest
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public class UpdateDocumentRequest
{
    public DocumentStatus? Status { get; set; }
    public string? CheckerComment { get; set; }
    public string? CreatorComment { get; set; }
    public string? RmComment { get; set; }
    public string? FileUrl { get; set; }
}

// CoCreator DTOs
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

public class UpdateChecklistStatusRequest
{
    public Guid ChecklistId { get; set; }
    public ChecklistStatus Status { get; set; }
}

public class UpdateCheckerStatusRequest
{
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("checkerDecisions")]
    public List<CheckerDecisionDto>? CheckerDecisions { get; set; }

    [JsonPropertyName("checkerComments")]
    public string? CheckerComments { get; set; }

    [JsonPropertyName("checkerComment")]
    public string? CheckerComment { get; set; }
}

public class CheckerDecisionDto
{
    [JsonPropertyName("documentId")]
    public Guid? DocumentId { get; set; }

    [JsonPropertyName("checkerStatus")]
    public string? CheckerStatus { get; set; }

    [JsonPropertyName("checkerComment")]
    public string? CheckerComment { get; set; }
}

// Checker DTOs
public class UpdateCheckerDCLRequest
{
    public ChecklistStatus Status { get; set; }
    public List<DocumentUpdateDto>? DocumentUpdates { get; set; }
}

public class DocumentUpdateDto
{
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    [JsonPropertyName("_id")]
    public Guid? _id { get; set; }

    public CheckerStatus? Status { get; set; }
    public string? CheckerComment { get; set; }

    // Helper property to resolve either id or _id
    public Guid? DocumentId => Id ?? _id;
}

public class RejectDCLRequest
{
    public string Reason { get; set; } = string.Empty;
}

// RM DTOs
public class SubmitToCoCreatorRequest
{
    [JsonPropertyName("checklistId")]
    public Guid? ChecklistId { get; set; }

    [JsonPropertyName("rmGeneralComment")]
    public string? RmGeneralComment { get; set; }

    [JsonPropertyName("documents")]
    public List<RmDocumentUpdateDto>? Documents { get; set; }

    [JsonPropertyName("supportingDocs")]
    public List<SupportingDocDto>? SupportingDocs { get; set; }
}

public class SupportingDocDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("fileUrl")]
    public string? FileUrl { get; set; }
}

// CoCreator SubmitToRM Request - saves document updates before submitting
public class SubmitToRMRequest
{
    [JsonPropertyName("documents")]
    public List<DocumentCategoryDto>? Documents { get; set; }
}

public class RmDocumentUpdateDto
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DocumentStatus? Status { get; set; }

    [JsonPropertyName("rmStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RmStatus? RmStatus { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("fileUrl")]
    public string? FileUrl { get; set; }

    [JsonPropertyName("deferralReason")]
    public string? DeferralReason { get; set; }

    [JsonPropertyName("deferralNumber")]
    public string? DeferralNumber { get; set; }

    [JsonPropertyName("_id")]
    public Guid? _id { get; set; }

    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    // Helper property to get the document ID from either _id or Id
    public Guid? DocumentId => _id ?? Id;
}

public class UploadSupportingDocDto
{
    public IFormFile File { get; set; } = null!;
}

public class CoCreatorSubmitToCCRequest
{
    [JsonPropertyName("dclNo")]
    public string DclNo { get; set; } = string.Empty;

    [JsonPropertyName("documents")]
    public List<CoCreatorDocumentDto>? Documents { get; set; }

    [JsonPropertyName("submittedToCoChecker")]
    public bool? SubmittedToCoChecker { get; set; }

    [JsonPropertyName("assignedToCoChecker")]
    public Guid? AssignedToCoChecker { get; set; }

    [JsonPropertyName("finalComment")]
    public string? FinalComment { get; set; }

    [JsonPropertyName("attachments")]
    public List<string>? Attachments { get; set; }
}

public class CoCreatorDocumentDto
{
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    [JsonPropertyName("_id")]
    public Guid? _id { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("fileUrl")]
    public string? FileUrl { get; set; }

    [JsonPropertyName("deferralNo")]
    public string? DeferralNo { get; set; }

    [JsonPropertyName("deferralReason")]
    public string? DeferralReason { get; set; }

    [JsonPropertyName("expiryDate")]
    public DateTime? ExpiryDate { get; set; }
}
