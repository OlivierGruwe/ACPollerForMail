using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ACPoller.Ui.Services;

namespace ACPoller.Ui;

/// <summary>
/// Icone de zone de notification : etat du service et alerte de redemarrage.
/// </summary>
/// <remarks>
/// L'interet reel n'est pas le raccourci vers la fenetre, c'est la NOTIFICATION.
/// Un exploitant qui modifie un reglage non rechargeable, ferme l'ecran et
/// passe a autre chose ne saura jamais que sa modification dort. Un bandeau
/// dans une fenetre fermee n'avertit personne.
///
/// La notification n'est emise qu'au CHANGEMENT d'etat, jamais a chaque
/// rafraichissement : une alerte repetee toutes les dix secondes serait
/// desactivee par l'exploitant au bout de deux minutes, et la prochaine, la
/// vraie, passerait inapercue.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;

    private bool _lastRestartRequired;
    private bool _lastConnected = true;

    /// <summary>Construit l'icone et l'attache a la fenetre principale.</summary>
    /// <param name="window">Fenetre a afficher sur activation.</param>
    public TrayIcon(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => Show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "ACPoller",
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => Show();

        // Un clic sur la bulle ouvre la fenetre : l'exploitant vient d'etre
        // averti, il veut agir, pas chercher ou cliquer.
        _icon.BalloonTipClicked += (_, _) => Show();
    }

    /// <summary>Met a jour l'icone selon l'etat du service.</summary>
    /// <param name="connected">Vrai si le service repond.</param>
    /// <param name="restartRequired">Vrai si un redemarrage est en attente.</param>
    /// <param name="summary">Resume affiche en infobulle.</param>
    public void Update(bool connected, bool restartRequired, string summary)
    {
        // L'infobulle Windows est bornee a 63 caracteres : au-dela, elle est
        // silencieusement tronquee ou refusee selon la version.
        var text = restartRequired
            ? "ACPoller - redemarrage requis"
            : $"ACPoller - {summary}";

        _icon.Text = text.Length > 63 ? text[..60] + "..." : text;

        if (restartRequired && !_lastRestartRequired)
        {
            _icon.ShowBalloonTip(
                10000,
                "ACPoller demande un redemarrage",
                "Un reglage modifie ne prendra effet qu'apres redemarrage du service. "
                + "Cliquer pour voir lesquels.",
                ToolTipIcon.Warning);
        }
        else if (!connected && _lastConnected)
        {
            _icon.ShowBalloonTip(
                10000,
                "ACPoller injoignable",
                "Le service ne repond plus. La capture est probablement arretee.",
                ToolTipIcon.Error);
        }

        _lastRestartRequired = restartRequired;
        _lastConnected = connected;
    }

    private void Show()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static Icon LoadIcon()
    {
        var stream = System.Windows.Application
            .GetResourceStream(new Uri("pack://application:,,,/Resources/acpoller.ico"))?.Stream;

        // Repli sur l'icone systeme : une exception ici empecherait le
        // demarrage de l'interface pour un defaut purement decoratif.
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Sans ce retrait explicite, l'icone reste affichee apres la fermeture
        // et ne disparait qu'au passage du pointeur dessus.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
