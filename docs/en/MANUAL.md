# ACPoller 2.0 — Manual

## 1. What the product does

ACPoller watches mailboxes, converts everything that arrives into PDF, and
delivers the result to an ECM system or a file share.

The cycle, for each message:

1. **Collection** over IMAP, Microsoft Graph, or by reading a folder
2. **Parsing** of the message into a tree: body, attachments, archives,
   embedded messages
3. **Conversion** of every part into PDF
4. **Assembly** into a single document, with one bookmark per part
5. **Information file** describing the message and its contents
6. **Delivery** to one or more targets
7. **Marking** the message as processed

The order of those last two steps is not negotiable: marking only happens once
delivery is confirmed. A duplicate can be spotted and corrected, a silent loss
cannot.

### What it does not do

It does not read document contents: no character recognition, no data
extraction. Those belong to business plugins, covered in chapter 9.

It does not send mail, except acknowledgements when a plugin asks for them.

---

## 2. Concepts worth knowing

### Configuration

A **configuration** describes one mailbox to watch and what to do with its
contents: where to collect from, how to convert, where to deliver. Each
configuration runs its own *worker*, independently of the others.

A faulty configuration is **set aside**: the others keep running. This is
deliberate, and it is what allows a fleet of a hundred mailboxes to be
operated without one typing mistake bringing everything down.

### Template

A **template** is a model configuration that others inherit from. Across a
fleet sharing the same Microsoft 365 tenant, the same credentials and the same
targets, each mailbox comes down to three lines: its name, the template, its
address.

The day the client secret changes, one edit is enough.

A configuration may override any inherited value. One rule is worth
remembering: **lists are replaced, never merged**. Redeclaring targets in a
configuration replaces the template's entirely.

### Idempotence

The service keeps a log of messages already processed, in a local database. A
message already delivered is never processed again, even after a restart, even
if the message is still sitting in the mailbox.

This log is **the most critical file in the product**. Losing it causes
everything still in the mailboxes to be processed again, hence duplicates in
the ECM.

### Working unit

Each message being processed occupies a working directory holding the original
message, the extracted parts and the produced PDFs. It is only cleaned up once
delivery is confirmed.

A build-up in that directory therefore points to failed deliveries, not to a
cleanup defect.

### Part status

The information file carries a status per part. Telling them apart matters:
three of them mean the ECM does not have the document.

| Status | Meaning |
|---|---|
| `Converted` | Faithful PDF produced, present in the merged document |
| `PassThrough` | Already a PDF, taken as is or repaired |
| `PassThroughUnverified` | **Delivered alongside, ABSENT from the merged document** |
| `Substituted` | **Not converted**: a page carrying the reason replaces it |
| `Failed` | **Nothing reaches the ECM** for this part |
| `Excluded` | Deliberately dropped: filter or size threshold |
| `Container` | Archive or embedded message: only its children count |

---

## 3. Architecture

The product is made of two distinct applications.

**The Windows service** does all the work. It runs without an open session,
starts automatically, and restarts by itself after a failure.

**The monitoring interface** does nothing by itself: it talks to the service
over a local API. It can be closed without consequence, and installed on a
separate workstation.

That separation has a practical consequence: the interface never writes
directly into the configuration files. The service stays the sole owner of
what it reads and when it re-reads it. An interface writing the file while a
worker reads it would produce an inconsistent state, with no error.

### External components

| Component | Role | Without it |
|---|---|---|
| LibreOffice | Converting office documents | Word, Excel and PowerPoint come out as substitution pages |
| qpdf | Repairing unreadable PDFs | A few percent of invoices are not merged |

Both are called out of process, never linked into the product. Their absence
degrades the service, it does not stop it.
---

## 4. Installation

### Prerequisites

| Item | Requirement |
|---|---|
| Operating system | Windows Server 2016 or later |
| .NET runtime | None: the build is self-contained |
| Disk space | 500 MB, plus the working volume |
| Service account | Dedicated domain account, see below |

Windows Server 2012 R2 is not supported.

### The service account

**This is the installation decision with the heaviest consequences.**

The Local System account has NO network identity. A service running under it
can neither reach a UNC share nor authenticate against an internal FTP server.
The error only surfaces at the first delivery, hours after installation.

