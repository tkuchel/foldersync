# Bidirectional Sync Design

## Goal

Add an explicit bidirectional sync capability without weakening the current one-way safety model.

The current product is built around a simple rule:

- source is authoritative
- destination is derived
- reconciliation is directional
- deletions are controlled from one side

That simplicity is one of the reasons the current implementation is understandable and safe. Bidirectional sync should therefore be introduced as a new engine and profile mode, not as a small extension of the current one-way behavior.

## Recommendation

Introduce bidirectional sync in phases:

1. `OneWay`
   Keep the current behavior exactly as-is.
2. `TwoWayPreview`
   Detect and classify changes on both sides, but do not automatically apply destructive conflict resolution.
3. `TwoWaySafe`
   Apply non-destructive creates and updates in both directions, but never auto-propagate deletes.
4. `TwoWay`
   Optional future mode with configurable delete propagation and stricter conflict policy.

This staged model gives us a way to ship useful bidirectional support without jumping straight into risky auto-merge behavior.

## Proposed Config Shape

Add a sync mode at the profile level.

```json
{
  "FolderSync": {
    "Profiles": [
      {
        "Name": "workspace",
        "SourcePath": "C:\\Left",
        "DestinationPath": "D:\\Right",
        "SyncMode": "TwoWayPreview",
        "TwoWay": {
          "PropagateDeletes": false,
          "ConflictMode": "Manual",
          "RequireHashComparison": true,
          "StateStorePath": "",
          "PreferRenameDetection": true
        }
      }
    ]
  }
}
```

Suggested new enums:

- `SyncMode`
  - `OneWay`
  - `TwoWayPreview`
  - `TwoWaySafe`
  - `TwoWay`
- `TwoWayConflictMode`
  - `Manual`
  - `KeepNewest`
  - `KeepBoth`
  - `PreferLeft`
  - `PreferRight`

Notes:

- `OneWay` should remain the default.
- `TwoWayPreview` should be the recommended first bidirectional mode.
- `RequireHashComparison` should effectively be mandatory for two-way profiles.
- `StateStorePath` should default inside the install directory, not inside either synced tree.

## Why The Current Engine Should Not Be Reused Directly

Today the core model assumes:

- a watcher event always originates from the source
- conflict resolution is destination-focused
- reconciliation can use robocopy as a one-sided mirror
- delete/archive logic is source-driven

Bidirectional sync breaks all of those assumptions.

If we tried to bolt two-way logic into the current pipeline, we would likely end up with:

- confusing branching throughout the processor
- unsafe delete behavior
- duplicated event loops
- hard-to-reason-about reconciliation
- fragile conflict handling

A cleaner approach is to preserve the current one-way pipeline and add a separate bidirectional profile pipeline alongside it.

## Core Design

### 1. Treat both sides symmetrically

For two-way profiles, stop thinking in terms of source and destination behavior.

Internally model them as:

- `LeftRoot`
- `RightRoot`

The config can still use `SourcePath` and `DestinationPath` for compatibility, but the two-way engine should treat them as peers.

### 2. Add a persistent state store

Bidirectional sync needs memory. Without it, the system cannot distinguish:

- a new file from a renamed file
- a delete from a move
- “left changed” from “both changed independently”
- whether a conflict is new or already acknowledged

Proposed persisted state:

- per-profile state file or lightweight database
- one row/document per relative path
- fingerprints for both sides
- last observed timestamps
- last applied operation
- conflict status
- rename correlation hints

Suggested model:

```text
ProfileState
  RelativePath
  LeftFingerprint
  RightFingerprint
  LastSeenUtc
  LastResolvedUtc
  LastResolution
  ConflictState
```

For scale and future flexibility, SQLite is the best long-term choice. A JSON store would be simpler initially, but I would still recommend SQLite for this feature.

## Change Classification

Every reconciliation cycle or watcher event should reduce to one of these cases:

- left only exists
- right only exists
- both exist and are equal
- left changed, right unchanged
- right changed, left unchanged
- both changed from the last known common state
- delete on one side, unchanged on the other
- delete on one side, modified on the other
- rename suspected

That gives us a decision table instead of ad hoc branching.

## Conflict Rules

### Safe first version

In `TwoWayPreview` and `TwoWaySafe`:

- create missing files on the opposite side
- propagate straightforward updates when only one side changed
- never auto-delete by default
- when both sides changed, mark conflict and do not overwrite either side
- record conflict in runtime health and dashboard
- notify through tray/dashboard/toasts if configured

### Conflict actions

For a conflicting file, support:

- leave both untouched and alert
- keep newest
- keep both by duplicating one side with a suffix
- prefer left
- prefer right

Recommended default: `Manual`

That keeps the product honest and reduces the chance of silent data loss.

## Deletes

Deletes are the riskiest part of two-way sync.

