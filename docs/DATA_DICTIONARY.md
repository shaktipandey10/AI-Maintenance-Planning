# Data Dictionary

This is the single detailed data dictionary for the AI Maintenance Planner. It covers the persisted entities, their fields and relationships, the enumerations, the sample datasets, the startup seed, the import process, and the reset position.

It was written from source at HEAD `1376af5` plus uncommitted changes. **No test was run, the API was not started, Ollama was not invoked, and no database file or sidecar was opened in the stage that wrote it.** Nothing here claims that a clean setup, an import run or a reset was tested. Statements about runtime behaviour come from reading code and are labelled *derived from code* where that matters.

Related documents:
- [README.md](../README.md): setup, configuration and run commands (the data section there is a summary of this file)
- [ARCHITECTURE.md](ARCHITECTURE.md): components, workflow, state diagrams, API endpoints, transactions
- [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md): requirement status, including FR02 and FR03
- [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md): acceptance scenarios and evidence
- Requirement source: [requirementsforpoc.md](requirementsforpoc.md), section 5 and DEL03

## 1. Coverage statement

**Covered:** every persisted column of the 15 entity tables and the `SparePartAssetCompatibility` join table, as defined in `src/MaintenancePlanner.Domain/Entities/*.cs` and `MaintenancePlannerDbContext.cs`, and checked against the EF model snapshot. Also the four persisted enumerations, the import datasets and their columns, and every sample-data file under `data/`.

**Not covered:**
- The inner structure of the JSON text columns (`ResultPayload`, `CheckpointPayload`, `ClarificationPayload`, `ApprovedCandidatePayload`, `ValidationViolations`, the technician-ID arrays). Only `RequestPayload` has a verified shape (section 4.1). The result shapes are described in [ARCHITECTURE.md](ARCHITECTURE.md) section 6.
- API request and response records (see ARCHITECTURE.md).
- Frontend types (`frontend/src/types/api.ts`).
- EF's own `__EFMigrationsHistory` table.

## 2. Storage conventions

All of this is from the model snapshot (`Persistence/Migrations/MaintenancePlannerDbContextModelSnapshot.cs`) and the DbContext.

| Convention | Detail |
|---|---|
| Database | One SQLite file. The connection string is `ConnectionStrings:MaintenancePlanner`; the repository value is `Data Source=../../maintenance-planner.db`, resolved against the API content root. |
| Column types | Every string, `Guid`, `decimal` and `DateTimeOffset` column is SQLite `TEXT`. Integers (`int`, `long`) are `INTEGER`. |
| Decimals | Stored as `TEXT` (`HasColumnType("TEXT")`). Quantities and hourly rates are `decimal` in code. |
| Nullability | `Nullable` is enabled in the Domain project. A property typed `string` or a value type is `NOT NULL`; `string?`, `Guid?`, `DateTimeOffset?` and `long?` are nullable. The tables below follow the snapshot. |
| Enumerations | `PlanningJobStatus`, `DraftStatus`, `ReservationStatus` and `TaskProgressStatus` are stored as their **names** (`HasConversion<string>()`), not numbers. |
| Identifiers | Reference-data IDs are text keys that people choose (for example `P-101`). Workflow records use `Guid` keys, except `TechnicalSkill.Id` and `TechnicianAvailability.Id`, which are auto-increment integers. |
| Currency | No table has a currency column. `Technician.HourlyRate` is a bare decimal. |
| Time zones | `DateTimeOffset` values keep their offset. `MaintenanceWindow.TimeZoneId` is a separate text field that defaults to `Asia/Kolkata`. |
| Length limits | The database defines **no maximum lengths**. Length limits apply only in the import validator (section 8). |

## 3. Entity overview

| Entity (table) | Purpose | Section |
|---|---|---|
| `Asset` (`Assets`) | A piece of equipment that can be maintained. | 4.1 |
| `SparePart` (`SpareParts`) | A stocked part with on-hand and reserved quantities. | 4.1 |
| `SparePartAssetCompatibility` | Join table: which parts are approved for which assets. | 4.1 |
| `MaintenanceTaskDefinition` (`MaintenanceTaskDefinitions`) | A catalogue task for an asset: duration, skills, crew, part, predecessor, source manual. | 4.1 |
| `Technician` (`Technicians`) | A person who can be assigned, with an hourly rate. | 4.1 |
| `TechnicalSkill` (`TechnicalSkills`) | One skill code held by a technician. | 4.1 |
| `TechnicianAvailability` (`TechnicianAvailabilities`) | One interval when a technician is available. | 4.1 |
| `MaintenanceWindow` (`MaintenanceWindows`) | A time window in which work may be planned. | 4.1 |
| `MaintenanceHistoryRecord` (`MaintenanceHistoryRecords`) | A past maintenance event for an asset, with its source. | 4.1 |
| `PlanningJob` (`PlanningJobs`) | One asynchronous planning request and its result. | 4.2 |
| `Draft` (`Drafts`) | An approved plan created from a planning job. | 4.2 |
| `Reservation` (`Reservations`) | Stock reserved for a draft. | 4.2 |
| `LockedTaskAssignment` (`LockedTaskAssignments`) | A technician locked to a task in a draft. | 4.2 |
| `EditedTaskPlacement` (`EditedTaskPlacements`) | A pending manual change to a task's time or crew. | 4.2 |
| `DraftTaskProgress` (`DraftTaskProgress`) | Recorded progress of a task in an approved draft. | 4.2 |
| `BusinessActionRecord` (`BusinessActionRecords`) | Audit history of business actions. | 4.2 |

