using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NCBA.DCL.Models;

/// <summary>
/// Represents a logout session with verification tracking
/// Used for sending verification codes to user email on logout
/// </summary>
[Table("LogoutSessions")]
public class LogoutSession
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey(nameof(User))]
    public Guid UserId { get; set; }

    /// <summary>
    /// Hashed verification code
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// IP address from which logout was initiated
    /// </summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent (browser/device info)
    /// </summary>
    [Column(TypeName = "TEXT")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Whether the logout was verified via email code
    /// </summary>
    public bool IsVerified { get; set; } = false;

    /// <summary>
    /// Timestamp when verification code was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when logout was verified
    /// </summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// When this session/code expires
    /// </summary>
    [Required]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Navigation property
    /// </summary>
    public virtual User? User { get; set; }
}

/// <summary>
/// Email verification record
/// Used for email verification during registration or address changes
/// </summary>
[Table("EmailVerifications")]
public class EmailVerification
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Hashed verification code
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// When this code expires
    /// </summary>
    [Required]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether this code has been used
    /// </summary>
    public bool IsUsed { get; set; } = false;

    /// <summary>
    /// Timestamp when created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when verified
    /// </summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// Optional reference to user (if user exists)
    /// </summary>
    [ForeignKey(nameof(User))]
    public Guid? UserId { get; set; }

    public virtual User? User { get; set; }
}
