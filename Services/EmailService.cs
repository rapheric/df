using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace NCBA.DCL.Services;

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly IConfiguration _configuration;

    public EmailService(ILogger<EmailService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status)
    {
        // TODO: Implement actual SMTP sending
        // var smtpServer = _configuration["EmailSettings:SmtpServer"];
        
        _logger.LogInformation($"[EMAIL SENT] To: {toEmail} | Subject: DCL {dclNo} Status Update | Body: Hello {userName}, DCL {dclNo} status has been changed to {status}.");
        return Task.CompletedTask;
    }

    public Task SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName)
    {
        _logger.LogInformation($"[EMAIL SENT] To: {toEmail} | Subject: Extension Request for {deferralNumber} | Body: Hello {userName}, {requesterName} has requested an extension for Deferral {deferralNumber}. Please review.");
        return Task.CompletedTask;
    }

    public Task SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status)
    {
         _logger.LogInformation($"[EMAIL SENT] To: {toEmail} | Subject: Extension Update for {deferralNumber} | Body: Hello {userName}, The extension request for Deferral {deferralNumber} has been {status}.");
        return Task.CompletedTask;
    }
}