## 4. Field dictionary

"Req." means the column is `NOT NULL`. "Source of values" is where the value comes from at runtime; `import` means the import validator, which applies the extra rules in section 8.

### 4.1 Reference data

**`Asset`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | text (PK) | yes | For example `P-101`. |
| `PlantId` | text | yes | For example `PLANT-01`. Not a foreign key; there is no plant table. |
| `Name` | text | yes | |
| `Model` | text | yes | |
| `Priority` | integer | yes | Import accepts 1 to 99. Lower numbers appear to be higher priority in the seeds (P-101 is 1), but no source states the meaning, so treat the ordering as unverified. |

**`SparePart`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | text (PK) | yes | |
| `Name` | text | yes | |
| `Unit` | text | yes | Import accepts `each`, `pair`, `set`, `kg`, `g`, `l`, `ml`, `m`. |
| `OnHandQuantity` | decimal | yes | Physical stock. Import accepts zero or more; whole numbers only for `each`, `pair`, `set`. |
| `ReservedQuantity` | decimal | yes | Quantity held by confirmed reservations. Import always sets 0. Approval and cancellation change it (see ARCHITECTURE.md section 5). |
| `AvailableQuantity` | none | n/a | **Not a column.** The code computes `OnHandQuantity - ReservedQuantity` (`SparePart.cs`). |

**`SparePartAssetCompatibility`** (join table, not an entity class): composite primary key (`SparePartId`, `AssetId`), index on (`AssetId`, `SparePartId`). Both are foreign keys with delete restricted. This is the only place compatibility is stored.

**`MaintenanceTaskDefinition`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | text (PK) | yes | |
| `AssetId` | text (FK to `Assets`) | yes | Delete restricted. |
| `Name` | text | yes | |
| `DurationMinutes` | integer | yes | Import accepts 1 to 1440 and a multiple of 30. |
| `RequiredSkills` | text | yes | **Comma-separated skill codes** (the import joins with `,`). The seeds store a single code. |
| `RequiredCrewSize` | integer | yes | Number of technicians. Import column name is `crewSize`; accepts 1 to 10. |
| `RequiredPartId` | text (FK to `SpareParts`) | no | Delete restricted. |
| `RequiredPartQuantity` | decimal | yes | 0 when no part is required. |
| `PredecessorTaskId` | text (FK to the same table) | no | Delete restricted. A task that must finish first. |
| `SourceDocumentId` | text | yes | Manual document ID. Not a foreign key (manuals are not in the database). |
| `SourceSection` | text | yes | Section title in that manual. |

Indexes: `AssetId`, `PredecessorTaskId`, `RequiredPartId`.

**`Technician`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | text (PK) | yes | For example `TECH-001`. |
| `Name` | text | yes | |
| `HourlyRate` | decimal | yes | Import accepts a positive amount with at most two decimal places. No currency is stored. |

**`TechnicalSkill`**: `Id` (integer, auto-increment PK); `TechnicianId` (text, FK to `Technicians`, delete cascades, indexed); `SkillCode` (text). A technician's skills are one row each. Skill codes are free text; there is no skill table.

**`TechnicianAvailability`**: `Id` (integer, auto-increment PK); `TechnicianId` (text, FK to `Technicians`, delete cascades, indexed); `AvailableFrom` and `AvailableTo` (`DateTimeOffset`, both required). Import accepts only an explicit UTC offset, a 30-minute boundary, `from` earlier than `to`, and no overlap with saved or same-file intervals for that technician.

**`MaintenanceWindow`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | text (PK) | yes | For example `MW-P101-BASELINE-20261024`. |
| `Name` | text | yes | |
| `Start`, `End` | `DateTimeOffset` | yes | |
| `TimeZoneId` | text | yes | Default `Asia/Kolkata`. |

