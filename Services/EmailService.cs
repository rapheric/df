
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;

namespace NCBA.DCL.Services;

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string? _smtpHost;
    private readonly int _smtpPort;
    private readonly string? _smtpUser;
    private readonly string? _smtpPass;
    private readonly bool _smtpSecure;
    private readonly string? _emailFrom;
    private readonly string _loginUrl;

    public EmailService(ILogger<EmailService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        // Read SMTP configuration from environment or appsettings
        _smtpHost =
            configuration["EmailSettings:SmtpHost"]
            ?? Environment.GetEnvironmentVariable("SMTP_HOST")
            ?? Environment.GetEnvironmentVariable("EMAIL_HOST");

        var smtpPortRaw =
            configuration["EmailSettings:SmtpPort"]
            ?? Environment.GetEnvironmentVariable("SMTP_PORT")
            ?? Environment.GetEnvironmentVariable("EMAIL_PORT");
        _smtpPort = int.TryParse(smtpPortRaw, out var port) ? port : 587;

        _smtpUser =
            configuration["EmailSettings:SmtpUser"]
            ?? Environment.GetEnvironmentVariable("SMTP_USER")
            ?? Environment.GetEnvironmentVariable("EMAIL_USER");

        _smtpPass =
            configuration["EmailSettings:SmtpPass"]
            ?? Environment.GetEnvironmentVariable("SMTP_PASS")
            ?? Environment.GetEnvironmentVariable("EMAIL_PASS");

        var smtpSecureRaw =
            configuration["EmailSettings:SmtpSecure"]
            ?? Environment.GetEnvironmentVariable("SMTP_SECURE");
        if (!string.IsNullOrWhiteSpace(smtpSecureRaw))
        {
            _smtpSecure = smtpSecureRaw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _smtpSecure = _smtpPort == 465 || _smtpPort == 587;
        }

        _emailFrom =
            configuration["EmailSettings:EmailFrom"]
            ?? Environment.GetEnvironmentVariable("EMAIL_FROM")
            ?? _smtpUser;

        _loginUrl =
            configuration["EmailSettings:LoginUrl"]
            ?? configuration["Frontend:LoginUrl"]
            ?? Environment.GetEnvironmentVariable("APP_LOGIN_URL")
            ?? Environment.GetEnvironmentVariable("APP_URL")
            ?? "http://localhost:5173/login";

        // Log configuration status
        LogEmailConfig();
    }

    private void LogEmailConfig()
    {
        _logger.LogInformation("📧 Email Service Config: " +
            $"SMTP_HOST: {(_smtpHost != null ? "✅ set" : "❌ missing")}, " +
            $"SMTP_PORT: {(_smtpPort > 0 ? "✅ set" : "❌ missing")}, " +
            $"SMTP_USER: {(_smtpUser != null ? "✅ set" : "❌ missing")}, " +
            $"SMTP_SECURE: {(_smtpSecure ? "✅ true" : "❌ false")}, " +
            $"EMAIL_FROM: {(_emailFrom != null ? $"✅ {_emailFrom}" : "❌ missing")}, " +
            $"LOGIN_URL: {(!string.IsNullOrWhiteSpace(_loginUrl) ? $"✅ {_loginUrl}" : "❌ missing")}");
    }

    private string BuildLoginButtonHtml()
    {
        var loginHref = string.IsNullOrWhiteSpace(_loginUrl)
            ? "http://localhost:5173/login"
            : _loginUrl.Trim();

        return $@"
            <div style=""margin: 20px 0;"">
                <a href=""{loginHref}"" data-login-button=""true"" style=""
                    display: inline-block;
                    background-color: #164679;
                    color: #ffffff;
                    text-decoration: none;
                    padding: 10px 18px;
                    border-radius: 6px;
                    font-weight: 600;
                    font-family: Arial, sans-serif;
                "">Login to DCL System</a>
            </div>";
    }

    private string EnsureLoginButtonInEmail(string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(htmlBody))
        {
            return htmlBody;
        }

        if (htmlBody.Contains("data-login-button=\"true\"", StringComparison.OrdinalIgnoreCase))
        {
            return htmlBody;
        }

        var loginButtonHtml = BuildLoginButtonHtml();
        var bodyClosingTag = "</body>";
        var closingIndex = htmlBody.LastIndexOf(bodyClosingTag, StringComparison.OrdinalIgnoreCase);

        if (closingIndex >= 0)
        {
            return htmlBody.Insert(closingIndex, loginButtonHtml + Environment.NewLine);
        }

        return htmlBody + Environment.NewLine + loginButtonHtml;
    }

    // ✅ Generic email sending method (aligns with Node.js sendEmail)
    private async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(htmlBody))
        {
            throw new ArgumentException("Email parameters: to, subject, and htmlBody are required");
        }

        htmlBody = EnsureLoginButtonInEmail(htmlBody);

        // If SMTP not configured, log and return (non-blocking)
        if (string.IsNullOrWhiteSpace(_smtpHost) || string.IsNullOrWhiteSpace(_smtpUser))
        {
            _logger.LogWarning($"⚠️ SMTP not configured. Email not sent TO: {to} | Subject: {subject}");
            return;
        }

        try
        {
            using (var client = new SmtpClient(_smtpHost, _smtpPort))
            {
                client.EnableSsl = _smtpSecure;
                client.Credentials = new NetworkCredential(_smtpUser, _smtpPass);
                client.Timeout = 10000; // 10 second timeout

                using (var mailMessage = new MailMessage(_emailFrom ?? _smtpUser, to))
                {
                    mailMessage.Subject = subject;
                    mailMessage.Body = htmlBody;
                    mailMessage.IsBodyHtml = true;

                    await client.SendMailAsync(mailMessage);
                    _logger.LogInformation($"✅ [EMAIL SENT] To: {to} | Subject: {subject}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Failed to send email to {to}. Subject: {subject}");
            // Non-blocking: don't throw, just log the error
        }
    }

    // ✅ HTML template for checker status changed
    private string GetDeferralSubmittedHtml(string userName, string deferralNumber, string customerName, int daysSought, string recipientRole)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Deferral Submitted</h2>
                    <p>Hello {userName},</p>
                    <p>A deferral request has been submitted with details below:</p>
                    <ul>
                        <li><strong>Deferral Number:</strong> {deferralNumber}</li>
                        <li><strong>Customer:</strong> {customerName}</li>
                        <li><strong>Days Sought:</strong> {daysSought}</li>
                    </ul>
                    <p><strong>Recipient:</strong> {recipientRole}</p>
                    <p>Please log in to the system to review.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralReminderHtml(string userName, string deferralNumber, string customerName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Deferral Reminder</h2>
                    <p>Hello {userName},</p>
                    <p>This is a reminder that deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong> is awaiting your approval.</p>
                    <p>Please log in to the system and take action.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralApprovalConfirmationHtml(string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval)
    {
        var nextStep = isFinalApproval
            ? "This was the final approval step. The deferral is now fully approved."
            : $"The deferral has now moved to the next approver: <strong>{(string.IsNullOrWhiteSpace(nextApproverName) ? "Next Approver" : nextApproverName)}</strong>.";

        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Approval Confirmation</h2>
                    <p>Hello {userName},</p>
                    <p>You have successfully approved deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong>.</p>
                    <p>{nextStep}</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralApprovedToRmHtml(string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval)
    {
        var nextStep = isFinalApproval
            ? "This was the final approval step. The deferral is now fully approved and will proceed to completion."
            : $"The deferral has moved to the next approver: <strong>{(string.IsNullOrWhiteSpace(nextApproverName) ? "Next Approver" : nextApproverName)}</strong>.";

        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Deferral Update</h2>
                    <p>Hello {userName},</p>
                    <p>An approver has recorded an approval for deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong>.</p>
                    <p>{nextStep}</p>
                    <p>Please log in to the system to view details and next steps.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralRejectedToRmHtml(string userName, string deferralNumber, string customerName, string rejectionReason, string rejectedByName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Deferral Rejected</h2>
                    <p>Hello {userName},</p>
                    <p>Your deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong> has been rejected by <strong>{rejectedByName}</strong>.</p>
                    <p><strong>Reason:</strong> {rejectionReason}</p>
                    <p>Please review and take the next action in the system.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralReturnedToRmHtml(string userName, string deferralNumber, string customerName, string reworkComment, string returnedByName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Deferral Returned for Rework</h2>
                    <p>Hello {userName},</p>
                    <p>Your deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong> has been returned for rework by <strong>{returnedByName}</strong>.</p>
                    <p><strong>Rework instructions:</strong> {reworkComment}</p>
                    <p>Please review and update the deferral in the system.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralReturnConfirmationHtml(string userName, string deferralNumber, string customerName, string reworkComment)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Return for Rework Recorded</h2>
                    <p>Hello {userName},</p>
                    <p>You have successfully returned deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong> for rework.</p>
                    <p><strong>Instructions sent to RM:</strong> {reworkComment}</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDeferralRejectConfirmationHtml(string userName, string deferralNumber, string customerName, string rejectionReason)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Rejection Recorded</h2>
                    <p>Hello {userName},</p>
                    <p>You have successfully rejected deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong>.</p>
                    <p><strong>Reason recorded:</strong> {rejectionReason}</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ HTML template for checker status changed
    private string GetCheckerStatusChangedHtml(string userName, string dclNo, string status)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>DCL Status Update</h2>
                    <p>Hello {userName},</p>
                    <p>Your DCL <strong>{dclNo}</strong> status has been changed to <strong>{status}</strong>.</p>
                    <p>Please log in to the system to review the details.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ HTML template for extension approval request
    private string GetExtensionApprovalRequestHtml(string userName, string deferralNumber, string requesterName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Extension Request</h2>
                    <p>Hello {userName},</p>
                    <p><strong>{requesterName}</strong> has requested an extension for Deferral <strong>{deferralNumber}</strong>.</p>
                    <p>Please review and take action in the system.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ HTML template for extension status update
    private string GetExtensionStatusUpdateHtml(string userName, string deferralNumber, string status)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Extension Request Update</h2>
                    <p>Hello {userName},</p>
                    <p>The extension request for Deferral <strong>{deferralNumber}</strong> has been <strong>{status}</strong>.</p>
                    <p>Please log in to the system to view the details.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ HTML template for checker approval
    private string GetCheckerApprovedHtml(string userName, string dclNo, string checkerName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>DCL Approved</h2>
                    <p>Hello {userName},</p>
                    <p>Your DCL <strong>{dclNo}</strong> has been <strong>approved</strong> by checker <strong>{checkerName}</strong>.</p>
                    <p>Thank you for your submission.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ HTML template for checker returned
    private string GetCheckerReturnedHtml(string userName, string dclNo, string checkerName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>DCL Returned for Revision</h2>
                    <p>Hello {userName},</p>
                    <p>Your DCL <strong>{dclNo}</strong> has been <strong>returned</strong> by checker <strong>{checkerName}</strong> for revision.</p>
                    <p>Please review the feedback and resubmit.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetFirstApproverReplacedHtml(string userName, string deferralNumber, string customerName, string replacementName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Approval Flow Updated</h2>
                    <p>Hello {userName},</p>
                    <p>You are no longer the first approver for deferral <strong>{deferralNumber}</strong> ({customerName}).</p>
                    <p><strong>New first approver:</strong> {replacementName}</p>
                    <p>If you have any questions, please contact the Relationship Manager.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetFirstApproverAssignedHtml(string userName, string deferralNumber, string customerName, string replacedName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>New First Approver Assignment</h2>
                    <p>Hello {userName},</p>
                    <p>You have been assigned as the first approver for deferral <strong>{deferralNumber}</strong> ({customerName}).</p>
                    <p><strong>Previous first approver:</strong> {replacedName}</p>
                    <p>Please review and take action in the DCL system.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ NEW: Email verification code HTML helper
    private string GetEmailVerificationCodeHtml(string verificationCode, int expiryMinutes)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Email Verification Code</h2>
                    <p>Your email verification code is:</p>
                    <p style=""font-size: 24px; font-weight: bold; color: #164679; letter-spacing: 2px;"">{verificationCode}</p>
                    <p>This code will expire in <strong>{expiryMinutes} minutes</strong>.</p>
                    <p>If you did not request this code, please ignore this email.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    // ✅ NEW: Logout verification HTML helper
    private string GetLogoutVerificationHtml(string userName, string verificationCode, string ipAddress, string userAgent)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Logout Verification Required</h2>
                    <p>Hello {userName},</p>
                    <p>A logout request has been initiated on your account.</p>
                    <p><strong>Verification Code:</strong></p>
                    <p style=""font-size: 20px; font-weight: bold; color: #164679;"">{verificationCode}</p>
                    <p><strong>Login Details:</strong></p>
                    <ul>
                        <li><strong>IP Address:</strong> {ipAddress}</li>
                        <li><strong>Device:</strong> {userAgent}</li>
                        <li><strong>Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</li>
                    </ul>
                    <p>If this logout request was not initiated by you, please contact support immediately.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    public async Task SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status)
    {
        var subject = $"DCL {dclNo} Status Update";
        var htmlBody = GetCheckerStatusChangedHtml(userName, dclNo, status);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralSubmittedAsync(string toEmail, string userName, string deferralNumber, string customerName, int daysSought, string recipientRole)
    {
        var subject = $"Deferral Submitted: {deferralNumber}";
        var htmlBody = GetDeferralSubmittedHtml(userName, deferralNumber, customerName, daysSought, recipientRole);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralReminderAsync(string toEmail, string userName, string deferralNumber, string customerName)
    {
        var subject = $"Reminder: Deferral {deferralNumber} Awaiting Approval";
        var htmlBody = GetDeferralReminderHtml(userName, deferralNumber, customerName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralApprovalConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval)
    {
        var subject = isFinalApproval
            ? $"Confirmation: Final Approval Recorded for {deferralNumber}"
            : $"Confirmation: You Approved {deferralNumber}";
        var htmlBody = GetDeferralApprovalConfirmationHtml(userName, deferralNumber, customerName, nextApproverName, isFinalApproval);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralApprovedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval)
    {
        var subject = isFinalApproval
            ? $"Deferral Approved: Final Approval Recorded for {deferralNumber}"
            : $"Deferral Update: {deferralNumber} moved to next approver";

        var htmlBody = GetDeferralApprovedToRmHtml(userName, deferralNumber, customerName, nextApproverName, isFinalApproval);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralRejectedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason, string rejectedByName)
    {
        var subject = $"Deferral Rejected: {deferralNumber}";
        var htmlBody = GetDeferralRejectedToRmHtml(userName, deferralNumber, customerName, rejectionReason, rejectedByName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralReturnedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment, string returnedByName)
    {
        var subject = $"Deferral Returned for Rework: {deferralNumber}";
        var htmlBody = GetDeferralReturnedToRmHtml(userName, deferralNumber, customerName, reworkComment, returnedByName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralReturnConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment)
    {
        var subject = $"Confirmation: You Returned {deferralNumber} for Rework";
        var htmlBody = GetDeferralReturnConfirmationHtml(userName, deferralNumber, customerName, reworkComment);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendDeferralRejectConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason)
    {
        var subject = $"Confirmation: You Rejected {deferralNumber}";
        var htmlBody = GetDeferralRejectConfirmationHtml(userName, deferralNumber, customerName, rejectionReason);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName)
    {
        var subject = $"Extension Request for {deferralNumber}";
        var htmlBody = GetExtensionApprovalRequestHtml(userName, deferralNumber, requesterName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status)
    {
        var subject = $"Extension Update for {deferralNumber}";
        var htmlBody = GetExtensionStatusUpdateHtml(userName, deferralNumber, status);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Checker approval notification (aligns with Node.js sendCheckerApproved)
    public async Task SendCheckerApprovedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName)
    {
        var subject = $"DCL {dclNo} Approved by Checker";
        var htmlBody = GetCheckerApprovedHtml(userName, dclNo, checkerName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Checker returned notification (aligns with Node.js sendCheckerReturned)
    public async Task SendCheckerReturnedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName)
    {
        var subject = $"DCL {dclNo} Returned by Checker";
        var htmlBody = GetCheckerReturnedHtml(userName, dclNo, checkerName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendFirstApproverReplacedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacementName)
    {
        var subject = $"First Approver Replaced: {deferralNumber}";
        var htmlBody = GetFirstApproverReplacedHtml(userName, deferralNumber, customerName, replacementName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task SendFirstApproverAssignedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacedName)
    {
        var subject = $"New First Approver Assignment: {deferralNumber}";
        var htmlBody = GetFirstApproverAssignedHtml(userName, deferralNumber, customerName, replacedName);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Email verification code notification
    public async Task SendEmailVerificationCodeAsync(string toEmail, string verificationCode, int expiryMinutes)
    {
        var subject = "Email Verification Code";
        var htmlBody = GetEmailVerificationCodeHtml(verificationCode, expiryMinutes);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Logout verification email notification
    public async Task SendLogoutVerificationEmailAsync(string toEmail, string userName, string verificationCode, string ipAddress, string userAgent)
    {
        var subject = "Logout Verification Required";
        var htmlBody = GetLogoutVerificationHtml(userName, verificationCode, ipAddress, userAgent);
        await SendEmailAsync(toEmail, subject, htmlBody);
    }
}