Recommended policy by mode:

- `TwoWayPreview`
  - never propagate deletes
  - show delete candidates as warnings
- `TwoWaySafe`
  - optionally archive delete candidates, but do not permanently remove automatically
- `TwoWay`
  - allow delete propagation only when explicitly enabled

If delete propagation is enabled:

- require archive support on both sides where possible
- record tombstones in the state store
- treat delete-vs-modify as a conflict, not an automatic delete

## Reconciliation

One-way reconciliation currently leans on robocopy. That is not appropriate as the main bidirectional reconciliation engine.

For two-way profiles, reconciliation should:

1. enumerate both trees
2. normalize relative paths
3. fingerprint candidates
4. compare against the state store
5. classify changes
6. produce an operation plan
7. execute safe operations
8. persist the updated state

This means:

- robocopy can remain for one-way profiles
- two-way profiles need a managed reconciliation implementation inside FolderSync

## Watchers

Current one-way watchers are source-driven. For bidirectional profiles we need:

- one watcher per side
- event origin tagging
- coalescing of near-simultaneous left/right changes
- reparse-point safety on both sides
- debounce at the relative-path level, not just per raw event stream

I would recommend:

- a shared bidirectional event buffer per profile
- each event tagged with `Left` or `Right`
- the processor always resolving work via state + current filesystem, not trusting raw watcher order alone

## New Runtime and Dashboard Concepts

Bidirectional mode should surface more state to operators.

New status concepts:

- sync mode per profile
- left/right root summary
- pending conflicts
- last conflict
- delete candidates
- last bidirectional reconcile result
- state store health

Dashboard opportunities:

- show conflict count prominently
- add a conflict review panel
- allow operator actions:
  - accept left
  - accept right
  - keep both
  - clear acknowledged preview conflicts

Tray opportunities:

- alert when new conflicts appear
- provide quick action to open dashboard filtered to conflicted profile

## Validation Rules

For two-way profiles, add new validation:

- `SourcePath` and `DestinationPath` must both exist
- paths must not overlap
- delete propagation cannot be enabled without archive/tombstone support
- hash comparison must be enabled
- robocopy-based reconciliation must be disabled
- conflicting one-way-only options should be rejected

Examples of settings to reject or override in two-way mode:

- one-way `DeleteMode` semantics without two-way delete policy
- one-way-only reconciliation assumptions
- profile setups where one path is nested inside the other

## Proposed Implementation Phases

### Phase 1: Design and data model

- add `SyncMode` and `TwoWayOptions`
- add validation rules
- add state store abstraction
- add models for bidirectional change classification and conflict records

### Phase 2: `TwoWayPreview`

- enumerate both sides
- detect differences
- write conflict/change records
- expose status in dashboard/tray
- do not automatically apply destructive changes

This phase gives operators visibility and confidence before automation.

### Phase 3: `TwoWaySafe`

- auto-apply unambiguous creates and one-sided updates
- do not auto-delete
- surface conflicts for manual review
- persist state after each resolution

### Phase 4: Operator conflict resolution

- dashboard conflict view
- accept-left / accept-right / keep-both actions
- notifications on new conflicts

### Phase 5: Optional full `TwoWay`

- explicit delete propagation
- tombstones
- stronger rename detection
- more advanced policy controls

## Suggested Codebase Shape

Add a parallel two-way slice instead of reshaping the one-way path in place.

Suggested new areas:

- `Models/TwoWayOptions.cs`
- `Models/TwoWayConflictRecord.cs`
- `Models/TwoWayStateEntry.cs`
- `Services/TwoWayStateStore.cs`
- `Services/TwoWayReconciliationService.cs`
- `Services/TwoWaySyncProcessor.cs`
- `Services/TwoWayWatcherCoordinator.cs`

And then update:

- `FolderSyncService`
  - choose one-way or two-way pipeline per profile
- `ProfileConfigurationValidator`
  - enforce two-way safety rules
- `DashboardCommand`
  - expose sync mode and conflict state
- tray/dashboard health surfaces
  - add conflict-centric indicators

## Non-Goals For The First Bidirectional Release

The first release should not try to solve everything.

Out of scope initially:

- perfect rename detection
- cross-device file identity
- auto-merge of document contents
- three-way text merge
- cloud-provider-specific semantics
- live hot-reload of bidirectional profile engine config

## Recommendation Summary

Bidirectional sync is worth building, but only if we treat it as a distinct engine with a conflict-first design.

The safest product path is:

1. keep `OneWay` untouched
2. add `TwoWayPreview`
3. add state tracking and conflict reporting
4. add `TwoWaySafe`
5. only later consider full automatic bidirectional delete propagation

If we follow that path, we can add a meaningful bidirectional feature without undermining the safety and clarity that make the current tool trustworthy.
