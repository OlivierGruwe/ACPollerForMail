using ACPoller.Abstractions.Infrastructure;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ACPoller.Core.Observability;

/// <summary>
/// N'envoie rien. Implementation par defaut quand aucun acquittement n'est
/// configure.
/// </summary>
public sealed class NullAckSender(ILogger<NullAckSender> logger) : IAckSender
{
    /// <inheritdoc />
    public Task SendAsync(AckMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Trace en debug : en recette, savoir qu'une notification AURAIT ete
        // envoyee et a qui vaut mieux qu'un silence complet.
        logger.LogDebug(
            "Acquittement non envoye (emetteur nul) : {Subject} vers {To}",
            message.Subject, message.To);

        return Task.CompletedTask;
    }
}

/// <summary>Reglages de l'emetteur SMTP.</summary>
public sealed record SmtpAckOptions
{
    /// <summary>Hote SMTP.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Port. 587 en STARTTLS, 465 en SSL implicite, 25 sans chiffrement.</summary>
    public int Port { get; init; } = 587;

    /// <summary>Utilisateur. Vide pour un relais interne sans authentification.</summary>
    public string? UserName { get; init; }

    /// <summary>Mot de passe, dechiffre a la construction.</summary>
    public string? Password { get; init; }

    /// <summary>Adresse d'expedition.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Nom affiche de l'expediteur.</summary>
    public string FromDisplayName { get; init; } = "ACPoller";

    /// <summary>Chiffrement : "starttls", "ssl" ou "none".</summary>
    public string Encryption { get; init; } = "starttls";

    /// <summary>Destinataire en copie cachee, pour l'archivage des notifications.</summary>
    public string? BccAddress { get; init; }
}

/// <summary>
/// Envoie les acquittements et notifications de rejet par SMTP.
/// </summary>
/// <remarks>
/// Un echec d'envoi ne remonte PAS en exception. La notification est un effet de
/// bord du traitement : le message a deja ete exporte avec succes quand elle
/// part, et faire echouer le traitement a ce stade rejouerait un export deja
/// reussi, donc creerait un doublon en GED pour un simple probleme de relais.
/// L'echec est journalise en Error, ce qui suffit a le voir en supervision.
/// </remarks>
public sealed class SmtpAckSender(SmtpAckOptions options, ILogger<SmtpAckSender> logger) : IAckSender
{
    /// <inheritdoc />
    public async Task SendAsync(AckMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var mail = new MimeMessage();
            mail.From.Add(new MailboxAddress(options.FromDisplayName, options.From));
            mail.To.Add(MailboxAddress.Parse(message.To));

            if (!string.IsNullOrWhiteSpace(options.BccAddress))
            {
                mail.Bcc.Add(MailboxAddress.Parse(options.BccAddress));
            }

            mail.Subject = message.Subject;

            var builder = new BodyBuilder();

            if (message.IsHtml)
            {
                builder.HtmlBody = message.Body;
            }
            else
            {
                builder.TextBody = message.Body;
            }

            foreach (var attachment in message.Attachments.Where(File.Exists))
            {
                await builder.Attachments.AddAsync(attachment, cancellationToken).ConfigureAwait(false);
            }

            mail.Body = builder.ToMessageBody();

            if (message.CorrelationId is not null)
            {
                // En-tete de correlation : permet de relier une notification
                // recue par le client a la trace du service, sans avoir a
                // chercher par sujet et par date.
                mail.Headers.Add("X-ACPoller-CorrelationId", message.CorrelationId);
            }

            await SendCoreAsync(mail, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Acquittement envoye a {To} ({CorrelationId})", message.To, message.CorrelationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException
            or AuthenticationException or IOException or FormatException)
        {
            // Volontairement avale : voir la remarque de classe.
            logger.LogError(
                ex,
                "Acquittement non envoye a {To} ({CorrelationId}) : {Reason}",
                message.To, message.CorrelationId, ex.Message);
        }
    }

    private async Task SendCoreAsync(MimeMessage mail, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();

        var security = options.Encryption.ToLowerInvariant() switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };

        await client.ConnectAsync(options.Host, options.Port, security, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            await client.AuthenticateAsync(
                options.UserName,
                options.Password ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }
}
