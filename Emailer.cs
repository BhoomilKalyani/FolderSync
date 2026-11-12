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
        // MailKit 5 sends a request rather than a bare message. A recipient the server rejects still
        // throws (SmtpCommandException), as in 4.x, so a bad "To" address is reported, not skipped.
        client.Send(new MailKit.SendRequest(msg));
        client.Disconnect(true);
    }

    /// <summary>
    /// Builds the summary subject/body for a finished sync.
    ///
    /// Reports each pair from the run's own results. It deliberately does not read
    /// <see cref="SyncConfig.Source"/> or <see cref="SyncConfig.Mirror"/>: those are the pre-1.1.0
    /// single-source fields, which the move to folder pairs blanks - so an earlier version of this
    /// method reported an empty source and the wrong mode in every email it ever sent.
    /// </summary>
    public static (string subject, string body) BuildSummary(SyncResult result, SyncConfig config, string logPath)
    {
        int copied = result.Targets.Sum(t => t.FilesCopied);
        int failed = result.Targets.Sum(t => t.FilesFailed);
        int recycled = result.Targets.Sum(t => t.FilesRecycled);
        int kept = result.Targets.Sum(t => t.FilesBackedUp);
        int skipped = result.Targets.Count(t => t.Skipped);

        string status = result.Success ? (skipped > 0 ? "OK, some skipped" : "OK") : "ERRORS";
        string subject = $"[Folder Sync] {status} — {DateTime.Now:yyyy-MM-dd HH:mm}";

        var sb = new StringBuilder();
        sb.AppendLine($"Folder Sync finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss} on {Environment.MachineName}.");
        sb.AppendLine($"Result: {(result.Success ? "Success" : "Completed WITH ERRORS")}");
        sb.AppendLine();

        sb.AppendLine($"Files copied      : {copied}");
        sb.AppendLine($"Files failed      : {failed}");
        if (recycled > 0)
            sb.AppendLine($"Deletions kept    : {recycled}  (in \"{config.RecycleFolderName}\", restorable from the app)");
        if (kept > 0)
            sb.AppendLine($"Previous versions : {kept}  (replaced by a newer copy from the other side)");
        sb.AppendLine();

        sb.AppendLine("Pairs:");
        foreach (var t in result.Targets)
        {
            // SKIP is distinct from OK on purpose: a pair that never ran must not read as a success.
            string tag = t.Skipped ? "SKIP" : t.Success ? "OK  " : "FAIL";
            sb.AppendLine($"  [{tag}] {t.Name} -> {t.Destination}");

            if (t.Skipped || !t.Success)
                sb.AppendLine($"         {(string.IsNullOrWhiteSpace(t.Note) ? "no detail" : t.Note)}");
            else
                sb.AppendLine($"         copied {t.FilesCopied}, failed {t.FilesFailed}"
                            + (t.FilesRecycled > 0 ? $", deletions kept {t.FilesRecycled}" : "")
                            + (t.FilesBackedUp > 0 ? $", previous versions kept {t.FilesBackedUp}" : "")
                            + $"   (robocopy exit {t.ExitCode})");
        }

        if (skipped > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{skipped} pair(s) were skipped because a drive was not connected. "
                        + "That is not treated as an error.");
        }

        sb.AppendLine();
        sb.AppendLine($"Full log: {logPath}");
        return (subject, sb.ToString());
    }
}
