using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NCBA.DCL.Models;

/// <summary>
/// Email-based MFA code for login verification
/// Stores temporary codes sent to user emails during login
/// </summary>
[Table("EmailMFACodes")]
public class EmailMFACode
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("User")]
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Hashed 6-digit MFA code
    /// </summary>
    [Required]
    [StringLength(255)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Session token linking this code to the login attempt
    /// </summary>
    [Required]
    [StringLength(255)]
    public string SessionToken { get; set; } = string.Empty;

    /// <summary>
    /// Whether this code has been used for verification
    /// </summary>
    public bool IsUsed { get; set; } = false;

    /// <summary>
    /// When the code was verified
    /// </summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// When the code was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the code expires (usually 10 minutes)
    /// </summary>
    [Required]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// IP address from which the MFA was initiated
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent of the device
    /// </summary>
    public string? UserAgent { get; set; }
}
