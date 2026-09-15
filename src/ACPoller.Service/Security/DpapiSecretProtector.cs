using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ACPoller.Abstractions.Infrastructure;

namespace ACPoller.Service.Security;

/// <summary>
/// Chiffre les secrets de configuration avec DPAPI, portee machine.
/// </summary>
/// <remarks>
/// Portee machine et non utilisateur : le service tourne sous un compte de
/// service AD, et la configuration est editee depuis l'UI sous un compte
/// d'exploitation. Une portee utilisateur rendrait le secret illisible par
/// celui qui ne l'a pas chiffre.
///
/// Consequence a assumer et a documenter cote exploitation : le fichier de
/// configuration n'est dechiffrable que sur LA MACHINE ou il a ete chiffre.
/// Copier un appsettings.json d'un serveur a l'autre ne fonctionne pas, il faut
/// ressaisir les secrets. C'est une contrainte, pas un defaut : cela evite
/// qu'un fichier de configuration exfiltre soit exploitable ailleurs.
///
/// Si un jour un deploiement multi-serveurs impose le partage, la bascule se
/// fera vers un coffre (DPAPI-NG, Windows Credential Manager avec compte
/// gere, ou Azure Key Vault) sans toucher aux appelants, grace a l'interface.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "ENC:";

    // Entropie supplementaire : un secret chiffre par ce service ne peut pas
    // etre dechiffre par une autre application tournant sur la meme machine.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ACPoller.v2.SecretProtector");

    /// <inheritdoc />
    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        if (IsProtected(plainText))
        {
            // Idempotent : rechiffrer une valeur deja chiffree la rendrait
            // illisible, et l'UI peut tres bien resauvegarder sans modifier.
            return plainText;
        }

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.LocalMachine);

        return Prefix + Convert.ToBase64String(encrypted);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        if (!IsProtected(protectedText))
        {
            // Valeur en clair dans le fichier : on l'accepte pour ne pas bloquer
            // une premiere mise en service, le chiffrement aura lieu a la
            // premiere sauvegarde depuis l'UI.
            return protectedText;
        }

        var payload = protectedText[Prefix.Length..];

        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(payload),
                Entropy,
                DataProtectionScope.LocalMachine);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Cas le plus frequent : configuration copiee depuis une autre
            // machine. Le message doit le dire, sinon le diagnostic prend
            // une demi-journee.
            throw new InvalidOperationException(
                "Dechiffrement impossible. Le secret a probablement ete chiffre sur une autre "
                + "machine : DPAPI en portee machine ne le permet pas. Ressaisir les secrets "
                + "depuis l'UI sur ce serveur.",
                ex);
        }
    }

    /// <inheritdoc />
    public bool IsProtected(string value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);
}
