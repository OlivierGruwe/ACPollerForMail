# ACPoller.Sources.Imap

## A implementer

1. `ImapMailSource` sur MailKit : `FETCH BODY[]` pour le MIME brut, meme contrat
   que Graph donc un seul parseur en aval.
2. Reconnexion et reprise sur coupure, avec qualification `FailureKind.Transient`.
3. Resolution des dossiers via les capacites SPECIAL-USE quand le serveur les expose,
   repli sur le nom sinon.