The dedicated domain account needs:

- **Log on as a service**, in the local policy
- **Modify** on the ECM delivery folder
- **Read** on the certificate private key, if Graph authentication uses a
  certificate

It does not need to be a server administrator.

### Running the installer

1. Run `ACPoller-x.y.z-setup.exe` as administrator
2. Pick the components: LibreOffice and qpdf if the server lacks them
3. **Enter the service account** on the dedicated page
4. Let the wizard finish

The installer creates the directories, generates the control token, sets the
NTFS permissions, and registers the service as delayed automatic start with
automatic restart after failure.

Installing LibreOffice silently takes several minutes. The progress page looks
frozen during that time.

### After the first start

Secrets entered in clear text are encrypted on first access, and the service
keeps a copy of the original file.

**Those copies hold the secrets IN CLEAR TEXT.** Check that encryption took
place, then delete them:

```
findstr /i "ENC:" "C:\Program Files\ACPoller\appsettings.json"
del "C:\Program Files\ACPoller\appsettings.json.clear.*.bak"
```

The service purges them by itself after twenty-four hours, but there is no
reason to wait.

---

## 5. The interface

### The status bar

The first thing to look at. A green light and the number of active workers
mean the service is running. A red light tells two cases apart by its message:
service unreachable, or token rejected.

### The two lists on the left

**Configurations** lists the watched mailboxes, with their protocol and the
number of issues found. A blocking issue sets the configuration aside: it
shows in red and its worker does not run.

**Templates** lists the models, with the number of configurations inheriting
from each. That number is the **reach** of a change: seeing it before opening
a template avoids editing fifty mailboxes while believing you are editing one.

### The two tabs on the right

**Monitoring** shows the details of the selected configuration: its issues,
the result of the last test, the state of every worker.

**Dashboard** shows activity over the last twenty-four hours.

### The dashboard

Four indicators are enough to know whether things are fine.

| Indicator | What it answers |
|---|---|
| Success rate | Is it running normally? |
| 95th percentile duration | Are slow messages saturating the workers? |
| Graph throttling | Am I close to the tenant quota? |
| Free disk | How long before it stops? |

The per-configuration breakdown at the bottom sorts mailboxes by failure
count: the first row names the one causing trouble.

The 95th percentile deserves a word. On processing durations, the average
hides the slow cases, and those are the ones holding concurrent slots while
other mailboxes wait. A wide gap between the average and that percentile
points to a handful of messages monopolising the service.

### The buttons

| Button | Effect |
|---|---|
| New, Edit, Delete | Act on the selected configuration |
| Test | Checks the source and every target, without processing a message |
| Restart | Restarts that configuration's worker, leaving the others alone |
| Refresh | Forces an update, also available on F5 |
| Maintenance | Triggers an immediate cleanup pass |
| Service | Server settings, which require a restart |
| Connection | Address and token of the interface itself |
| Help | Token, status and troubleshooting reference, also on F1 |

**Test** is the button to use before any save. It actually writes to file
targets, checking permissions and not merely the existence of the path.
---

## 6. Configuring a mailbox

Each tab of the editor covers one stage of processing.

### General tab

**Name**: unique identifier, used as the key in logs and as the working
directory name. Changing it amounts to creating a new configuration, and the
processed-message log starts from scratch.

**Template**: the model to inherit from. Empty for a standalone configuration.

**Interval between cycles**: delay between the end of one cycle and the start
of the next. Too short a value across a large fleet multiplies calls without
gaining anything, collection already being bounded by global concurrency.

**Random startup delay**: without it, every mailbox hits the server on the
same second when the service starts, triggering throttling before the first
message.

**First-cycle lookback**: applies to the very first cycle only, when nothing
has been processed yet for this mailbox. Too low a value makes it look like
nothing is arriving while the service works fine, and nothing reports it.

**Floor date**: no older message will ever be processed. Unlike the lookback,
it does not slide. **Set it at go-live** when history is not being imported:
without it, the first run processes everything sitting in the mailbox.

### Protocol tab

Choosing the protocol reveals the matching fields.

**graph** for Microsoft 365. **Certificate** authentication is preferable to a
client secret, which always expires on a Friday evening.

