using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FolderSync;

/// <summary>Sends notification emails over SMTP (e.g. Gmail with an App Password).</summary>
public static class Emailer
{
    public static void Send(EmailSettings s, string subject, string body, string? attachmentPath = null)
    {
        if (string.IsNullOrWhiteSpace(s.EffectiveFrom)) throw new InvalidOperationException("No 'From' address configured.");
        if (string.IsNullOrWhiteSpace(s.To)) throw new InvalidOperationException("No 'To' address configured.");

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(s.EffectiveFrom));
        foreach (var to in s.To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;

        var builder = new BodyBuilder { TextBody = body };
        if (s.AttachLog && !string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
            builder.Attachments.Add(attachmentPath);
        msg.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        // Port 587 = STARTTLS; port 465 = implicit SSL.
        var secure = s.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        client.Connect(s.SmtpHost, s.SmtpPort, secure);
        client.Authenticate(s.Username, s.GetPassword());
        client.Send(msg);
        client.Disconnect(true);
    }

    /// <summary>Builds the summary subject/body for a finished sync.</summary>
    public static (string subject, string body) BuildSummary(SyncResult result, SyncConfig config, string logPath)
    {
        string status = result.Success ? "OK" : "ERRORS";
        string subject = $"[Folder Sync] {status} — {DateTime.Now:yyyy-MM-dd HH:mm}";

        var sb = new StringBuilder();
        sb.AppendLine($"Folder Sync finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss} on {Environment.MachineName}.");
        sb.AppendLine($"Result: {(result.Success ? "Success" : "Completed WITH ERRORS")}");
        sb.AppendLine();
        sb.AppendLine($"Source: {config.Source}");
        sb.AppendLine($"Mode:   {(config.Mirror ? "Mirror" : "Additive")}");
        sb.AppendLine();
        sb.AppendLine("Destinations:");
        foreach (var t in result.Targets)
            sb.AppendLine($"  [{(t.Success ? "OK " : "FAIL")}] {t.Name} -> {t.Destination}   (robocopy exit {t.ExitCode})");
        sb.AppendLine();
        sb.AppendLine($"Full log: {logPath}");
        return (subject, sb.ToString());
    }
}
