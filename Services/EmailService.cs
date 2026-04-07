
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NCBA.DCL.Services;

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _smtpHost;
    private readonly int _smtpPort;
    private readonly string? _smtpUser;
    private readonly string? _smtpPass;
    private readonly bool _smtpSecure;
    private readonly string? _emailFrom;
    private readonly string _loginUrl;
    private readonly string? _sendGridApiKey;

    public EmailService(ILogger<EmailService> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;

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

        _sendGridApiKey =
            configuration["EmailSettings:SendGridApiKey"]
            ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

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
                $"SENDGRID: {(!string.IsNullOrWhiteSpace(_sendGridApiKey) ? "✅ set" : "❌ missing")}, " +
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
    private async Task<bool> SendEmailAsync(string to, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(htmlBody))
        {
            throw new ArgumentException("Email parameters: to, subject, and htmlBody are required");
        }

        htmlBody = EnsureLoginButtonInEmail(htmlBody);
        Exception? smtpException = null;

        _logger.LogInformation(
            "📨 Email dispatch requested. To: {To} | Subject: {Subject} | SMTP: {Host}:{Port} | Secure: {Secure} | SendGridConfigured: {SendGridConfigured}",
            to,
            subject,
            _smtpHost ?? "<none>",
            _smtpPort,
            _smtpSecure,
            !string.IsNullOrWhiteSpace(_sendGridApiKey));

        if (!string.IsNullOrWhiteSpace(_smtpHost) && !string.IsNullOrWhiteSpace(_smtpUser))
        {
            try
            {
                using (var client = new SmtpClient(_smtpHost, _smtpPort))
                {
                    client.UseDefaultCredentials = false;
                    client.EnableSsl = _smtpSecure;
                    client.DeliveryMethod = SmtpDeliveryMethod.Network;
                    client.Credentials = new NetworkCredential(_smtpUser, _smtpPass);
                    client.Timeout = 10000;

                    using (var mailMessage = new MailMessage(_emailFrom ?? _smtpUser, to))
                    {
                        mailMessage.Subject = subject;
                        mailMessage.Body = htmlBody;
                        mailMessage.IsBodyHtml = true;

                        await client.SendMailAsync(mailMessage);
                        _logger.LogInformation("✅ [EMAIL SENT VIA SMTP] To: {To} | Subject: {Subject}", to, subject);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                smtpException = ex;
                _logger.LogWarning(ex,
                    "⚠️ SMTP email delivery failed for {To} via {Host}:{Port} (Secure: {Secure}). Will try HTTP provider fallback if configured.",
                    to,
                    _smtpHost,
                    _smtpPort,
                    _smtpSecure);
            }
        }

        if (await SendEmailViaSendGridAsync(to, subject, htmlBody))
        {
            return true;
        }

        if (smtpException != null)
        {
            _logger.LogError(smtpException, "❌ Failed to send email to {To}. Subject: {Subject}", to, subject);
        }
        else
        {
            _logger.LogWarning("⚠️ No email transport configured for {To}. SMTP and SendGrid are both unavailable.", to);
        }

        return false;
    }

    private async Task<bool> SendEmailViaSendGridAsync(string to, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_sendGridApiKey) || string.IsNullOrWhiteSpace(_emailFrom))
        {
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _sendGridApiKey);

            var payload = new
            {
                personalizations = new[]
                {
                    new { to = new[] { new { email = to } } }
                },
                from = new { email = _emailFrom },
                subject,
                content = new object[]
                {
                    new { type = "text/plain", value = StripHtml(htmlBody) },
                    new { type = "text/html", value = htmlBody }
                },
                tracking_settings = new
                {
                    click_tracking = new { enable = false, enable_text = false }
                }
            };

            using var response = await client.PostAsync(
                "https://api.sendgrid.com/v3/mail/send",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("⚠️ SendGrid email delivery failed for {To}. Status: {Status}. Body: {Body}", to, (int)response.StatusCode, errorBody);
                return false;
            }

            _logger.LogInformation("✅ [EMAIL SENT VIA SENDGRID] To: {To} | Subject: {Subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ SendGrid email delivery threw an exception for {To}", to);
            return false;
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
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

    private string GetDeferralClosedHtml(string userName, string deferralNumber, string customerName, string closedByName, string closeComment)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Deferral Closed</h2>
                    <p>Hello {userName},</p>
                    <p>Deferral <strong>{deferralNumber}</strong> for customer <strong>{customerName}</strong> has been closed by <strong>{closedByName}</strong>.</p>
                    <p><strong>Comment:</strong> {closeComment}</p>
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

    private string GetDclSubmittedToRmHtml(string userName, string dclNo, string submittedByName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>DCL Submitted For RM Review</h2>
                    <p>Hello {userName},</p>
                    <p>DCL <strong>{dclNo}</strong> has been submitted to you for review by <strong>{submittedByName}</strong>.</p>
                    <p>Please log in to the system to review the checklist.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDclSubmittedToCoCheckerHtml(string userName, string dclNo, string submittedByName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>DCL Submitted For Co-Checker Approval</h2>
                    <p>Hello {userName},</p>
                    <p>DCL <strong>{dclNo}</strong> has been submitted to you for approval by <strong>{submittedByName}</strong>.</p>
                    <p>Please log in to the system to review and take action.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetDclReturnedToCoCreatorHtml(string userName, string dclNo, string submittedByName)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>DCL Returned To Co-Creator</h2>
                    <p>Hello {userName},</p>
                    <p>DCL <strong>{dclNo}</strong> has been sent back to you for review by <strong>{submittedByName}</strong>.</p>
                    <p>Please log in to the system to review the comments and continue processing.</p>
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

    private string GetApprovalFlowRemovedHtml(string userName, string deferralNumber, string customerName, string? currentApproverName)
    {
        var currentApproverText = string.IsNullOrWhiteSpace(currentApproverName)
            ? "The approval flow has changed and another approver will now handle the current step."
            : $"<strong>Current approver:</strong> {currentApproverName}";

        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Approval Flow Updated</h2>
                    <p>Hello {userName},</p>
                    <p>You were removed from the approval flow for deferral <strong>{deferralNumber}</strong> ({customerName}).</p>
                    <p>{currentApproverText}</p>
                    <p>If this change is unexpected, please contact the Relationship Manager.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetApprovalFlowAddedHtml(string userName, string deferralNumber, string customerName, string assignedRole)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Added To Approval Flow</h2>
                    <p>Hello {userName},</p>
                    <p>You were added to the approval flow for deferral <strong>{deferralNumber}</strong> ({customerName}).</p>
                    <p><strong>Your role:</strong> {assignedRole}</p>
                    <p>Please review the deferral in the DCL system when it reaches your step.</p>
                    <hr>
                    <p>This is an automated email. Please do not reply.</p>
                </body>
            </html>";
    }

    private string GetApprovalFlowStepUpdatedHtml(string userName, string deferralNumber, string customerName, string roleLabel)
    {
        return $@"
            <html>
                <body style=""font-family: Arial, sans-serif; color: #333;"">
                    <h2>Approval Step Updated</h2>
                    <p>Hello {userName},</p>
                    <p>Your approval step for deferral <strong>{deferralNumber}</strong> ({customerName}) was updated.</p>
                    <p><strong>Role:</strong> {roleLabel}</p>
                    <p>Please review the updated approval sequence in the DCL system.</p>
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

    public async Task<bool> SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status)
    {
        var subject = $"DCL {dclNo} Status Update";
        var htmlBody = GetCheckerStatusChangedHtml(userName, dclNo, status);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDclSubmittedToRmAsync(string toEmail, string userName, string dclNo, string submittedByName)
    {
        var subject = $"DCL {dclNo} Submitted For Review";
        var htmlBody = GetDclSubmittedToRmHtml(userName, dclNo, submittedByName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDclSubmittedToCoCheckerAsync(string toEmail, string userName, string dclNo, string submittedByName)
    {
        var subject = $"DCL {dclNo} Submitted For Approval";
        var htmlBody = GetDclSubmittedToCoCheckerHtml(userName, dclNo, submittedByName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDclReturnedToCoCreatorAsync(string toEmail, string userName, string dclNo, string submittedByName)
    {
        var subject = $"DCL {dclNo} Returned To You";
        var htmlBody = GetDclReturnedToCoCreatorHtml(userName, dclNo, submittedByName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralSubmittedAsync(string toEmail, string userName, string deferralNumber, string customerName, int daysSought, string recipientRole)
    {
        var subject = $"Deferral Submitted: {deferralNumber}";
        var htmlBody = GetDeferralSubmittedHtml(userName, deferralNumber, customerName, daysSought, recipientRole);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralReminderAsync(string toEmail, string userName, string deferralNumber, string customerName)
    {
        var subject = $"Reminder: Deferral {deferralNumber} Awaiting Approval";
        var htmlBody = GetDeferralReminderHtml(userName, deferralNumber, customerName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralApprovalConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval)
    {
        var subject = isFinalApproval
            ? $"Confirmation: Final Approval Recorded for {deferralNumber}"
            : $"Confirmation: You Approved {deferralNumber}";
        var htmlBody = GetDeferralApprovalConfirmationHtml(userName, deferralNumber, customerName, nextApproverName, isFinalApproval);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralApprovedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval)
    {
        var subject = isFinalApproval
            ? $"Deferral Approved: Final Approval Recorded for {deferralNumber}"
            : $"Deferral Update: {deferralNumber} moved to next approver";

        var htmlBody = GetDeferralApprovedToRmHtml(userName, deferralNumber, customerName, nextApproverName, isFinalApproval);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralRejectedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason, string rejectedByName)
    {
        var subject = $"Deferral Rejected: {deferralNumber}";
        var htmlBody = GetDeferralRejectedToRmHtml(userName, deferralNumber, customerName, rejectionReason, rejectedByName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralReturnedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment, string returnedByName)
    {
        var subject = $"Deferral Returned for Rework: {deferralNumber}";
        var htmlBody = GetDeferralReturnedToRmHtml(userName, deferralNumber, customerName, reworkComment, returnedByName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralReturnConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment)
    {
        var subject = $"Confirmation: You Returned {deferralNumber} for Rework";
        var htmlBody = GetDeferralReturnConfirmationHtml(userName, deferralNumber, customerName, reworkComment);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralRejectConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason)
    {
        var subject = $"Confirmation: You Rejected {deferralNumber}";
        var htmlBody = GetDeferralRejectConfirmationHtml(userName, deferralNumber, customerName, rejectionReason);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendDeferralClosedAsync(string toEmail, string userName, string deferralNumber, string customerName, string closedByName, string closeComment)
    {
        var subject = $"Deferral Closed: {deferralNumber}";
        var htmlBody = GetDeferralClosedHtml(userName, deferralNumber, customerName, closedByName, closeComment);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName)
    {
        var subject = $"Extension Request for {deferralNumber}";
        var htmlBody = GetExtensionApprovalRequestHtml(userName, deferralNumber, requesterName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status)
    {
        var subject = $"Extension Update for {deferralNumber}";
        var htmlBody = GetExtensionStatusUpdateHtml(userName, deferralNumber, status);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Checker approval notification (aligns with Node.js sendCheckerApproved)
    public async Task<bool> SendCheckerApprovedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName)
    {
        var subject = $"DCL {dclNo} Approved by Checker";
        var htmlBody = GetCheckerApprovedHtml(userName, dclNo, checkerName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Checker returned notification (aligns with Node.js sendCheckerReturned)
    public async Task<bool> SendCheckerReturnedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName)
    {
        var subject = $"DCL {dclNo} Returned by Checker";
        var htmlBody = GetCheckerReturnedHtml(userName, dclNo, checkerName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendFirstApproverReplacedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacementName)
    {
        var subject = $"First Approver Replaced: {deferralNumber}";
        var htmlBody = GetFirstApproverReplacedHtml(userName, deferralNumber, customerName, replacementName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendFirstApproverAssignedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacedName)
    {
        var subject = $"New First Approver Assignment: {deferralNumber}";
        var htmlBody = GetFirstApproverAssignedHtml(userName, deferralNumber, customerName, replacedName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendApprovalFlowRemovedAsync(string toEmail, string userName, string deferralNumber, string customerName, string? currentApproverName)
    {
        var subject = $"Approval Flow Updated: {deferralNumber}";
        var htmlBody = GetApprovalFlowRemovedHtml(userName, deferralNumber, customerName, currentApproverName);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendApprovalFlowAddedAsync(string toEmail, string userName, string deferralNumber, string customerName, string assignedRole)
    {
        var subject = $"Added To Approval Flow: {deferralNumber}";
        var htmlBody = GetApprovalFlowAddedHtml(userName, deferralNumber, customerName, assignedRole);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    public async Task<bool> SendApprovalFlowStepUpdatedAsync(string toEmail, string userName, string deferralNumber, string customerName, string roleLabel)
    {
        var subject = $"Approval Step Updated: {deferralNumber}";
        var htmlBody = GetApprovalFlowStepUpdatedHtml(userName, deferralNumber, customerName, roleLabel);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Email verification code notification
    public async Task<bool> SendEmailVerificationCodeAsync(string toEmail, string verificationCode, int expiryMinutes)
    {
        var subject = "Email Verification Code";
        var htmlBody = GetEmailVerificationCodeHtml(verificationCode, expiryMinutes);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }

    // ✅ NEW: Logout verification email notification
    public async Task<bool> SendLogoutVerificationEmailAsync(string toEmail, string userName, string verificationCode, string ipAddress, string userAgent)
    {
        var subject = "Logout Verification Required";
        var htmlBody = GetLogoutVerificationHtml(userName, verificationCode, ipAddress, userAgent);
        return await SendEmailAsync(toEmail, subject, htmlBody);
    }
}