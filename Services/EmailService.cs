using System;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NCBA.DCL.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent)
        {
            try
            {
                var smtpServer = _configuration["EmailSettings:SmtpServer"];
                var smtpPort = int.Parse(_configuration["EmailSettings:SmtpPort"] ?? "587");
                var senderEmail = _configuration["EmailSettings:SenderEmail"];
                var senderPassword = _configuration["EmailSettings:SenderPassword"];

                using (var client = new SmtpClient(smtpServer, smtpPort))
                {
                    client.EnableSsl = true;
                    client.Credentials = new NetworkCredential(senderEmail, senderPassword);

                    var mailMessage = new MailMessage
                    {
                        From = new MailAddress(senderEmail),
                        Subject = subject,
                        Body = htmlContent,
                        IsBodyHtml = true
                    };

                    mailMessage.To.Add(toEmail);

                    await client.SendMailAsync(mailMessage);
                    _logger.LogInformation($"Email sent successfully to {toEmail}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending email to {toEmail}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendEmailVerificationCodeAsync(string toEmail, string code, int expiryMinutes = 15)
        {
            var htmlContent = BuildEmailVerificationHtml(code, expiryMinutes);
            return await SendEmailAsync(toEmail, "Email Verification Code", htmlContent);
        }

        public async Task<bool> SendLogoutVerificationEmailAsync(string toEmail, string userName, string code, string ipAddress, string userAgent)
        {
            var deviceInfo = ExtractDeviceInfoFromUserAgent(userAgent);
            var htmlContent = BuildLogoutVerificationHtml(code, userName, ipAddress, deviceInfo);
            return await SendEmailAsync(toEmail, "Logout Verification Required", htmlContent);
        }

        public async Task<bool> SendMFAEnabledNotificationAsync(string toEmail, string userName, int backupCodeCount = 10)
        {
            var htmlContent = BuildMFAEnabledHtml(userName, backupCodeCount);
            return await SendEmailAsync(toEmail, "Multi-Factor Authentication Enabled", htmlContent);
        }

        private string BuildEmailVerificationHtml(string code, int expiryMinutes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".code-box { background-color: #f9f9f9; border: 2px solid #2196F3; padding: 20px; text-align: center; margin: 20px 0; border-radius: 4px; }");
            sb.AppendLine(".code { font-size: 32px; font-weight: bold; letter-spacing: 5px; color: #2196F3; }");
            sb.AppendLine(".message { color: #666; line-height: 1.6; margin: 15px 0; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class='container'>");
            sb.AppendLine("<div class='header'><h2>Email Verification</h2></div>");
            sb.AppendLine("<p class='message'>Hello,</p>");
            sb.AppendLine("<p class='message'>Your email verification code is:</p>");
            sb.AppendLine("<div class='code-box'>");
            sb.AppendLine($"<div class='code'>{code}</div>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<p class='message'>This code will expire in <strong>{expiryMinutes} minutes</strong>.</p>");
            sb.AppendLine("<p class='message'>If you did not request this verification, please ignore this email.</p>");
            sb.AppendLine("<div class='footer'>");
            sb.AppendLine("<p>This is an automated email. Please do not reply to this message.</p>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private string BuildLogoutVerificationHtml(string code, string userName, string ipAddress, string deviceInfo)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".alert { background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; }");
            sb.AppendLine(".alert-title { font-weight: bold; color: #856404; margin-bottom: 8px; }");
            sb.AppendLine(".code-box { background-color: #f0f0f0; border: 2px solid #ff9800; padding: 20px; text-align: center; margin: 20px 0; border-radius: 4px; }");
            sb.AppendLine(".code { font-size: 32px; font-weight: bold; letter-spacing: 5px; color: #ff9800; }");
            sb.AppendLine(".device-info { background-color: #f9f9f9; border: 1px solid #ddd; padding: 15px; margin: 15px 0; border-radius: 4px; font-size: 13px; color: #666; }");
            sb.AppendLine(".message { color: #666; line-height: 1.6; margin: 15px 0; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class='container'>");
            sb.AppendLine("<div class='header'><h2>Logout Verification Required</h2></div>");
            sb.AppendLine($"<p class='message'>Hello {userName},</p>");
            sb.AppendLine("<div class='alert'>");
            sb.AppendLine("<div class='alert-title'>Security Alert</div>");
            sb.AppendLine("<div>A logout request was initiated on your account. To complete the logout, please verify using the code below.</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<p class='message'>Your logout verification code is:</p>");
            sb.AppendLine("<div class='code-box'>");
            sb.AppendLine($"<div class='code'>{code}</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class='device-info'>");
            sb.AppendLine("<strong>Logout Request Details:</strong><br>");
            sb.AppendLine($"IP Address: {ipAddress}<br>");
            sb.AppendLine($"Device: {deviceInfo}<br>");
            sb.AppendLine($"Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("</div>");
            sb.AppendLine("<p class='message'><strong>If this was not you:</strong> Do not share this code and immediately change your password.</p>");
            sb.AppendLine("<div class='footer'>");
            sb.AppendLine("<p>This is an automated email. Please do not reply to this message.</p>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private string BuildMFAEnabledHtml(string userName, int backupCodeCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".success-box { background-color: #d4edda; border: 1px solid #c3e6cb; border-left: 4px solid #28a745; padding: 15px; margin: 15px 0; border-radius: 4px; color: #155724; }");
            sb.AppendLine(".info-box { background-color: #d1ecf1; border: 1px solid #bee5eb; border-left: 4px solid #17a2b8; padding: 15px; margin: 15px 0; border-radius: 4px; color: #0c5460; }");
            sb.AppendLine(".message { color: #666; line-height: 1.6; margin: 15px 0; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class='container'>");
            sb.AppendLine("<div class='header'><h2>Multi-Factor Authentication Enabled</h2></div>");
            sb.AppendLine($"<p class='message'>Hello {userName},</p>");
            sb.AppendLine("<div class='success-box'>");
            sb.AppendLine("<strong>Success!</strong> Multi-factor authentication (MFA) has been enabled on your account.");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class='info-box'>");
            sb.AppendLine($"<strong>Backup Codes:</strong> You have {backupCodeCount} backup codes available. Please store them in a secure location. These codes can be used to access your account if you lose access to your authenticator app.");
            sb.AppendLine("</div>");
            sb.AppendLine("<p class='message'><strong>What's Next:</strong></p>");
            sb.AppendLine("<ul>");
            sb.AppendLine("<li>Download and save your backup codes securely</li>");
            sb.AppendLine("<li>Use your authenticator app when logging in</li>");
            sb.AppendLine("<li>Keep your backup codes in a safe place</li>");
            sb.AppendLine("</ul>");
            sb.AppendLine("<p class='message'>If you did not enable MFA, please contact support immediately.</p>");
            sb.AppendLine("<div class='footer'>");
            sb.AppendLine("<p>This is an automated email. Please do not reply to this message.</p>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private string ExtractDeviceInfoFromUserAgent(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent))
                return "Unknown Device";

            userAgent = userAgent.ToLower();

            if (userAgent.Contains("windows"))
                return "Windows Desktop";
            else if (userAgent.Contains("mac"))
                return "Mac Desktop";
            else if (userAgent.Contains("iphone"))
                return "iPhone Mobile";
            else if (userAgent.Contains("ipad"))
                return "iPad Tablet";
            else if (userAgent.Contains("android"))
                return "Android Mobile";
            else if (userAgent.Contains("linux"))
                return "Linux Desktop";

            return "Unknown Device";
        }

        public async Task<bool> SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status)
        {
            var subject = $"Checklist Status Update - {dclNo}";
            var htmlContent = BuildStatusChangedHtml(userName, dclNo, status);
            return await SendEmailAsync(toEmail, subject, htmlContent);
        }

        public async Task<bool> SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName)
        {
            var subject = $"Extension Approval Request - {deferralNumber}";
            var htmlContent = BuildExtensionApprovalHtml(userName, deferralNumber, requesterName);
            return await SendEmailAsync(toEmail, subject, htmlContent);
        }

        public async Task<bool> SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status)
        {
            var subject = $"Extension Status Update - {deferralNumber}";
            var htmlContent = BuildExtensionStatusHtml(userName, deferralNumber, status);
            return await SendEmailAsync(toEmail, subject, htmlContent);
        }

        public async Task<bool> SendCheckerApprovedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName)
        {
            var subject = $"Checklist Approved - {dclNo}";
            var htmlContent = BuildCheckerApprovedHtml(userName, dclNo, checkerName);
            return await SendEmailAsync(toEmail, subject, htmlContent);
        }

        public async Task<bool> SendCheckerReturnedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName)
        {
            var subject = $"Checklist Returned - {dclNo}";
            var htmlContent = BuildCheckerReturnedHtml(userName, dclNo, checkerName);
            return await SendEmailAsync(toEmail, subject, htmlContent);
        }

        private string BuildStatusChangedHtml(string userName, string dclNo, string status)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='UTF-8'><style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".content { color: #666; line-height: 1.6; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class='container'><div class='header'><h2>Checklist Status Update</h2></div>");
            sb.AppendLine($"<div class='content'><p>Hello {userName},</p>");
            sb.AppendLine($"<p>The status of checklist <strong>{dclNo}</strong> has been updated to <strong>{status}</strong>.</p>");
            sb.AppendLine("<p>Please log in to the system to view more details.</p></div>");
            sb.AppendLine("<div class='footer'><p>This is an automated email. Please do not reply.</p></div>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private string BuildExtensionApprovalHtml(string userName, string deferralNumber, string requesterName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='UTF-8'><style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".content { color: #666; line-height: 1.6; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class='container'><div class='header'><h2>Extension Approval Request</h2></div>");
            sb.AppendLine($"<div class='content'><p>Hello {userName},</p>");
            sb.AppendLine($"<p>An extension approval request has been submitted for deferral <strong>{deferralNumber}</strong> by {requesterName}.</p>");
            sb.AppendLine("<p>Please log in to the system to review and take action.</p></div>");
            sb.AppendLine("<div class='footer'><p>This is an automated email. Please do not reply.</p></div>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private string BuildExtensionStatusHtml(string userName, string deferralNumber, string status)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='UTF-8'><style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".content { color: #666; line-height: 1.6; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class='container'><div class='header'><h2>Extension Status Update</h2></div>");
            sb.AppendLine($"<div class='content'><p>Hello {userName},</p>");
            sb.AppendLine($"<p>The extension for deferral <strong>{deferralNumber}</strong> has been updated to <strong>{status}</strong>.</p>");
            sb.AppendLine("<p>Please log in to the system for more details.</p></div>");
            sb.AppendLine("<div class='footer'><p>This is an automated email. Please do not reply.</p></div>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private string BuildCheckerApprovedHtml(string userName, string dclNo, string checkerName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='UTF-8'><style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".success-box { background-color: #d4edda; border: 1px solid #c3e6cb; border-left: 4px solid #28a745; padding: 15px; margin: 15px 0; border-radius: 4px; color: #155724; }");
            sb.AppendLine(".content { color: #666; line-height: 1.6; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class='container'><div class='header'><h2>Checklist Approved</h2></div>");
            sb.AppendLine("<div class='success-box'><p><strong>Your checklist has been approved!</strong></p></div>");
            sb.AppendLine($"<div class='content'><p>Hello {userName},</p>");
            sb.AppendLine($"<p>Your checklist <strong>{dclNo}</strong> has been approved by {checkerName}.</p>");
            sb.AppendLine("<p>Thank you for completing the required documentation.</p></div>");
            sb.AppendLine("<div class='footer'><p>This is an automated email. Please do not reply.</p></div>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private string BuildCheckerReturnedHtml(string userName, string dclNo, string checkerName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='UTF-8'><style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; background-color: #f5f5f5; }");
            sb.AppendLine(".container { max-width: 600px; margin: 20px auto; background-color: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".header { text-align: center; color: #333; margin-bottom: 20px; }");
            sb.AppendLine(".warning-box { background-color: #fff3cd; border: 1px solid #ffeaa7; border-left: 4px solid #ffc107; padding: 15px; margin: 15px 0; border-radius: 4px; color: #856404; }");
            sb.AppendLine(".content { color: #666; line-height: 1.6; }");
            sb.AppendLine(".footer { text-align: center; color: #999; font-size: 12px; margin-top: 20px; border-top: 1px solid #eee; padding-top: 20px; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class='container'><div class='header'><h2>Checklist Returned for Revision</h2></div>");
            sb.AppendLine("<div class='warning-box'><p><strong>Your checklist requires revision.</strong></p></div>");
            sb.AppendLine($"<div class='content'><p>Hello {userName},</p>");
            sb.AppendLine($"<p>Your checklist <strong>{dclNo}</strong> has been returned by {checkerName} for revision.</p>");
            sb.AppendLine("<p>Please review the feedback and make the necessary corrections before resubmitting.</p></div>");
            sb.AppendLine("<div class='footer'><p>This is an automated email. Please do not reply.</p></div>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }
    }
}
