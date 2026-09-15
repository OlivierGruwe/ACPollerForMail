# ACPollerForMail — Operations guide

This document is written for whoever runs the service day to day, not for
whoever built it. It answers questions in the order they actually come up in
production.

---

## 1. Where things live

| Path | Contents | Critical |
|---|---|---|
| `C:\Program Files\ACPollerForMail\appsettings.json` | Configuration, encrypted secrets | Yes |
| `C:\Program Files\ACPollerForMail\logs\` | Service logs | No |
| `C:\ProgramData\ACPollerForMail\work\` | Working units in progress | No |
| `C:\ProgramData\ACPollerForMail\work\state\processed.db` | Processed-message log | **Critical** |
| `C:\ProgramData\ACPollerForMail\work\_quarantaine\` | Abandoned units, kept 30 days | No |
| `C:\ProgramData\ACPollerForMail\ui-settings.default.json` | Interface address and token | No |
| `%APPDATA%\ACPollerForMail\ui-settings.json` | Each operator's own settings | No |

### Backup

Two items belong in the server backup:

- `C:\ProgramData\ACPollerForMail\work\state\processed.db`
- `C:\Program Files\ACPollerForMail\appsettings.json`

**Losing `processed.db` causes everything still in the mailboxes to be
processed again**, hence duplicates in the ECM. It is the only file whose loss
has an irreversible consequence: the others can be rebuilt, this one carries
the history of what has already been delivered.

`appsettings.json` can be rebuilt, but every secret would have to be entered
again, an afternoon's work across a hundred mailboxes.

---

## 2. Checking that things are fine

Open the interface, **Dashboard** tab. Four numbers are enough:

**Success rate.** Below 100 %, the per-configuration breakdown at the bottom
names the mailbox involved.

**95th percentile duration.** A wide gap with the average points to slow
messages holding concurrent slots. Other mailboxes wait meanwhile.

**Graph throttling.** Non-zero in steady state means concurrency must come
down or spacing must go up, in the Protocol tab of the configuration
concerned.

**Free disk.** Below 15 % the indicator turns red. At saturation, capture
stops.

Without the interface, the same information in one command:

```
curl -H "X-ACPollerForMail-Token: <token>" http://127.0.0.1:5199/api/status
```

---

## 3. The five common incidents

### A message comes back every cycle

Marking is not happening, or the deduplication key is not stable.

First check that **delivery succeeds**: marking only happens once delivery is
confirmed, deliberately. A duplicate can be spotted and corrected, a silent
loss cannot.

If delivery succeeds, look at the disposition in the Protocol tab: with
`None`, the message stays unread and will be read again every cycle. That is
useful during acceptance testing, not in production.

### The merged PDF is empty or incomplete

Open the information file next to the PDF and look at the status of each part:

| Status | Meaning |
|---|---|
| `Converted` | Faithful PDF, present in the merged document |
| `PassThrough` | Already a PDF, taken as is |
| `PassThroughUnverified` | **Delivered alongside, ABSENT from the merged file** |
| `Substituted` | **Not converted**: a page carrying the reason replaces it |
| `Failed` | **Nothing reached the ECM** for this part |
| `Excluded` | Deliberately dropped: extension filter or signature image |

The three statuses in bold mean the ECM does not have the document.

If every office document fails, LibreOffice is missing or misconfigured. Test
it from the interface, **Test** button.

### The disk fills up

The working directory is only cleaned up once delivery is confirmed. A
build-up therefore points to failed deliveries.

The **Maintenance** button triggers an immediate pass: it quarantines
unfinished units older than seven days and removes orphan directories.

If the volume does not drop, look at `_quarantaine`: units are kept there for
thirty days, with the original message. That is intended, but it takes space.

### The service does not start

The start-up log says everything:

```
findstr /i "worker(s) demarre" logs\ACPollerForMail-*.log
```

The line `N worker(s) demarre(s), M ecarte(s)` gives the count. If `M` is not
zero, the reasons are just above, at `Error` level.

A configuration set aside does NOT stop the others from running: that is
deliberate. It appears in the interface with its issues in red.

### The interface says the token is rejected

The token in `%APPDATA%\ACPollerForMail\ui-settings.json` does not match the service
one. Open **Connection**, then **Test connection**: the message tells an
unreachable service apart from a rejected token.

The token in clear text sits in
`C:\ProgramData\ACPollerForMail\ui-settings.default.json`, left there by the
installer.

---

## 4. Replaying a message

The original message is kept in the working directory of that message, under
the name `message.eml`, and in quarantine for abandoned units.

1. Copy the `.eml` into a folder, say `D:\replay\`
2. Create a configuration using the **folder** protocol pointing at it
3. Give it the same targets as the original configuration, or test targets
4. Start it

The message is replayed identically, without touching the mailbox or the
network. This is also how a customer incident is reproduced on a development
machine.

**Careful**: the processed-message log applies in folder mode too. To replay
the same message twice, either change the configuration name or purge the log.

---

## 5. Reading the information file

A file accompanies every PDF, in the format declared in the configuration.

What to look at first:

- **`CorrelationId`**: unique identifier of the processing, present in every
  log line. It is the key to reconstructing what happened.
- **`FailedCount`**: above zero, parts are missing.
- **`Warnings`**: the reason for each failure, in plain words.
- **`Items`**: one entry per part, with its status.
- **`Fields`**: the custom fields declared in the configuration.

To trace the full processing of a message:

```
findstr /i "<CorrelationId>" logs\ACPollerForMail-*.log
```

---

## 6. Routine tasks

### Suspending a mailbox

Clear **Configuration active** in the General tab, save. The worker stops, the
settings are kept.

### Changing a password

Protocol tab, enter the new value, save. The secret is encrypted by the
service on first access.

**Leaving the field empty keeps the current value.** The interface never
receives secrets, so it cannot send them back.

### Adding a mailbox to an existing fleet

Use a **template**: the new configuration then comes down to its name, the
template and the address. The Templates tab shows the existing ones and how
many mailboxes inherit from each.

### Changing a service setting

**Service** button. These settings are not reloadable: after saving, an amber
banner appears and a notification shows in the system tray. Restart with:

```
Restart-Service ACPollerForMail
```

### Changing the log level without a rebuild

```
setx ACPollerForMail_LOGLEVEL Debug /M
Restart-Service ACPollerForMail
```

The variable avoids editing `nlog.config`, but the service still has to
restart to read it.

---

## 7. What not to do

**Do not delete `processed.db`** to "start clean". Everything still in the
mailboxes will be processed again.

**Do not edit `appsettings.json` by hand while the service runs.** The
interface detects concurrent changes and refuses to overwrite, but the reverse
is not true: a manual edit during a save from the interface would be lost.

**Do not delete the `appsettings.json.*.bak` files without reading them.**
They are the automatic backups taken before each change, and the only way
back. The **`.clear.*.bak` files, however, hold the secrets IN CLEAR TEXT** and
must be deleted once encryption is verified.

**Do not raise the extraction bounds without reason.** They protect against a
crafted archive, and processed files arrive by mail, therefore from anyone.

---

## 8. Resuming after a long outage

After several days down, the mailboxes hold the backlog.

1. Check disk space before starting
2. Temporarily lower the per-cycle message cap to spread the load
3. Watch Graph throttling on the dashboard during the first hour
4. Restore nominal values once the backlog is cleared

The random startup delay already spreads the first cycle, but on a large fleet
the catch-up remains the most exposed moment.
