using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBA.DCL.DTOs;
using NCBA.DCL.Models;
using NCBA.DCL.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace NCBA.DCL.Controllers
{
    [ApiController]
    [Route("api/audit")]
    [Authorize(Roles = "admin")]
    public class AuditController : ControllerBase
    {
        private readonly IAuditLogService _auditLogService;
        public AuditController(IAuditLogService auditLogService)
        {
            _auditLogService = auditLogService;
        }

        [HttpGet("logs")]
            public async Task<IActionResult> GetLogs([FromQuery] int page = 1, [FromQuery] int limit = 20, [FromQuery] string? action = null, [FromQuery] Guid? userId = null, [FromQuery] string? resource = null, [FromQuery] string? status = null, [FromQuery] DateTime? startDate = null, [FromQuery] DateTime? endDate = null, [FromQuery] string? search = null)
        {
            var result = await _auditLogService.GetLogsAsync(page, limit, action, userId, resource, status, startDate, endDate, search);
            return StatusCode(result.StatusCode, result.Body);
        }

        [HttpGet("logs/{id}")]
        public async Task<IActionResult> GetLogById(Guid id)
        {
            var result = await _auditLogService.GetLogByIdAsync(id);
            return StatusCode(result.StatusCode, result.Body);
        }

        [HttpPost("logs")]
        [AllowAnonymous] // Might be needed for system logs or login failure logs
        public async Task<IActionResult> CreateLog([FromBody] AuditLogCreateDto dto)
        {
            var result = await _auditLogService.CreateLogAsync(dto);
            return StatusCode(result.StatusCode, result.Body);
        }

        [HttpGet("logs/export")]
            public async Task<IActionResult> ExportLogs([FromQuery] string? action = null, [FromQuery] Guid? userId = null, [FromQuery] string? resource = null, [FromQuery] string? status = null, [FromQuery] DateTime? startDate = null, [FromQuery] DateTime? endDate = null, [FromQuery] string? search = null)
        {
            var result = await _auditLogService.ExportLogsAsync(action, userId, resource, status, startDate, endDate, search);
            
            if (result.StatusCode != 200) return StatusCode(result.StatusCode, result.Body);

            var logs = result.Body as IEnumerable<AuditLog>;
            if (logs == null) return StatusCode(500, new { message = "Failed to export logs" });

            var csv = new StringBuilder();
            csv.AppendLine("createdAt,action,resource,status,performedBy.name,performedBy.email,targetUser.name,targetUser.email,details,errorMessage");

            foreach (var log in logs)
            {
                csv.AppendLine($"{log.CreatedAt:yyyy-MM-dd HH:mm:ss}," +
                               $"\"{log.Action}\"," +
                               $"\"{log.Resource}\"," +
                               $"\"{log.Status}\"," +
                               $"\"{log.PerformedBy?.Name}\"," +
                               $"\"{log.PerformedBy?.Email}\"," +
                               $"\"{log.TargetUser?.Name}\"," +
                               $"\"{log.TargetUser?.Email}\"," +
                               $"\"{log.Details?.ToString().Replace("\"", "\"\"")}\"," +
                               $"\"{log.ErrorMessage?.Replace("\"", "\"\"")}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return File(bytes, "text/csv", "audit-logs.csv");
        }

        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            var result = await _auditLogService.GetStatsAsync();
            return StatusCode(result.StatusCode, result.Body);
        }

        [HttpGet("online-users")]
        public async Task<IActionResult> GetOnlineUsersWithActivity()
        {
            var result = await _auditLogService.GetOnlineUsersWithActivityAsync();
            return StatusCode(result.StatusCode, result.Body);
        }
    }
}