**imap** for a classic mail server. Port 993 means implicit SSL, port 143
means STARTTLS.

**folder** to read `.eml` files from disk. This mode serves to replay a
problematic message and to run acceptance tests, without a mailbox or a
network.

**Watched folder**: designated by its canonical name, not its label. On a
French mailbox the label is *Boîte de réception*, and any lookup by label
would fail.

**After processing**: applied once delivery is confirmed. `None` leaves the
message untouched, which allows unlimited replays during acceptance testing.
In production, `MarkAsRead` or `Move` avoid listing the same messages forever.

**Throttling**, Graph only. These bounds are **shared by every mailbox of the
same tenant**. Spacing matters as much as concurrency: without it, eight calls
leave on the same millisecond and trigger throttling before reaching the
limit.

### Conversion tab

**Timeout per part**: once exceeded, the converter is **killed** rather than
waited for. A LibreOffice instance stuck on a corrupt document never recovers.

**Unconvertible part**: three policies.

| Policy | Effect |
|---|---|
| `Substitute` | A page carrying the name and the reason replaces the part |
| `Skip` | The part is dropped, leaving no trace in the PDF |
| `RejectMessage` | The whole message is rejected, no partial output |

`Substitute` is the recommended default: the ECM sees something is missing,
which triggers a supplier follow-up. With `Skip`, the gap goes unnoticed.

**Inline image threshold**: below it, an image referenced by the HTML body is
dropped. Without this filter, every message produces three pages of signature
logos.

**Extraction bounds**: depth, part count, expanded volume, single entry size.
They protect against a crafted archive, and processed files arrive by mail,
therefore from anyone. Raising them without reason exposes the service.

**Excluded extensions**: used to drop electronic signatures and calendar
invitations, which have no business reaching the ECM.

### Output format tab

**PDF production mode**:

| Mode | Result |
|---|---|
| `Merged` | One PDF per message, body then parts, with bookmarks |
| `PerAttachment` | One PDF per part |
| `Both` | Both of the above |

**Information file format**: `json`, `xml`, `csv` or `xslt`.

`xslt` applies a transformation sheet to the canonical XML. This is what makes
it possible to produce the exact structure an ECM expects, including flat
text, without shipping code. To develop a sheet: set the format to `xml`, look
at the actual output, write the sheet against it, then switch back to `xslt`.

**Naming template**: see appendix A for the token list. In `PerAttachment`
mode the `{index}` token is **mandatory**, otherwise parts overwrite each
other.

**Atomic write**: the file is written under a temporary extension, then
renamed. Without it, an ECM watching the folder can pick up a PDF mid-write,
and the error is undetectable on its side.

**All or nothing across targets**: if one target fails, deliveries already
made to the others are rolled back. An ECM holding a message that another one
never received creates a gap nobody notices before the audit.

**Sentinel file**: written last, it tells the ECM the batch is complete.
Without it, the ECM may take the PDF without its information file, or the
other way round.

### Processors tab

The list of business processors provided by the installed plugins, with the
pipeline stages at which they run.

A processor framed in red is declared in the configuration but its plugin is
no longer loaded: it will not run, and the service will set the configuration
aside at the next start.

Rejected plugins appear below with their reason. Without that list, one would
hunt for a processor that does exist in the folder but whose plugin was never
loaded.

### Targets tab

A configuration may deliver to several targets. Each target carries a **name**
that appears in the logs: without distinct names, a failure on one of three
targets is unreadable.

The fields offered depend on the transport, but the table at the bottom shows
**every** setting, including those no field covers. That is what makes it
possible to configure a customer export DLL.

### Fields tab

Fields added to the information file. **Order matters**: it is the CSV column
order, and changing it can break an integration already in place.

See appendix B for the available sources and the resolution order.
---

## 7. Delivery targets

### File system

The most common transport: a local folder or a UNC share.

The service account needs **modify** permission on that folder. A UNC share
further requires the service to run under a domain account, the Local System
account having no network identity.

The **Test** button actually writes a witness file, then deletes it: it checks
permissions, not merely the existence of the path.

### FTP and FTPS

Explicit encryption on port 21 is the default, implicit uses port 990.

**Accept any certificate** removes every protection against interception: a
third party can sit between the service and the server undetected. Enable only
against a known self-signed certificate, and document it. The service repeats
the warning in its log on every session.