**There is no import dataset and no write endpoint for maintenance windows.** Windows come only from the startup seed or from code.

**`MaintenanceHistoryRecord`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | text (PK) | yes | |
| `AssetId` | text (FK to `Assets`) | yes | Delete restricted. |
| `PerformedAt` | `DateTimeOffset` | yes | Import requires an explicit offset and rejects future dates. No 30-minute rule. |
| `Summary`, `Outcome` | text | yes | Import accepts up to 2000 characters each. |
| `SourceDocumentId` | text | yes | Import stores an empty string when the column is blank. Not checked against any manual. |
| `SourceReference` | text | yes | Import stores an empty string when blank. |

Index: (`AssetId`, `PerformedAt`).

### 4.2 Planning, approval and audit

**`PlanningJob`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `OperationId` | `Guid` | no | The `Idempotency-Key`. **Unique index.** |
| `OriginalJobId` | `Guid` | no | Set on replacement and revision jobs. **Not a foreign key**; no relationship is configured. |
| `RequestPayload` | text (JSON) | yes | Serialised `PlanningJobRequestPayload { RequestText, AssetIds, WindowId }` (`PlanningJobReadModel.cs`). |
| `RequestFingerprint` | text | no | Hash of the request, used to detect a reused key with a different request. |
| `ConcurrencyVersion` | integer | yes | Concurrency token. Default 0. Incremented by `SaveChanges` on every modified job. |
| `Status` | text (enum) | yes | `PlanningJobStatus`. Default `Queued`. |
| `AttemptCount` | integer | yes | Default 0. |
| `MaxAttempts` | integer | yes | Default 2. |
| `CurrentStep` | text | yes | Default `Queued`. |
| `CheckpointPayload` | text (JSON) | no | The validated interpretation saved so a restart need not call the model again. |
| `ResultPayload` | text (JSON) | no | Envelope `PlanningJobResultEnvelope { Outcome, Result }`. |
| `ClarificationPayload` | text (JSON) | no | Written when a clarification is submitted (`UpdateAndResetAsync`). Its inner shape was not verified. |
| `FailureCode`, `ErrorMessage` | text | no | |
| `CreatedAtUtc` | `DateTimeOffset` | yes | |
| `StartedAtUtc`, `CompletedAtUtc` | `DateTimeOffset` | no | |
| `WorkerLeaseToken` | `Guid` | no | Identifies the worker that holds the lease. |
| `LeaseExpiresAtUnixMilliseconds` | long | no | Lease expiry. |

Indexes: unique `OperationId`; (`Status`, `CreatedAtUtc`).

**`Draft`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `PlanningJobId` | `Guid` (FK to `PlanningJobs`) | yes | **Unique index**: at most one draft per job. Delete restricted. |
| `Status` | text (enum) | yes | `DraftStatus`. |
| `CreatedAtUtc` | `DateTimeOffset` | yes | |
| `ApprovedAtUtc` | `DateTimeOffset` | no | |
| `ApprovedCandidatePayload` | text (JSON) | no | Snapshot of the candidate that was approved. |

**`Reservation`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `DraftId` | `Guid` (FK to `Drafts`) | yes | Delete cascades. Indexed. |
| `SparePartId` | text (FK to `SpareParts`) | yes | Delete restricted. Indexed. |
| `QuantityReserved` | decimal | yes | |
| `Status` | text (enum) | yes | `ReservationStatus`. |
| `CreatedAtUtc` | `DateTimeOffset` | yes | |
| `ConfirmedAtUtc` | `DateTimeOffset` | no | Set when approval inserts the reservation (`DraftApprovalService.cs`). |
| `CompletedAtUtc` | `DateTimeOffset` | no | Set when the part is consumed on task completion. |

**`LockedTaskAssignment`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `DraftId` | `Guid` (FK to `Drafts`) | yes | Delete restricted. |
| `TaskId` | text | yes | **Not a foreign key** to `MaintenanceTaskDefinitions`. |
| `LockedTechnicianId` | text | yes | **Not a foreign key** to `Technicians`. |
| `CreatedAtUtc` | `DateTimeOffset` | yes | |

Unique index: (`DraftId`, `TaskId`).

**`EditedTaskPlacement`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `DraftId` | `Guid` (FK to `Drafts`) | yes | Delete cascades. |
| `TaskId` | text | yes | Not a foreign key. |
| `OriginalStart`, `OriginalEnd` | `DateTimeOffset` | yes | From the approved candidate. |
| `OriginalTechnicianIds` | text (JSON array) | yes | |
| `NewStart`, `NewEnd` | `DateTimeOffset` | yes | The proposed values. |
| `NewTechnicianIds` | text (JSON array) | yes | |
| `ValidationStatus` | text | yes | Free text, not an enum. The code writes `pending`, `valid` and `invalid` (`TaskEditorService.cs`, `EditValidationService.cs`). |
| `ValidationViolations` | text (JSON) | no | |
| `CreatedAtUtc` | `DateTimeOffset` | yes | |
| `UpdatedAtUtc` | `DateTimeOffset` | no | |

