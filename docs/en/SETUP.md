# ACPollerForMail — Installation guide

## Prerequisites

| Item | Requirement |
|---|---|
| Operating system | Windows Server 2016 or later |
| .NET runtime | None: the build is self-contained |
| LibreOffice | Optional, but without it no office document is converted |
| qpdf | Optional, repairs PDFs the reader engine refuses |
| Disk space | 500 MB for the product, plus the working volume |

Windows Server 2012 R2 is not supported: .NET 10 does not run on it.

---

## The service account

**This is the installation decision with the heaviest consequences.**

The Local System account has NO network identity. A service running under it
can neither reach a UNC share nor authenticate against an internal FTP server.
The error only surfaces at the first delivery, hours after installation.

Plan for a dedicated domain account, with:

- **Log on as a service** in the local policy
- **Modify** on the ECM delivery folder
- **Read** on the certificate private key, if Graph authentication uses a
  certificate

The account does not need to be a server administrator.

---

## Installation

1. Run `ACPollerForMail-x.y.z-setup.exe` as administrator
2. Accept the licence, pick the components
3. **Enter the service account** on the dedicated page
4. Tick LibreOffice and qpdf if they are not already installed

The installer creates the directories under `ProgramData`, generates the
control token, sets the NTFS permissions and registers the service as delayed
automatic start with automatic restart after failure.

Installing LibreOffice silently takes several minutes. The progress page looks
frozen during that time, which is normal.

---

## First configuration

The service is installed but does nothing: no mailbox is declared yet.

1. Launch **ACPollerForMail** from the Start menu
2. The interface connects on its own, the token having been left for it
3. **New** button, fill in the source and at least one target
4. **Test** button before saving
5. Tick **Configuration active**

### The floor date

**Set it at go-live** if history is not being imported. Without it, the first
run processes everything sitting in the mailbox, which can amount to thousands
of messages.

General tab, *Never process messages older than*.

### Across several mailboxes

Create a **template** first, carrying everything that is common: tenant,
credentials, targets, output mode. Each mailbox then comes down to its name,
the template and its address.

The day the client secret changes, one edit is enough.

---

## After the first start

Secrets entered in clear text are encrypted on first access. The service keeps
a copy of the original file as `appsettings.json.clear.*.bak`.

**Those files hold the secrets IN CLEAR TEXT.** Check that encryption took
place, then delete them:

```
findstr /i "ENC:" "C:\Program Files\ACPollerForMail\appsettings.json"
del "C:\Program Files\ACPollerForMail\appsettings.json.clear.*.bak"
```

---

## Upgrading

A new version's installer:

- stops the service, replaces the binaries, restarts it
- **keeps** `appsettings.json` and drops the new reference version as
  `appsettings.json.nouveau`
- **keeps** the processed-message log

Compare `appsettings.json.nouveau` with the configuration in place to pick up
the settings the new version added.

---

## Uninstalling

The installer deliberately keeps:

- `C:\Program Files\ACPollerForMail\appsettings.json`, holding the secrets and the
  configuration
- `C:\ProgramData\ACPollerForMail`, holding the processed-message log

**Deleting the processed-message log causes the entire history to be processed
again at the next installation**, hence duplicates in the ECM.

LibreOffice is not uninstalled: it may have been installed before ACPollerForMail or
serve something else on that server.

---

## Go-live checklist

To be run on the customer server before declaring the service operational.

| Check | How | Expected |
|---|---|---|
| Service started | `Get-Service ACPollerForMail` | `Running` |
| Active workers | Start-up log | `N started, 0 set aside` |
| Source reachable | **Test** button | Every component green |
| Targets reachable | **Test** button | Actual write confirmed |
| LibreOffice | Start-up log | Path resolved, no warning |
| qpdf | Start-up log | Path resolved |
| Account network access | Test a UNC target | Delivery succeeds |
| First message | Drop a test message | PDF and information file in the ECM |
| Idempotence | Wait for a second cycle | The message is not processed again |
| Restart | `Restart-Service ACPollerForMail` | Resumes without duplicates |

The **account network access** row is the one that fails most often, and it
only fails against a real share: a local test does not cover it.