### S3 and compatible stores

For AWS, the region is enough. For MinIO, Garage or Ceph, fill in the **custom
endpoint**: path style and the compatible checksum mode are then enabled
automatically. Without them, those implementations reject deliveries with an
error that does not name the cause.

**Server-side encryption** must stay empty if the store does not configure it:
declaring it would make every delivery fail.

Keys may be left empty to use the usual AWS resolution chain, environment
variables or instance role.

---

## 8. Custom fields and transformation

### Why

No fixed format suits every ECM: one wants a hard-coded company code, another
the current date in its own format, a third a header set by its relay.
Freezing these in code would mean one release per customer.

### The five sources

| Source | Value |
|---|---|
| `Fixed` | Literal, identical for every message |
| `Token` | Token template, resolved like file naming |
| `Header` | MIME header, name case-insensitive |
| `Property` | Value left behind by a business processor |
| `Environment` | Server environment variable |

### Resolution order

Source value, then date format, then translation table, then default value if
the result is empty, then fixed length.

The **translation table** turns a label into the code the ECM expects. A value
missing from the table passes through unchanged.

**Fixed length** serves ECMs reading positional formats. A value that is too
long is truncated: overflowing would shift every following column, which is
worse than losing the end of a label.

An unresolved field **never fails the processing**. It produces its default
value and a warning. The document is what matters, not the completeness of its
index.

### XSLT transformation

The service always produces the same canonical XML. A customer-specific style
sheet turns it into whatever their ECM expects.

The output need not be XML: a sheet declaring `xsl:output method="text"`
produces flat, CSV or fixed-width output.

For safety, **scripts and the `document()` function are disabled**. A sheet is
a configuration file, and allowing code execution from a configuration file
would amount to granting the service account's rights to anyone able to write
in that folder.

---

## 9. Business processors

A plugin is a DLL dropped into the plugin folder. The service reads it by
metadata without executing its code, checks compatibility, then loads it into
an isolated context.

A plugin may provide:

- **processors** running at various pipeline stages
- **delivery targets** specific to the customer
- **reference data providers** consulted by processors

### Stages

| Stage | When |
|---|---|
| `AfterParse` | After the message is parsed, before conversion |
| `BeforeConvert` | Before each part conversion |
| `AfterConvert` | After conversion, before assembly |
| `BeforeExport` | Before delivery, last chance to reject |
| `AfterExport` | After delivery is confirmed |

A processor may **reject** a message or **skip** it. Rejection reports an
anomaly, skipping marks a message that does not concern this flow. Both appear
separately on the dashboard.

A processor leaves its results in a property bag, which custom fields of
source `Property` can pick up. That is how a supplier code extracted from the
subject line reaches the information file.

### Contract version

Every plugin declares the contract version it expects. A plugin that is too
old is **rejected and not loaded**, with its reason visible in the interface.
Loading it would produce runtime errors, far harder to diagnose than an
explicit refusal at start-up.

---

## 10. Service settings

The **Service** button. These settings are **not reloadable while running**:
any change requires a full restart, during which capture is interrupted.

After saving, an amber banner appears and a notification shows in the system
tray. The details list the pending settings and the reason for each.

### What they cover

| Tab | Settings |
|---|---|
| Service | Working directory, global concurrency, plugins, retention |
| Tools | LibreOffice, profiles, qpdf, fonts |
| Maintenance | Interval, quarantine, disk threshold |
| Control | API enablement and port, token, metrics database |

### Three traps

**Changing the working directory loses the idempotence log.** Messages still
in the mailboxes will be processed again.

**Raising global concurrency beyond the LibreOffice profile count achieves
nothing**: conversion becomes the bottleneck.

**Changing the token invalidates every operator workstation**, including the
one it was changed from.

### Restarting

The interface does not restart the service itself: it talks to it over its
API, and a service shutting down on its own client's order could not confirm
the order was carried out.

```
Restart-Service ACPoller
```
---

## 11. Day-to-day operation

### Checking that things are fine

Open the interface, Dashboard tab. If the success rate is 100 %, no Graph
throttling shows and the disk has room, there is nothing to do.

Without the interface:

```
curl -H "X-ACPoller-Token: <token>" http://127.0.0.1:5199/api/status
```

### Suspending a mailbox

Clear **Configuration active**, save. The worker stops, the settings are kept.

### Changing a password

Protocol tab, enter the new value, save. The secret is encrypted by the
service on first access.

**Leaving the field empty keeps the current value.** The interface never
receives secrets, so it cannot send them back.

### Adding a mailbox to an existing fleet

Use a template. The new configuration comes down to its name, the template and
the address.

### Replaying a message

The original message is kept in its working directory under the name
`message.eml`, and in quarantine for abandoned units.

1. Copy the `.eml` into a folder
2. Create a configuration using the **folder** protocol pointing at it
3. Give it test targets
4. Start it

The message is replayed identically, without touching the mailbox or the
network.

Note: the processed-message log applies in folder mode too. To replay the same
message twice, change the configuration name.

### Resuming after a long outage

After several days down, the mailboxes hold the backlog.

1. Check disk space before starting
2. Temporarily lower the per-cycle message cap
3. Watch Graph throttling during the first hour
4. Restore nominal values once the backlog is cleared

---

## 12. Troubleshooting

### A message comes back every cycle

First check that **delivery succeeds**: marking only happens once delivery is
confirmed.

If delivery succeeds, look at the disposition: with `None` the message stays
unread and will be listed again every cycle.

### The merged PDF is empty or incomplete

Open the information file and look at the status of each part. Three statuses
mean the ECM does not have the document: `PassThroughUnverified`,
`Substituted` and `Failed`.

If every office document fails, LibreOffice is missing or misconfigured.

### Nothing is collected, with no error

The service runs and finds nothing. Look, in this order:

1. **First-cycle lookback**: a low value excludes older messages
2. **Unread only**: messages opened in the webmail are marked as read
3. **Floor date**: later than the messages present
4. **Watched folder**: messages may sit in a subfolder

Switching the log level to `Debug` makes the cycle verbose:

```
setx ACPOLLER_LOGLEVEL Debug /M
Restart-Service ACPoller
```

### The disk fills up

The working directory is only cleaned up once delivery is confirmed. A
build-up points to failed deliveries.

The **Maintenance** button triggers an immediate pass: quarantine of unfinished
units older than seven days, removal of orphan directories.

### The service does not start

```
findstr /i "worker(s) demarre" logs\acpoller-*.log
```

The line `N worker(s) demarre(s), M ecarte(s)` gives the count. If `M` is not
zero, the reasons are just above, at `Error` level.

### The interface says the token is rejected

The interface token does not match the service one. Open **Connection**, then
**Test connection**: the message tells an unreachable service apart from a
rejected token.

The token in clear text sits in
`C:\ProgramData\ACPoller\ui-settings.default.json`.

### Tracing one message

The information file carries a `CorrelationId`, present in every log line of
that processing:

```
findstr /i "<CorrelationId>" logs\acpoller-*.log
```

---

## 13. What not to do

**Do not delete the processed-message log** to start clean. Everything still
in the mailboxes will be processed again.

**Do not edit the configuration by hand while the service runs.** The
interface detects concurrent changes and refuses to overwrite, but the reverse
is not true.

**Do not delete `.bak` backups without reading them.** They are the automatic
copies taken before each change, and the only way back. The `.clear.*.bak`
files, on the other hand, hold secrets in clear text and must be deleted once
encryption is verified.

**Do not raise the extraction bounds without reason.** They protect against a
crafted archive, and processed files arrive from anyone.

**Do not forget the floor date at go-live.** Without it, the first run
processes all the history sitting in the mailbox.
---

## Appendix A — Naming tokens

Usable in the naming template and in fields of source `Token`. A format may
follow a colon, for example `{received:yyyyMMdd}`.

| Token | Value |
|---|---|
| `{date}` | **Processing** date, in local time |
| `{time}` | Processing time |
| `{datetime}` | Processing date and time |
| `{received}` | Message **reception** date |
| `{guid}` | Random identifier, guarantees uniqueness |
| `{mailbox}` | Local part of the mailbox address |
| `{config}` | Configuration name |
| `{subject}` | Message subject, truncated and sanitised |
| `{from}` | Local part of the sender address |
| `{index}` | Part number, **mandatory** in PerAttachment mode |
| `{name}` | File name without extension |
| `{ext}` | Part extension |
| `{node}` | Full logical path |