Unique index: (`DraftId`, `TaskId`).

**`DraftTaskProgress`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `DraftId` | `Guid` (FK to `Drafts`) | yes | Delete restricted. |
| `TaskId` | text | yes | Not a foreign key. |
| `Status` | text (enum) | yes | `TaskProgressStatus`. |
| `UpdatedAtUtc` | `DateTimeOffset` | yes | |

Unique index: (`DraftId`, `TaskId`). A placed task with no row has not started (entity comment).

**`BusinessActionRecord`**

| Field | Type | Req. | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `Action` | text | yes | One of the values in section 6. |
| `PerformedBy` | text | yes | The role from the `X-Actor-Role` header, or `unspecified`. Roles are self-declared, not authenticated (`BusinessActor.cs`). |
| `PerformedAtUtc` | `DateTimeOffset` | yes | |
| `PlanningJobId`, `DraftId` | `Guid` | no | **Not foreign keys.** The DbContext comment says history rows have no foreign keys so they never block or follow the records they describe. |
| `TaskId` | text | no | |
| `Details` | text | no | Free text, for example "Approved alternative N of M". |

Indexes: (`PlanningJobId`, `PerformedAtUtc`); (`DraftId`, `PerformedAtUtc`). The entity comment says rows are never changed after they are written.

## 5. Relationships

| From | To | Kind | On delete |
|---|---|---|---|
| `MaintenanceTaskDefinition.AssetId` | `Asset` | many to one | Restrict |
| `MaintenanceTaskDefinition.RequiredPartId` | `SparePart` | many to one, optional | Restrict |
| `MaintenanceTaskDefinition.PredecessorTaskId` | `MaintenanceTaskDefinition` | self, optional | Restrict |
| `SparePartAssetCompatibility` | `SparePart` and `Asset` | many to many | Restrict |
| `MaintenanceHistoryRecord.AssetId` | `Asset` | many to one | Restrict |
| `TechnicalSkill.TechnicianId` | `Technician` | many to one | Cascade |
| `TechnicianAvailability.TechnicianId` | `Technician` | many to one | Cascade |
| `Draft.PlanningJobId` | `PlanningJob` | one to one (unique) | Restrict |
| `Reservation.DraftId` | `Draft` | many to one | Cascade |
| `Reservation.SparePartId` | `SparePart` | many to one | Restrict |
| `LockedTaskAssignment.DraftId` | `Draft` | many to one | Restrict |
| `EditedTaskPlacement.DraftId` | `Draft` | many to one | Cascade |
| `DraftTaskProgress.DraftId` | `Draft` | many to one | Restrict |

**Links that look like references but are not enforced by the database:** `PlanningJob.OriginalJobId`, `LockedTaskAssignment.TaskId` and `LockedTechnicianId`, `EditedTaskPlacement.TaskId`, `DraftTaskProgress.TaskId`, every ID on `BusinessActionRecord`, and `MaintenanceTaskDefinition.SourceDocumentId`.

## 6. Enumerations

Transitions are summarised here and shown as diagrams in [ARCHITECTURE.md](ARCHITECTURE.md) section 4.

| Enumeration | Values | Meaning and transitions (from code) |
|---|---|---|
| `PlanningJobStatus` | `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled` | Created `Queued`. A worker claim sets `Running`. `Succeeded` means a business result was stored, **including** outcomes such as `ClarificationRequired` or infeasible. `Failed` is a technical failure or exhausted attempts. A transient failure with attempts left, or a graceful shutdown, returns `Running` to `Queued`. A clarification resets a `ClarificationRequired` job to `Queued`. A revision created from edits is inserted directly as `Succeeded`. **`Cancelled` is declared but no code assigns it.** |
| `DraftStatus` | `Pending`, `Approved`, `Cancelled`, `Superseded` | Every draft is inserted `Approved`. An approved draft becomes `Superseded` when a replacement is approved, or `Cancelled` when cancelled. **`Pending` is the property default but no code assigns it.** |
| `ReservationStatus` | `Pending`, `Confirmed`, `Cancelled` | Inserted `Confirmed` at approval. Becomes `Cancelled` when the draft is superseded or cancelled. Consumption sets `CompletedAtUtc` and leaves the status `Confirmed`. **`Pending` is never assigned.** |
| `TaskProgressStatus` | `NotStarted`, `InProgress`, `Completed` | Forward only: `NotStarted` to `InProgress` to `Completed`. Other moves are rejected. |

