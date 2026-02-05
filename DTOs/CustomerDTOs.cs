namespace NCBA.DCL.DTOs;

// Customer Search Request
public class CustomerSearchRequest
{
    public string? CustomerNumber { get; set; }
    public string? CustomerName { get; set; }
}

// Customer Search Response
public class CustomerSearchResponse
{
    public string CustomerNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool Active { get; set; }
}

// DCL Search Response
public class DclSearchResponse
{
    public Guid Id { get; set; }
    public string DclNo { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string? CustomerNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
