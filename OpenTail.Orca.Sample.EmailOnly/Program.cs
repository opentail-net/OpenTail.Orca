using OpenTail.Connectivity.Inbox.Imap;
using OpenTail.Orca.Admin;
using OpenTail.Modules.Email;

// Credentials come from the environment, never from tool arguments and never from the policy file.
//   IMAP_HOST IMAP_USER IMAP_PASSWORD [IMAP_PORT]   SMTP_HOST SMTP_USER SMTP_PASSWORD [SMTP_PORT]
//   ALLOWED_RECIPIENT_DOMAINS="corp.example;partner.example"   (optional hard limit on who can be written to)
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Set the environment variable {name}.");

static int? Port(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var p) ? p : null;

return await DeploymentRunner.RunAsync(args, "opentail-orca-email", builder =>
{
    var imap = ImapSettings.Create(Required("IMAP_HOST"), Port("IMAP_PORT"), null, Required("IMAP_USER"), Required("IMAP_PASSWORD"));
    var smtp = SmtpSettings.Create(Required("SMTP_HOST"), Port("SMTP_PORT"), null, Required("SMTP_USER"), Required("SMTP_PASSWORD"));
    var domains = (Environment.GetEnvironmentVariable("ALLOWED_RECIPIENT_DOMAINS") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    builder.AddModule(new EmailModule(
        new ImapInboxAdapter(imap, "default"),
        new ImapEmailSender(smtp, imap),
        new EmailModuleOptions { AllowedRecipientDomains = domains }));
});
