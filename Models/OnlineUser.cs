namespace NCBA.DCL.Models;

public class OnlineUser
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public DateTime LoginTime { get; set; } = DateTime.UtcNow;
    public string? CurrentPage { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public HashSet<string> SocketIds { get; set; } = new();
}
