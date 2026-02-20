// using Microsoft.AspNetCore.Http;
// using System;
// using System.Text.Json.Serialization;

// namespace NCBA.DCL.DTOs
// {
//     public class FileUploadDto
//     {
//         public required IFormFile File { get; set; }
//         public string? ChecklistId { get; set; }
//         public string? DocumentId { get; set; }
//         public string? DocumentName { get; set; }
//         public string? Category { get; set; }
//     }
// }
using Microsoft.AspNetCore.Http;
using System;
using System.Text.Json.Serialization;

namespace NCBA.DCL.DTOs
{
    public class FileUploadDto
    {
        public required IFormFile File { get; set; }
        public string? ChecklistId { get; set; }
        public string? DocumentId { get; set; }
        public string? DocumentName { get; set; }
        public string? Category { get; set; }
    }

    public class SupportingDocUploadDto
    {
        public required IFormFile File { get; set; }
        public required string ChecklistId { get; set; }
    }

    // Response DTO - Excludes binary FileData to reduce response size
    public class UploadResponseDto
    {
        [JsonPropertyName("_id")]
        public Guid Id { get; set; }

        [JsonPropertyName("checklistId")]
        public Guid? ChecklistId { get; set; }

        [JsonPropertyName("documentId")]
        public Guid? DocumentId { get; set; }

        [JsonPropertyName("documentName")]
        public string? DocumentName { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("fileUrl")]
        public string? FileUrl { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("fileType")]
        public string? FileType { get; set; }

        [JsonPropertyName("uploadedBy")]
        public string? UploadedBy { get; set; }

        [JsonPropertyName("uploadedByRole")]
        public string? UploadedByRole { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }
    }
}