# FolderSync Troubleshooting

This guide is for common operator-facing states in `status`, the dashboard, and the tray app.

## Reconcile States

### `Queued`

Meaning:
The reconcile request was accepted and written to `foldersync-control.json`, but the running FolderSync service has not consumed it yet.

What to check:
- Confirm the Windows service is running with `foldersync status --verbose`.
- Check whether another reconcile is already running for the same profile.
- Check whether multiple profiles are active and the queue is simply draining in order.

What to expect:
- Dashboard and tray actions queue reconcile requests; they do not run a separate one-shot process anymore.
- A queued request should eventually disappear once the service consumes it.

### `Running`

Meaning:
The running service has started a reconcile for that profile and is actively processing it.

What to check:
- Look at per-profile recent activity in the dashboard for trigger, duration, and result details.
- Use `foldersync status --verbose` to inspect the latest reconcile trigger and exit description.
- If the state stays `Running` for a long time, review current logs in the install `logs` directory.

What to expect:
- While a reconcile is running, later requests for the same profile can remain queued.
- Reconciles are serialized per profile.

### `Idle`

Meaning:
There is no queued reconcile request for the profile and no reconcile is currently running.

What to check:
- If you expected a reconcile, confirm that your dashboard or tray action succeeded.
- Check whether the service was unavailable at the time you clicked the action.
- Review recent activity to confirm the last successful or failed reconcile outcome.

### `Service unavailable`

Meaning:
The dashboard or tray cannot hand reconcile work to the running service because the service is stopped or otherwise unavailable.

What to check:
- Run `foldersync status --verbose`.
- Start or restart the Windows service if needed.
- Confirm the installed executable path and config are correct.

What to expect:
- Reconcile buttons are disabled in this state because reconcile requests are service-owned.
- Pause and resume visibility may still come from the persisted control snapshot even when the service is stopped.

## Watcher Overflow

Meaning:
The file watcher saw more changes than it could safely buffer, so FolderSync recorded an overflow and requested reconciliation as a safety net.

What to check:
- Look for repeated overflow counts in `status --verbose`, dashboard profile stats, or tray profile menus.
- Review current activity and logs to see whether a reconcile was triggered afterward.
- Check whether a profile is watching a very busy tree or a path with heavy churn.

What to expect:
- One overflow is not automatically a failure.
- Repeated overflows usually mean the profile needs attention, because live events are being replaced by catch-up reconciliation more often than intended.

## Repeated Failures

Meaning:
FolderSync is repeatedly failing sync work or reconcile work for the same profile.

What to check:
- Inspect the profile’s recent activity and `LastFailure` details.
- Review logs in the install `logs` directory.
- Validate the config with `foldersync validate-config --config <path>`.
- Confirm source/destination paths still exist and remain accessible.

Common causes:
- source path missing or inaccessible
- destination path permissions changed
- files staying unstable or locked for long periods
- risky or incorrect reconciliation options

## Useful Commands

```powershell
foldersync status --verbose
foldersync status --json
foldersync health
foldersync health --json
foldersync validate-config --config <path>
```

## Persisted State Files

- `foldersync-health.json`: runtime health, counters, alerts, and recent activity
- `foldersync-control.json`: pause state and queued reconcile requests

If the dashboard or tray looks confusing, these files are often the quickest way to confirm what the service believes is happening.