**Other fixed value sets**

| Set | Values | Source |
|---|---|---|
| Business actions (`BusinessActionRecord.Action`) | `Approved`, `Cancelled`, `ReplanRequested`, `TaskProgressUpdated`, `PartConsumptionRecorded`, `TaskEditSubmitted`, `TaskEditDeleted`, `TaskEditsCleared`, `TaskLocked`, `TaskUnlocked`, `RevisionCreatedFromEdits` | `BusinessActor.cs` |
| Actor roles (`PerformedBy`) | `planner`, `reviewer`, `technician`, `unspecified` | `BusinessActor.cs` |
| Part units | `each`, `pair`, `set`, `kg`, `g`, `l`, `ml`, `m` (the first three are count units) | `ReferenceDataImportService.cs` |
| Import datasets | `assets`, `spare-parts`, `technicians`, `technician-availability`, `tasks`, `maintenance-history` (plus `manuals` through its own route) | `ImportContracts.cs`, `ImportEndpoints.cs` |
| Import formats | `Csv`, `Json` for datasets; `Pdf` for manuals | `ImportContracts.cs` |

## 7. Sample data and where it comes from

The project has three sources of data, which must not be confused.

| | Startup anchor seed | Six-file import (PLANT-01 fixtures) | AT01 test seed |
|---|---|---|---|
| Code or files | `Seeding/P101AnchorSeed.cs`, run by `DatabaseInitializer.InitializeAsync` | `data/fixtures/plant01/` loaded through the API by `import-plant01.ps1` | `Seeding/AT01_FiveAssetSeed.cs` |
| When it runs | Automatically at every API start, after migrations | Only when someone runs the script or calls the import endpoints | Only inside `AT01_FiveAssetAcceptanceTests` |
| Behaviour | Adds a record only if its ID is missing. Never updates. | Insert-only and all-or-nothing per file. Existing IDs are rejected. | Inserts into a test database. |
| Contents | Asset `P-101`; part `BR-101` (1 each) with compatibility to `P-101`; three `P-101` tasks; `TECH-001`, `TECH-002`, `TECH-003` with skills and availability; windows `MW-P101-BASELINE-20261024` and `MW-P101-REDUCED-20261024` | Assets `P-102` to `P-105`; five parts; one manual; eight tasks; eleven history records | Five assets `P-101` to `P-105`; five parts; five tasks; three technicians; one window `MW-AT01-BASELINE-20261024` |
| Maintenance history | **None.** | 11 records | None |
| Technicians, availability, windows | Yes | **No files exist for these.** | Yes |

Read each column as a separate dataset. The seed and the fixtures are meant to be combined for the five-asset demonstration (section 10). The AT01 seed is not a startup path and does not feed the running application.

### 7.1 Startup anchor seed contents

Verified from `P101AnchorSeed.cs`.

| Record | Values |
|---|---|
| Asset | `P-101`, plant `PLANT-01`, "Cooling Water Pump P-101", model `CP-100`, priority 1 |
| Part | `BR-101` "P-101 replacement bearing", unit `each`, on hand 1, reserved 0, compatible with `P-101` |
| Tasks | `P101-PREP` (60 min, `MECHANICAL`, crew 1, no part, no predecessor); `P101-BEARING-REPLACEMENT` (180 min, `ROTATING-EQUIPMENT`, crew 2, 1 x `BR-101`, predecessor `P101-PREP`); `P101-VERIFICATION` (120 min, `INSTRUMENTATION`, crew 1, no part, predecessor `P101-BEARING-REPLACEMENT`). Source manual `MAN-P101-001`, sections "Planning assumptions: Preparation", "... Bearing replacement" and "... Verification". |
| Technicians | `TECH-001` Asha Rao, rate 42.00, `MECHANICAL` and `ROTATING-EQUIPMENT`; `TECH-002` Kabir Shah, 39.50, `MECHANICAL` and `ROTATING-EQUIPMENT`; `TECH-003` Meera Iyer, 37.00, `MECHANICAL` and `INSTRUMENTATION`. Each is available 2026-10-24 08:00 to 16:00 (+05:30). |
| Windows | `MW-P101-BASELINE-20261024`: 2026-10-24 08:00 to 16:00 (+05:30). `MW-P101-REDUCED-20261024`: 08:00 to 12:00 (+05:30). Both `Asia/Kolkata`. |

The seed values are fixed in code. A modified seed requires a code change.

### 7.2 Files under `data/`

