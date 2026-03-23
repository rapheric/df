using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Models;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomerController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CustomerController> _logger;

    public CustomerController(
        ApplicationDbContext context,
        ILogger<CustomerController> logger)
    {
        _context = context;
        _logger = logger;
    }

    // POST /api/customers/search
    [HttpPost("search")]
    public async Task<IActionResult> SearchCustomers([FromBody] CustomerSearchRequest request)
    {
        try
        {
            var query = _context.Users.Where(u => u.Role == UserRole.Customer);

            if (!string.IsNullOrEmpty(request.CustomerNumber))
            {
                query = query.Where(u => u.CustomerNumber != null && 
                                        u.CustomerNumber.Contains(request.CustomerNumber));
            }

            if (!string.IsNullOrEmpty(request.CustomerName))
            {
                query = query.Where(u => u.Name.Contains(request.CustomerName));
            }

            var customers = await query
                .Select(u => new CustomerSearchResponse
                {
                    CustomerNumber = u.CustomerNumber ?? "",
                    CustomerName = u.Name,
                    Email = u.Email,
                    Active = u.Active
                })
                .ToListAsync();

            return Ok(customers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching customers");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/customers/search
    [HttpGet("search")]
    public async Task<IActionResult> SearchCustomersQuery([FromQuery] string? customerNumber, [FromQuery] string? loanType)
    {
        try
        {
            var query = _context.Users.Where(u => u.Role == UserRole.Customer);

            if (!string.IsNullOrEmpty(customerNumber))
            {
                query = query.Where(u => u.CustomerNumber != null && 
                                        u.CustomerNumber.Contains(customerNumber));
            }

            var customers = await query
                .Select(u => new
                {
                    id = u.Id,
                    customerNumber = u.CustomerNumber ?? "",
                    customerName = u.Name,
                    name = u.Name,
                    email = u.Email,
                    active = u.Active,
                    loanType = loanType  // Include the requested loan type in response
                })
                .Take(20)
                .ToListAsync();

            return Ok(customers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching customers");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // GET /api/customers/search-dcl
    [HttpGet("search-dcl")]
    public async Task<IActionResult> SearchByDcl([FromQuery] string dclNo)
    {
        try
        {
            if (string.IsNullOrEmpty(dclNo))
                return BadRequest(new { message = "DCL number is required" });

            var checklist = await _context.Checklists
                .Where(c => c.DclNo!.Contains(dclNo))
                .Select(c => new DclSearchResponse
                {
                    Id = c.Id,
                    DclNo = c.DclNo!,
                    CustomerName = c.CustomerName,
                    BusinessName = c.CustomerName, // Mapping CustomerName to BusinessName as per logic
                    CustomerNumber = c.CustomerNumber,
                    LoanType = c.LoanType,
                    Status = c.Status.ToString(),
                    CreatedAt = c.CreatedAt
                })
                .ToListAsync();

            return Ok(checklist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching DCL");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
