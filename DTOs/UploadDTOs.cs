using Microsoft.AspNetCore.Http;
using System;

namespace NCBA.DCL.DTOs
{
    public class FileUploadDto
    {
        public IFormFile File { get; set; }
        public string? ChecklistId { get; set; }
        public string? DocumentId { get; set; }
        public string? DocumentName { get; set; }
        public string? Category { get; set; }
    }
}