| Path | Contents | Used by |
|---|---|---|
| `data/fixtures/plant01/01-assets.csv` | Assets `P-102` to `P-105` (4 rows) | Import script |
| `data/fixtures/plant01/02-spare-parts.csv` | `SEAL-102` (2), `FILTER-103` (3), `COUPLING-104` (1), `GASKET-105` (4), `LUBE-OIL-46` (20 `l`, compatible with `P-101`, `P-102`, `P-104`, `P-105`) | Import script |
| `data/fixtures/plant01/MAN-PLANT01-SERVICE-001.pdf` | Synthetic service manual | Import script (`documentId=MAN-PLANT01-SERVICE-001`, `version=1.0`) |
| `data/fixtures/plant01/04-tasks.json` | 8 tasks for `P-102` to `P-105` | Import script |
| `data/fixtures/plant01/05-maintenance-history.csv` | 11 records for `P-101` to `P-105` | Import script |
| `data/fixtures/plant01/import-plant01.ps1` | Posts the five files in that order with `curl.exe` | Manual run |
| `data/fixtures/p101-anchor.json` | A description of the anchor data and two scenarios (baseline; changed constraints with the reduced window and 0 bearings) with expected outcomes | **Not read by the application code reviewed** (README section 7). Property names differ from the import columns (for example `requiredCrewSize` rather than `crewSize`), so it is not an import file. |
| `data/fixtures/fr02-demo/` | One valid and two invalid asset CSV files, plus a README with expected results | Manual demo through the Import screen |
| `data/manuals/MAN-P101-001.txt` | Text manual, bundled | Registered at startup |
| `data/manuals/MAN-P101-PLANNING-001.pdf` | PDF manual, bundled | Registered at startup |
| `data/manuals/MAN-P102-SCOPE-001.pdf` | PDF manual (inspection-only scope), bundled | Registered at startup |
| `data/manuals/README.md` | Describes the bundled manuals | Reference |
| `data/manuals/imported/` | Where imported manuals are stored (`{documentId}.pdf` and `{documentId}.json`). **Not present in the repository.** | Created by manual import |

The PDF files were not opened, so their section headings are not documented here.

**Counts after the PLANT-01 import, counted from the fixture files and the seed** (the README states that `FR03FiveAssetPlanningFileDatabaseTests` asserts the dataset minimums; this section was not re-run): 5 assets, 11 tasks (3 seeded and 8 imported), 3 technicians, 6 parts (`BR-101` and 5 imported), 11 history records, 2 windows.

## 8. Import: formats, datasets and validation

Endpoint details (statuses and bodies) are in [ARCHITECTURE.md](ARCHITECTURE.md) section 6. This section describes the data rules, from `ImportRecordReader.cs`, `ReferenceDataImportService.cs`, `ManualImportService.cs` and `ImportEndpoints.cs`.

### 8.1 General rules

| Rule | Detail |
|---|---|
| Request | `multipart/form-data` with the file in a field named `file`. |
| Structured formats | `.csv` (RFC 4180: header row, comma separated, double-quoted fields) or `.json` (an array of objects). The extension decides the format. Other extensions return 400. Maximum 10 MB. |
| Lists | `;` separated in CSV; a JSON array in JSON. |
| Identifiers | 1 to 64 characters: a letter or digit, then letters, digits, `.`, `_` or `-`. |
| Unknown columns or properties | Reported as errors, not ignored. |
| Insert-only | A record whose ID already exists is rejected. Existing data is never changed. |
| All-or-nothing | If any record is invalid, **nothing** in that file is saved and every problem is reported. |
| Timestamps | ISO 8601 with an explicit offset, for example `2026-10-24T08:00:00+05:30`. |
| Granularity | Availability timestamps and task durations must fall on the 30-minute grid. |

### 8.2 Columns by dataset

| Dataset (slug) | Columns | Rules beyond section 8.1 |
|---|---|---|
| `assets` | `id`, `plantId`, `name`, `model`, `priority` | `id` and `plantId` must be identifiers; `name` and `model` are text up to 200 characters; `priority` is a whole number 1 to 99; all required. |
| `spare-parts` | `id`, `name`, `unit`, `onHandQuantity`, `compatibleAssetIds` | `unit` from the supported list; quantity zero or more, whole for count units; `compatibleAssetIds` is required (an empty value is rejected), must list existing assets, and may not repeat an ID. |
| `technicians` | `id`, `name`, `hourlyRate`, `skills` | Rate positive with at most two decimals; `skills` required, valid identifiers, no repeats. |
| `technician-availability` | `technicianId`, `availableFrom`, `availableTo` | The technician must exist. `availableTo` after `availableFrom`. No overlap with saved intervals or other rows in the file. |
| `tasks` | `id`, `assetId`, `name`, `durationMinutes`, `requiredSkills`, `crewSize`, `requiredPartId`, `requiredPartQuantity`, `predecessorTaskId`, `sourceDocumentId`, `sourceSection` | Asset must exist. Duration 1 to 1440 and a multiple of 30. Crew 1 to 10. If a part is named it must exist, be compatible with the asset, and have a quantity above zero (whole for count units). A quantity without a part is rejected. The predecessor must be a saved task or in the same file, not the task itself, and not part of a cycle. `sourceDocumentId` and `sourceSection` are required and **must match a manual and a section title the planning evidence retriever can find.** |
| `maintenance-history` | `id`, `assetId`, `performedAt`, `summary`, `outcome`, `sourceDocumentId`, `sourceReference` | Asset must exist. `performedAt` may not be in the future. `summary` and `outcome` up to 2000 characters. The two source columns are optional. |