`{date}` changes if the message is replayed the next day, `{received}` does
not. For naming that must stay reproducible after an incident, prefer
`{received}`.

Every character forbidden in a Windows file name is replaced, path separators
included: a subject containing a slash cannot create a subfolder on the
target.

---

## Appendix B — Custom field output

The same content comes out differently depending on the format.

**json**: a `fields` object, names as keys.

**xml**: a `Fields` block, the field name being an **attribute** rather than
an element name. A name typed by an operator may contain a space or start with
a digit, which would produce invalid XML as an element name.

**csv**: columns appended **after** the fixed ones, repeated on every row.
Adding a field therefore never shifts the existing columns.

**xslt**: available to the transformation sheet.

---

## Appendix C — Locations

| Path | Contents | Critical |
|---|---|---|
| `C:\Program Files\ACPoller\appsettings.json` | Configuration, encrypted secrets | Yes |
| `C:\Program Files\ACPoller\logs\` | Logs | No |
| `C:\Program Files\ACPoller\docs\` | This manual | No |
| `C:\ProgramData\ACPoller\work\` | Working units in progress | No |
| `C:\ProgramData\ACPoller\work\state\processed.db` | Processed-message log | **Critical** |
| `C:\ProgramData\ACPoller\work\_quarantaine\` | Abandoned units, 30 days | No |
| `C:\ProgramData\ACPoller\ui-settings.default.json` | Interface address and token | No |
| `%APPDATA%\ACPoller\ui-settings.json` | Operator's own settings | No |

### Backup

Two items belong in the server backup:

- `C:\ProgramData\ACPoller\work\state\processed.db`
- `C:\Program Files\ACPoller\appsettings.json`

The first is the only one whose loss is irreversible. The second can be
rebuilt, but every secret would have to be entered again.

---

## Appendix D — Control API

Listens on the loopback interface only. Every request carries the
`X-ACPoller-Token` header.

| Method | Route | Effect |
|---|---|---|
| GET | `/api/status` | Service and worker state |
| GET | `/api/dashboard` | Indicators over the last 24 hours |
| GET | `/api/configurations` | Configurations and their issues |
| GET | `/api/configurations/{name}/raw` | One configuration as JSON, secrets masked |
| PUT | `/api/configurations/{name}` | Creates or replaces a configuration |
| DELETE | `/api/configurations/{name}` | Deletes a configuration |
| POST | `/api/configurations/{name}/test` | Tests source and targets |
| POST | `/api/workers/{name}/restart` | Restarts one worker |
| GET | `/api/templates` | Templates and their reach |
| GET | `/api/processors` | Available processors, rejected plugins |
| GET | `/api/settings` | Service settings, secrets masked |
| PUT | `/api/settings` | Changes service settings |
| POST | `/api/maintenance/run` | Triggers a maintenance pass |

Secrets **never** leave the API: they are replaced by an empty string. On
write, an empty field means unchanged.

Every read returns a **fingerprint** of the configuration file, which writes
demand back. If the file changed in between, the change is refused rather than
overwriting someone else's.

---

## Appendix E — Go-live checklist

To be run on the customer server before declaring the service operational.

| Check | How | Expected |
|---|---|---|
| Service started | `Get-Service ACPoller` | `Running` |
| Active workers | Start-up log | `N started, 0 set aside` |
| Source reachable | **Test** button | Every component green |
| Targets reachable | **Test** button | Actual write confirmed |
| LibreOffice | Start-up log | Path resolved |
| qpdf | Start-up log | Path resolved |
| Account network access | Test a UNC target | Delivery succeeds |
| First message | Drop a test message | PDF and information file delivered |
| Idempotence | Wait for a second cycle | The message is not processed again |
| Restart | `Restart-Service ACPoller` | Resumes without duplicates |
| Floor date | General tab | Set if history is not imported |
| Encrypted secrets | `findstr ENC: appsettings.json` | At least one match |
| Clear-text backups | `dir *.clear.*.bak` | Deleted |

The **account network access** row is the one that fails most often, and it
only fails against a real share: a local test does not cover it.