Columns not named above, such as the `ReservedQuantity` of a part, cannot be set by import.

### 8.3 Manuals (`POST /api/imports/manuals`)

- Fields: `file` (`.pdf`, maximum 20 MB), `documentId`, `version`.
- `documentId` must be a valid identifier and must not match a bundled manual or an already imported one. `version` is 1 to 32 letters, digits, `.`, `_` or `-`.
- Every page must have extractable text. The manual must contain at least one `Section:` heading. A `Document ID:` line, if present, must match the supplied ID.
- Storage is on the **file system**, not in the database: `{documentId}.pdf` plus `{documentId}.json` in `ManualImports:Directory` (default `data/manuals/imported`). See section 10.

### 8.4 Error reporting

Every import returns an `ImportReport`: `dataset`, `format`, `fileName`, `imported` (true only if every record was saved), `totalRecords`, `importedRecords`, `invalidRecords` and `issues`. Each issue has `recordNumber`, `location`, `recordId`, `field` and `message`.

| Situation | Status | Report |
|---|---|---|
| Imported | 200 | `imported` true, no issues |
| Any invalid record, unknown column, duplicate ID, bad header, or other data problem | 422 | `imported` false, every issue listed in record order, nothing saved |
| Not multipart, `file` missing, file too large, wrong extension, unknown dataset | 400 | One file-level issue |

The `recordNumber` is the CSV line number (the header is line 1), the one-based position in a JSON array, or the one-based PDF page. File-level problems have no record number and `location` `file`. In a CSV file, a header problem (unknown, empty or duplicate column) stops the check before any row is validated, so row-level errors in that file are **not** reported until the header is fixed. In a JSON file an unknown property makes only that record unreadable.

### 8.5 Invalid-data fixtures and expected errors

`data/fixtures/fr02-demo/` holds the separate bad-data fixtures for the `assets` dataset.

| File | Expected result (from the fixture README) |
|---|---|
| `fr02-valid-assets.csv` | 200; 2 of 2 records imported. A second import of the same file returns 422 with a duplicate-ID message per row. |
| `fr02-invalid-assets.csv` | 422; 5 rows read, 0 imported, 4 invalid rows, 5 issues: `priority` below range (row 3), `priority` not a number (row 4), identifier with spaces (row 5), duplicate ID (row 6), empty `name` (row 6). The valid row 2 is not saved. |
| `fr02-invalid-assets-unknown-column.csv` | 422; one file-level issue "Unknown column 'location'"; the bad `priority` on row 3 is not reported. |

**Evidence status:** the fixture README says these outcomes are "derived from the validator code ... not from a recorded run". Automated tests cover the same behaviour with inline data (`FR02DataImportFileDatabaseTests`: valid CSV and JSON for every dataset; invalid asset rows; invalid task records; invalid availability and part rows; malformed files; text PDF; invalid PDFs). **Stage 7:** the `FR02DataImportFileDatabaseTests` class (valid CSV and JSON for every dataset, invalid rows, malformed files, text and invalid PDF imports) and `ImportRecordReaderTests` passed on 2026-10-29 (R-14 and R-15 in [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md)), using temporary databases and manual directories. That run used the tests' own inline data, not the files in `data/fixtures/`, and the import script was not run. Invalid-data **files** exist only for assets; the invalid task, availability, part and PDF cases exist only inside tests.

## 9. Loading the sample data

**Startup seed.** Automatic. Starting the API against any database file applies the migrations and then the anchor seed (section 7.1). This was **not executed** in the stage that wrote this document.

**PLANT-01 five-asset import.** From the repository root, with the API running:

```powershell
powershell -File data/fixtures/plant01/import-plant01.ps1 -ApiBase http://localhost:5083
```

*Documented from the script. The script itself was not run.* It needs PowerShell and `curl.exe`. It posts the files in this order and stops at the first response other than 200:

1. `01-assets.csv` to `/api/imports/assets`
2. `02-spare-parts.csv` to `/api/imports/spare-parts`
3. `MAN-PLANT01-SERVICE-001.pdf` to `/api/imports/manuals` (`documentId=MAN-PLANT01-SERVICE-001`, `version=1.0`)
4. `04-tasks.json` to `/api/imports/tasks`
5. `05-maintenance-history.csv` to `/api/imports/maintenance-history`

**Dependencies between the steps (derived from code and fixtures):**
- The spare-parts file names `P-101` in `LUBE-OIL-46`, so `P-101` must already exist. It comes from the startup seed.
- Tasks cite the manual `MAN-PLANT01-SERVICE-001`, which step 3 supplies. Task `P102-INSPECTION` cites the bundled `MAN-P102-SCOPE-001`.
- Technicians, availability and windows come only from the seed. There are no fixture files for them.
- Importing a second time reports "already exists" for every ID and changes nothing.

**Other datasets.** No sample files are provided for `technicians` or `technician-availability`. Their formats are in section 8.2 and the tests contain examples.

**Not verified:** that the script succeeds on a fresh database (the README states the same). See the manual-directory gotcha in section 10.

## 10. Reset and cleanup

**There is no reset command.** No script, command, endpoint or code path recreates the baseline database. This **does not meet** the requirement in [requirementsforpoc.md](requirementsforpoc.md) section 5 ("a seed/reset command. The reset must recreate the same baseline locally") and DEL03. There is also no endpoint that deletes imported records or manuals.

What exists, all *derived from code and not executed*:

| Mechanism | Effect | Limits |
|---|---|---|
| Point `ConnectionStrings__MaintenancePlanner` at a file that does not exist | The API creates the schema (18 migrations) and the anchor seed on start. | The five-asset import must be run again. |
| Set `ManualImports__Directory` to an empty folder | Imported manuals start empty. | None found. |
| The `fr02-demo` README procedure | Uses a temporary database and manual directory under `%TEMP%`. | Documented in that README; not run. |

**Limitations and risks:**
- **Two stores, not one.** Imported manuals live in `ManualImports:Directory`; everything else lives in the SQLite file. Recreating only the database leaves the manuals. `ManualImportService` rejects an existing `documentId`, so `import-plant01.ps1` on a new database with the old manual folder would stop at step 3 with a 422 report. *Derived from code; not executed.*
- **No automatic baseline for imports.** Even a new database holds only the anchor seed.
- **The tracked database changes when you run the API.** `maintenance-planner.db` and the stale `maintenance-planner copy.db` are both tracked by git, although `.gitignore` lists `*.db`. Running the API against the default path writes to the tracked file. This stage did not open either file or any WAL or SHM sidecar.
- **Seed values cannot be changed without editing code.**

## 11. Known discrepancies and unverified behaviour

1. **Reset is missing** (section 10).
2. **No window, technician or availability fixtures** for the import process; only the seed provides them. The `windows` dataset does not exist at all.
3. **Changed datasets for acceptance tests are not separate files.** The only stored "changed constraints" scenario is in `p101-anchor.json`, which no code reads. The tests build their own data inline (see [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md)). DEL03 asks for "all changed or invalid datasets used for acceptance tests".
4. **The AT01 seed conflicts with the fixtures and cites missing manuals.** In the AT01 seed `P-102` is "Heat Exchanger P-102" (model `HX-200`), but in `01-assets.csv` it is "Cooling Water Pump P-102" (model `CP-100`). The AT01 tasks cite `MAN-P102-001`, `MAN-P103-001`, `MAN-P104-001` and `MAN-P105-001`; no manual with those IDs exists in `data/`. The two are used in different tests, so this does not break either, but they must not be combined.
5. **`p101-anchor.json` is not an import file** and is not read by the code reviewed (property names differ from the import columns).
6. **Skill storage mismatch.** CSV and JSON lists use `;`, but tasks store skills comma-separated, and technicians store one row per skill.
7. **Free-text fields.** `EditedTaskPlacement.ValidationStatus` is text, not an enum. `PlanningJob.OriginalJobId` and every `TaskId` link are unenforced.
8. **Unused enum values.** `PlanningJobStatus.Cancelled`, `DraftStatus.Pending` and `ReservationStatus.Pending` are never assigned.
9. **`Asset.Priority` ordering** (whether 1 is the highest) is not stated in any source read.
10. **The PDF manuals** were not opened, so their section headings and the fit between task `sourceSection` values and the bundled manuals are *not verified here*. The import validator checks this at import time.
11. **Evidence gaps.** There are no recorded runs of the import script, of a clean setup, or of the JSON and PDF imports. The FR02 and FR03 behaviour above comes from source and from the existence of automated tests, not from evidence logs written for this document.
