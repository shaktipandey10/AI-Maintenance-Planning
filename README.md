# AI Maintenance Planner

A proof-of-concept application that helps a planner turn a natural-language maintenance request into reviewable, evidence-backed plans for several assets at one fictional plant. It checks every plan against deterministic rules, keeps approved plans and their history, and reserves spare parts only on approval.

The scope comes from [docs/requirementsforpoc.md](docs/requirementsforpoc.md) (the original assignment) and [docs/Feedback.md](docs/Feedback.md) (the reviewer's feedback). Current status is in [docs/REQUIREMENTS_STATUS.md](docs/REQUIREMENTS_STATUS.md).

## Delivered and verified

The planner is built end to end: import of reference data and manuals, natural-language planning requests, evidence-backed alternatives checked by a deterministic validator, approval with spare-part reservations, revisions and replanning, task locks and task progress, cancellation, a business-action history, restart-safe background jobs, and JSON and PDF exports. The React frontend drives the workflow and a local Ollama model (`qwen2.5:7b`) interprets requests.

| Item | Result |
|---|---|
| Requirements (23: FR01 to FR10, AI01 to AI07, APP01 to APP06) | **12 Implemented, 11 Partially implemented, 0 Not implemented** ([docs/REQUIREMENTS_STATUS.md](docs/REQUIREMENTS_STATUS.md)) |
| Backend automated tests | **606 test cases passed** (mocked model; isolated databases) |
| Frontend automated tests | **112 tests passed** |
| Acceptance scenarios AT01 to AT12 | **10 Pass — Automated, 2 Partial (AT05, AT12), 0 Fail, 0 Not Verified** ([docs/ACCEPTANCE_TEST_REPORT.md](docs/ACCEPTANCE_TEST_REPORT.md)) |
| Manual checks by the owner | **Completed — Manually Tested by Owner:** task progress; V1 and V2 exports checked against their planning job IDs; resource edits with replanning |
| Demonstration and exports | **Owner-confirmed:** demonstration videos (including a 16-minute demonstration) and the B1 and B2 PDF and JSON exports |

**What these results do and do not show.**

- Every automated pass used a mocked or fixed model interpretation. The one real-model run failed with `OLLAMA_TIMEOUT`, so the AI tool-calling path (AI03) is Partially implemented and no behaviour is verified with the real model.
- The manual checks were done by the owner and were not repeated by the assistant. They do not change the automated results.
- Known limitations: there is no before-and-after revision comparison; the exports omit assumptions, evidence references and deferred work; there is no validation-repair step (AI04); AI02 evidence is shown as source IDs in the UI; there is no seed and reset command. A clean setup, a real-model evaluation and a live-process restart have not been verified.
- Details and every remaining gap: [docs/SUBMISSION_HANDOVER.md](docs/SUBMISSION_HANDOVER.md) and [docs/SUBMISSION_GAP_SUMMARY.md](docs/SUBMISSION_GAP_SUMMARY.md); see also [Known limitations](#10-known-limitations-and-unverified-behaviour).

**Verification labels used below:**
- **[Executed]**: run during this project's development, and the result is recorded in `docs/DebugAndTests/`, or the run is named in this README.
- **[Documented]**: taken from the repository's configuration files; not run as part of the evidence.

---

## 1. Purpose and scope

- A planner submits a request naming assets, a maintenance window and a planning objective.
- A background worker interprets the request, retrieves manual evidence, builds candidate schedules, and validates each one.
- The planner reviews alternatives, edits or locks tasks, and submits a plan for approval.
- A reviewer approves one candidate. Approval revalidates it, reserves spare parts, and records the action.
- Approved plans can be replanned as a new revision, tracked, and exported to PDF and JSON.

Requirement references: FR01–FR10, AI01–AI07 and APP01–APP06 (see [REQUIREMENTS_STATUS.md](docs/REQUIREMENTS_STATUS.md)).

The data is fictional and synthetic. No real equipment or factory system is connected.

## 2. Architecture overview and technologies

```
Browser (React)  --HTTP-->  ASP.NET Core API  --EF Core-->  SQLite (maintenance-planner.db)
                               |                 ^
                               |                 |  PlanningJobWorker (background service)
                               +--HTTP--> Ollama (local model, qwen2.5:7b)
```

| Layer | Technology (as found in the repository) |
|---|---|
| Frontend | React 19.3 (`^19.3.1`), TypeScript 6.0 (`~6.0.3`), Vite 8.3 (`^8.3.4`), Vitest 5.0 (`^5.0.5`) — `frontend/package.json` |
| Backend | ASP.NET Core on .NET 10 (`net10.0`) — `src/MaintenancePlanner.Api/MaintenancePlanner.Api.csproj` |
| Persistence | SQLite through Entity Framework Core 10.0.13 (`Microsoft.EntityFrameworkCore.Sqlite`) — 18 migrations in `src/MaintenancePlanner.Infrastructure/Persistence/Migrations/` |
| Background planning | `PlanningJobWorker` (`src/MaintenancePlanner.Api/Workers/`) with lease-based claims |
| AI model | Ollama HTTP API (`/api/chat`) with `qwen2.5:7b` — configured in `appsettings.json` |
| Manual evidence | PdfPig 0.1.16 for PDF text; plain-text manuals |
| Exports | PDF via iTextSharp 5.5.13.3; JSON via System.Text.Json |

**Layers** (solution `MaintenancePlanner.slnx`): `Domain` (entities), `Application` (contracts, workflow, validators, tools), `Infrastructure` (EF Core, services, Ollama client, evidence retrieval), `Api` (endpoints, worker, startup), plus `UnitTests` and `IntegrationTests`.

For the detailed architecture, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## 3. Prerequisites

| Requirement | Version or detail | Verified |
|---|---|---|
| .NET SDK | 10.0 (the target is `net10.0`; there is no `global.json`, so the SDK is not pinned). SDK 10.0.300 was used during development. | [Executed] build of the API project in development. The SDK pin is not enforced. |
| Node.js and npm | Not pinned in the repository (no `engines` field). Versions are unconfirmed. | [Documented] — install a current LTS version. |
| Ollama | 0.40.12 was used. | [Executed]: `ollama --version` in `docs/DebugAndTests/AI03_Stage3_OllamaCompatibilityPreflight.txt` |
| Ollama model | `qwen2.5:7b` (7.6B parameters, Q4_K_M, with the `tools` capability) | [Executed]: `ollama show qwen2.5:7b` (same log) |
| Operating system | Windows 10 (the development and evidence machine). `import-plant01.ps1` requires PowerShell and `curl.exe`. | [Executed] on Windows 10 |

**Model access.** The model runs locally. No account, API key or internet access is required. The API expects Ollama at `http://localhost:11434`. To install the model, use the standard Ollama command `ollama pull qwen2.5:7b`. **[Documented]**, not run as part of the evidence.

## 4. Setup and configuration

No credentials are used anywhere in this project. The configuration keys are listed below as they appear in the repository. There is currently no `.env.example` file (see [§10](#10-known-limitations-and-unverified-behaviour)).

### Settings read by the API

Configuration is read from `src/MaintenancePlanner.Api/appsettings.json`. Any setting can be overridden with an environment variable whose name uses `__` (double underscore) in place of `:`.

| Key | Default in repository | Purpose |
|---|---|---|
| `ConnectionStrings:MaintenancePlanner` | `Data Source=../../maintenance-planner.db` | SQLite file. A relative path is resolved against the API project directory, so this is the repository-root database. |
| `Ollama:BaseUrl` | `http://localhost:11434` | Ollama endpoint. Must be an absolute HTTP or HTTPS URL. |
| `Ollama:Model` | `qwen2.5:7b` | Model name sent with each chat request. |
| `Ollama:TimeoutSeconds` | `180` | Timeout for each Ollama request (1–600). |
| `Ollama:EnableTools` | `false` (code default in `OllamaOptions.cs`; not in `appsettings.json`) | Turns on the AI tool-calling loop. Off by default. |
| `Ollama:MaxToolCalls` | `6` (code default; allowed 1–20) | Maximum tool calls per interpretation. |
| `ManualImports:Directory` | `data/manuals/imported` (relative to the repository) | Where imported PDF manuals are stored. |
| `PlanningJobs:*` | Code defaults (`PollInterval` 1 s, `LeaseDuration` 30 s) | Worker polling and lease. |

### Example environment overrides (no secrets)

These are **examples**, not a file. Set them in the shell that starts the API. A process-only setting does not change any tracked file.

```powershell
# PowerShell, current session only
$env:Ollama__EnableTools = "true"
$env:ConnectionStrings__MaintenancePlanner = "Data Source=D:\path\to\a-copy.db"   # see §7 before using the default path
```

ASP.NET Core does not read `.env` files by itself.

### Frontend

The frontend reads `VITE_API_BASE`. If it is not set, the API base defaults to `http://localhost:5083` (`frontend/src/App.tsx`, `frontend/src/services/apiClient.ts`).

---

## 5. Run commands

Start the **API first**, then the frontend.

### Backend (API)

```powershell
dotnet run --project src/MaintenancePlanner.Api --launch-profile http
```

- **[Executed]**: this command started the API. The log showed `Now listening on: http://localhost:5083` and `Hosting environment: Development` (`docs/DebugAndTests/AI03_Stage3_RealModelRun.txt`, section 2 and the run log).
- The `http` launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and listens on `http://localhost:5083` (`src/MaintenancePlanner.Api/Properties/launchSettings.json`). The `https` profile also uses port 7079.
- At startup the API applies migrations and the anchor seed (see [§7](#7-sample-data-and-seed-or-reset)).
- Swagger UI is registered only in the Development environment (`Program.cs`), at the default ASP.NET Core path `/swagger`. **[Documented]**, not opened during this review.

### Frontend

```powershell
cd frontend
npm install
npm run dev
```

- **[Documented]** from `frontend/package.json` (`"dev": "vite"`). Not executed as part of the evidence.
- The Vite configuration does not set a port (`frontend/vite.config.ts`), so Vite's default (5173) applies. A browser screenshot in `docs/Screenshots/` shows `localhost:5173`. Confirm the port Vite prints on startup.
- Production build: `npm run build` (runs `tsc -b && vite build`), and `npm run preview` to serve it. **[Documented]**.

## 6. Tests

Test projects: `tests/MaintenancePlanner.UnitTests` and `tests/MaintenancePlanner.IntegrationTests`. The integration tests use in-memory SQLite or temporary database files. A search found no test file that references `maintenance-planner.db`.

**Commands executed during development** (the output is in the referenced evidence files):

```powershell
# Model-independent AI03 proposal store (4/4 passed) — docs/DebugAndTests/AI03_ApprovalPath_Verification.txt
dotnet test tests/MaintenancePlanner.UnitTests/MaintenancePlanner.UnitTests.csproj --filter "FullyQualifiedName~AI03ModelCandidateProposalsTests"

# AI03 tool loop and save-candidate integration tests, with mocked model replies (23/23 passed) — docs/DebugAndTests/AI03_ApprovalPath_Verification.txt
dotnet test tests/MaintenancePlanner.IntegrationTests/MaintenancePlanner.IntegrationTests.csproj --filter "FullyQualifiedName~AI03ToolLoopIntegrationTests|FullyQualifiedName~AI03SaveCandidateTests"

# Window-ID prompt fix, interpreter unit tests (32/32 passed) — docs/DebugAndTests/AI03_WindowIdFix_Tests.txt
dotnet test tests/MaintenancePlanner.UnitTests/MaintenancePlanner.UnitTests.csproj --filter "FullyQualifiedName~AI03OllamaToolLoopTests|FullyQualifiedName~OllamaPlanningRequestInterpreterTests"
```

Scope: these are focused runs. Each evidence file states the exact commands and exit codes.

**Final focused verification (2026-10-29, [Executed]).** Eight focused backend groups plus the frontend suite were run and captured in `docs/DebugAndTests/Final_*.txt`, with the command, time and repository state in each header. Together the backend groups ran every test method that `dotnet test --list-tests` reports (606 test cases) and **all passed**; the frontend run passed 112 tests in 9 files. The full table is run register entries R-13 to R-20 in [docs/ACCEPTANCE_TEST_REPORT.md](docs/ACCEPTANCE_TEST_REPORT.md). Every test fixture uses an in-memory or temporary database. **These runs used a mocked model, not the real one.** The one real-model run (R-12) still timed out, and no successful real-model end-to-end run exists.

**Previously failing tests, now fixed.** `OllamaPlanningJobIntegrationTests` (2 tests), `AT03_ConflictPersistenceAndApiTests` and `AT04_InfeasibilityTests` had a stale fixture path: `FindRepositoryRoot` looked for `requirementsforpoc.md` at the repository root, but the file is under `docs/`. The three fixtures now also accept `docs/requirementsforpoc.md` (a test-only change; no application code changed). The earlier failure remains recorded as R-08 in the run register and in `docs/DebugAndTests/AI03_Stage3_PreexistingFailure_OllamaPlanningJobIntegrationTests.txt`.

**Other commands (documented, not run as part of the evidence):**

```powershell
dotnet build src/MaintenancePlanner.Api/MaintenancePlanner.Api.csproj   # executed during development (0 errors)
cd frontend; npm test                                                    # vitest; run in the final verification: 112 tests passed (docs/DebugAndTests/Final_Frontend_Vitest.txt)
dotnet test MaintenancePlanner.slnx                                      # full suite as one command: not issued. The same test methods were run in focused groups (see above).
```

## 7. Sample data, seed and reset

### What the application creates by itself

At startup, `DatabaseInitializer.InitializeAsync` applies pending migrations and then adds the **anchor seed** (`src/MaintenancePlanner.Infrastructure/Seeding/P101AnchorSeed.cs`), only where the records are missing:

- Asset `P-101` (Cooling Water Pump P-101, model CP-100);
- Spare part `BR-101` (bearing, on hand 1), compatible with P-101;
- Three tasks for P-101: `P101-PREP` (60 min), `P101-BEARING-REPLACEMENT` (180 min, crew 2, uses BR-101), `P101-VERIFICATION` (120 min);
- Technicians `TECH-001`, `TECH-002`, `TECH-003` (skills and hourly rates in the seed);
- Two windows in `Asia/Kolkata`: `MW-P101-BASELINE-20261024` (08:00–16:00) and `MW-P101-REDUCED-20261024` (08:00–12:00).

### Loading the five-asset sample dataset

With the API running (see [§5](#5-run-commands)):

```powershell
powershell -File data/fixtures/plant01/import-plant01.ps1 -ApiBase http://localhost:5083
```

**[Documented]** from the script, which posts these files to the FR02 import endpoints in this order: `01-assets.csv` → `/api/imports/assets`; `02-spare-parts.csv` → `/api/imports/spare-parts`; `MAN-PLANT01-SERVICE-001.pdf` → `/api/imports/manuals` (with `documentId` and `version`); `04-tasks.json` → `/api/imports/tasks`; `05-maintenance-history.csv` → `/api/imports/maintenance-history`.

- Imports are insert-only and all-or-nothing per file. Running the script again reports already-existing IDs and changes nothing (the script's own header says so).
- The script stops at the first file that fails, and nothing from that file is saved.
- Technicians and windows are not in this script; they come from the anchor seed.
- An integration test (`FR03FiveAssetPlanningFileDatabaseTests.SampleDataset_ImportsThroughTheImportEndpoints_AndMeetsTheAssignmentMinimums`) runs these imports against a temporary database and asserts the dataset minimums (5 assets, 10–15 tasks, 3 technicians, at least 6 parts, at least 10 history records, 2 windows). The script itself was **not** run for this README.

### Data files

| Path | Contents |
|---|---|
| `data/fixtures/plant01/01-assets.csv` | Assets P-102 to P-105 |
| `data/fixtures/plant01/02-spare-parts.csv` | Spare parts |
| `data/fixtures/plant01/04-tasks.json` | Maintenance tasks |
| `data/fixtures/plant01/05-maintenance-history.csv` | Maintenance history |
| `data/fixtures/plant01/MAN-PLANT01-SERVICE-001.pdf` | Synthetic service manual |
| `data/fixtures/plant01/import-plant01.ps1` | Import script (above) |
| `data/fixtures/p101-anchor.json` | Anchor reference data (not read by the application code reviewed) |
| `data/manuals/MAN-P101-001.txt` | Text manual, source for P-101 tasks |
| `data/manuals/MAN-P101-PLANNING-001.pdf`, `MAN-P102-SCOPE-001.pdf` | Planning and scope manuals (see [data/manuals/README.md](data/manuals/README.md)) |
| `data/fixtures/fr02-demo/` | Valid and invalid CSV fixtures for the import demo (see [its README](data/fixtures/fr02-demo/README.md)) |

### Data dictionary (concise)

The complete dictionary is available in [docs/DATA_DICTIONARY.md](docs/DATA_DICTIONARY.md). Source: `src/MaintenancePlanner.Domain/Entities/`.

| Entity | Key fields |
|---|---|
| `Asset` | `Id`, `PlantId`, `Name`, `Model`, `Priority` |
| `MaintenanceWindow` | `Id`, `Name`, `Start`, `End`, `TimeZoneId` |
| `MaintenanceTaskDefinition` | `Id`, `AssetId`, `Name`, `DurationMinutes`, `RequiredSkills`, `RequiredCrewSize`, `RequiredPartId`, `RequiredPartQuantity`, `PredecessorTaskId`, `SourceDocumentId`, `SourceSection` |
| `Technician` / `TechnicalSkill` / `TechnicianAvailability` | `Id`, `Name`, `HourlyRate`; skill codes; `AvailableFrom`, `AvailableTo` |
| `SparePart` | `Id`, `Name`, `Unit`, `OnHandQuantity`, `ReservedQuantity`; compatibility with assets |
| `MaintenanceHistoryRecord` | `Id`, `AssetId`, `PerformedAt`, `Summary`, `Outcome`, `SourceDocumentId`, `SourceReference` |
| `PlanningJob` | `Id`, `OperationId`, `OriginalJobId`, `Status` (Queued, Running, Succeeded, Failed, Cancelled), `AttemptCount`, `MaxAttempts`, `RequestPayload`, `ResultPayload`, `CheckpointPayload`, `FailureCode`, `ConcurrencyVersion`, lease fields |
| `Draft` | `Id`, `PlanningJobId`, `Status` (Pending, Approved, Cancelled, Superseded), `ApprovedAtUtc`, `ApprovedCandidatePayload` |
| `Reservation` | `Id`, `DraftId`, `SparePartId`, `QuantityReserved`, `Status`, `ConfirmedAtUtc`, `CompletedAtUtc` |
| `LockedTaskAssignment` | `Id`, `DraftId`, `TaskId`, `LockedTechnicianId` |
| `DraftTaskProgress` | `Id`, `DraftId`, `TaskId`, `Status`, `UpdatedAtUtc` |
| `EditedTaskPlacement` | `Id`, `DraftId`, `TaskId`, original and new start, end and technicians, `ValidationStatus`, `ValidationViolations` |
| `BusinessActionRecord` | `Id`, `Action`, `PerformedBy`, `PerformedAtUtc`, `PlanningJobId`, `DraftId`, `TaskId`, `Details` |

### Reset

**There is no reset command.** No script, command or code path recreates a baseline database. The only available approach:

- Work on a **copy** of the database file. Point `ConnectionStrings__MaintenancePlanner` at the copy (see [§4](#4-setup-and-configuration)) before starting the API.
- Recreating an empty database is possible by pointing the connection string at a file that does not yet exist. The startup code would then create the schema and the anchor seed (`DatabaseInitializer`). **This was not executed**, and the five-asset import would have to be run again afterwards.

## 8. Demo workflow and roles

**Roles.** The header of the frontend has a role selector with three options: Planner, Reviewer and Technician (`frontend/src/App.tsx`). The selection is sent as the `X-Actor-Role` header and recorded with each business action. **Roles are self-declared and are not enforced or authenticated** (`src/MaintenancePlanner.Application/History/BusinessActor.cs`). The role is a label in the audit history, not an access control.

**Screens** (`frontend/src/screens/`): Import, Planning request, Alternatives, Approval, Draft detail (with progress, edits, locks, replanning, revision history and action history), and Progress.

**Suggested demonstration order** (follows the implemented flow):

1. **Planner:** import the sample data (see §7), or use the anchor data. Create a request such as "Replace the P-101 bearing." for `P-101` with window `MW-P101-BASELINE-20261024`. The request returns a job ID and is processed in the background.
2. **Planner:** if the window or scope is missing, the job asks a clarifying question (AI01).
3. **Planner:** view the alternatives, with duration, labour cost, deferred tasks and evidence references.
4. **Reviewer:** approve one alternative by its index. Approval revalidates it, reserves `BR-101`, and records the action.
5. **Technician:** update task progress (Not started, In progress, Completed). Completing a part task consumes its reservation.
6. **Planner or Reviewer:** edit a task or lock a technician, then validate and create a revision. Replanning is a new revision linked to the approved plan; the approved plan stays unchanged until the new one is approved.
7. Review the revision history, the action history, and export the approved revision to PDF and JSON (`/api/drafts/{draftId}/export/json` and `/export/pdf`).

**Not yet in the workflow:** a requires-review state, a before-and-after comparison of revisions, and the "Awaiting review" and "Changes requested" states. See [§10](#10-known-limitations-and-unverified-behaviour).

## 9. Troubleshooting

Each entry below is taken from code or a recorded run.

| Symptom | Cause and action |
|---|---|
| Planning job fails with `OLLAMA_UNAVAILABLE` | Ollama is not reachable at `Ollama:BaseUrl`. Start Ollama and check `http://localhost:11434`. The failure is transient and retried once. |
| Planning job fails with `OLLAMA_TIMEOUT` ("did not respond within 180 seconds") | The model did not finish within `Ollama:TimeoutSeconds`. Observed on a CPU-only machine: single requests took 18 s to 262 s, so the job failed after two attempts (`docs/DebugAndTests/AI03_Stage3_RealModelRun.txt`). A GPU or a longer timeout is needed for a successful run. Do not change the timeout merely to pass one run. |
| Planning job fails with `OLLAMA_TOOLS_UNAVAILABLE` | `Ollama:EnableTools` is `true` but no planning tools are registered. Use the default configuration. |
| Planning job fails with `OLLAMA_TOOL_CALL_LIMIT` | The model asked for more than `Ollama:MaxToolCalls` tool calls. This is not retried. Raise the limit only with a reason. |
| Planning job fails with `OLLAMA_INVALID_OUTPUT` | The model's final JSON did not match the schema. This is not retried. |
| Import reports "already exists" | The data was imported before. Imports are insert-only, so nothing changes. |
| The import script stops with a non-200 status | The file that failed was not saved. Earlier files remain imported. Fix the file and run the script again. |
| `DirectoryNotFoundException` from `FindRepositoryRoot` in `OllamaPlanningJobIntegrationTests`, `AT03_ConflictPersistenceAndApiTests` or `AT04_InfeasibilityTests` | A stale fixture path (the requirements file moved to `docs/`). Fixed in the final verification: the three fixtures now accept `docs/requirementsforpoc.md`. On an older checkout, apply the same one-line change (see [§6](#6-tests)). |
| Running the API changes `maintenance-planner.db` | The database at the repository root is **tracked by git**. Running the API writes to it. Use a copy for demonstrations you do not want to keep (see [§7](#7-sample-data-and-seed-or-reset)). |

## 10. Known limitations and unverified behaviour

**AI03 (model tool calling): Partially implemented.**
- **No successful real-model end-to-end run exists.** The one run with `qwen2.5:7b` (E-AI03-08) made valid tool calls, but the job failed with `OLLAMA_TIMEOUT` on CPU-only inference. The configured timeout and tool limit were not changed.
- The saved-candidate approval path is verified with mocked model replies only (E-AI03-11).
- The `validate_schedule` and `calculate_cost` tools do not receive the replanning context (completed tasks and held parts). Only `save_candidate` does.
- Saved candidates exist only for one planning attempt. A retry or checkpoint resume starts without them.
- Tool calls are not recorded in the job result.
- Replacement-job and concurrent approvals of saved candidates are not tested.

**Other requirement gaps** (see [REQUIREMENTS_STATUS.md](docs/REQUIREMENTS_STATUS.md)):
- Change comparison (before and after) between revisions is not implemented (FR07, AI06, feedback point 19).
- The "Awaiting review" and "Changes requested" plan states, and documented state transitions, are not implemented (§4). The Draft statuses are Pending, Approved, Cancelled and Superseded.
- No "requires review" outcome after a resource is lost post-approval (APP02).
- No revise or repair step: validator violations are not sent back to the model (AI04).
- Planning-request submission and clarification are not recorded as business actions (FR09).
- **Exports are incomplete (FR10).** The JSON and PDF exports of an approved draft hold the draft and job IDs, the approval time, the approved task placements (times, technicians, status) and the part and task progress. They do **not** include assumptions, evidence references or deferred work, which the requirement asks for (`Infrastructure/Planning/PlanExportService.cs`).
- The frontend creates a new `Idempotency-Key` for each submission, so a deliberate second submission creates a second job. The submit buttons are disabled while a request is in flight and a browser refresh does not resubmit (read from source; no frontend test covers the disabled state) (APP05).
- The UI has no retry action for failed jobs; it shows the failure code, message and attempt count (APP06).
- The AI model's outputs are only partly explained: the selection rationale is a fixed sentence per policy (AI05).

**Setup and configuration:**
- `maintenance-planner.db` and `maintenance-planner copy.db` are tracked by git even though `.gitignore` lists them.
- No clean-machine setup has been tested. The commands in this README were verified only as noted in each section.
- No reset command exists (see §7).
- No `.env.example` file exists. The configuration keys are documented above instead.
- `appsettings.Development.json` contains a `ConnectionStrings:Maintenance` key that the code does not read.
- `Program.cs` still contains a template endpoint, `/weatherforecast`.
- CORS allows any origin (`src/MaintenancePlanner.Api/FrontendCors.cs`). This is acceptable for a local POC but is not production-safe.
- Labour cost currency is `UNKNOWN` because no currency has been chosen (open questions in `docs/DebugAndTests/Testing_Positivescenario.txt`).
- Role selection is self-declared (see §8).

**Discrepancy with historical documentation.** [docs/scope_assumption.md](docs/scope_assumption.md) (the original proposal) names **Angular** as the frontend technology. The implemented frontend is **React with Vite and TypeScript** (`frontend/package.json`; `frontend/README.md`). The requirements say any suitable web UI is acceptable (§1). The original scope document has been left unchanged.

**Not verified:** the frontend dev, build and preview commands in §5 (the frontend unit tests in §6 were run); the Swagger path; the Vite port; a clean setup; the demonstration flow end to end; and the behaviour of the import script on a fresh database. The demonstration videos and the exported plans are owner-confirmed deliverables whose content was not inspected for this documentation.

## 11. Documentation links

**Requirements and status**
- [docs/requirementsforpoc.md](docs/requirementsforpoc.md): original requirements
- [docs/Feedback.md](docs/Feedback.md): reviewer feedback
- [docs/REQUIREMENTS_STATUS.md](docs/REQUIREMENTS_STATUS.md): status of FR01–FR10, AI01–AI07, APP01–APP06

**Technical documents** (written from source; none of them was runtime-tested when written)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): components, workflow, states, transactions, trade-offs
- [docs/AI_DESIGN.md](docs/AI_DESIGN.md): model, prompts, output schema, retrieval, tools, limits. The recorded real-model run timed out.
- [docs/API.md](docs/API.md): endpoints, shapes, errors, idempotency. Examples are illustrative, not captured responses.
- [docs/DATA_DICTIONARY.md](docs/DATA_DICTIONARY.md): entities, fields, seed and import data, reset position

**Submission documents**
- [docs/SUBMISSION_HANDOVER.md](docs/SUBMISSION_HANDOVER.md): correction report, coverage totals, limitations, repository state, Azure proposal, owner-confirmation items
- [docs/SUBMISSION_GAP_SUMMARY.md](docs/SUBMISSION_GAP_SUMMARY.md): every remaining gap, with the recommended implementation and the evidence needed to close it

**Detailed traceability**
- [docs/FUNCTIONAL_REQUIREMENTS_TRACEABILITY.md](docs/FUNCTIONAL_REQUIREMENTS_TRACEABILITY.md): FR01–FR10
- [docs/AI_REQUIREMENTS_TRACEABILITY.md](docs/AI_REQUIREMENTS_TRACEABILITY.md): AI01–AI07, with the AI03 notes
- [docs/FEEDBACK_TRACEABILITY.md](docs/FEEDBACK_TRACEABILITY.md): feedback points 1–39

**Evidence (selected)**
- [docs/ACCEPTANCE_TEST_REPORT.md](docs/ACCEPTANCE_TEST_REPORT.md): acceptance scenarios AT01–AT12 with statuses, run register and AI evaluation summary
- [docs/DebugAndTests/AI03_ApprovalPath_Verification.txt](docs/DebugAndTests/AI03_ApprovalPath_Verification.txt): saved-candidate approval path (E-AI03-11)
- [docs/DebugAndTests/AI03_Stage4_SaveCandidate_Tests.txt](docs/DebugAndTests/AI03_Stage4_SaveCandidate_Tests.txt): draft saving (E-AI03-10)
- [docs/DebugAndTests/AI03_WindowIdFix_Tests.txt](docs/DebugAndTests/AI03_WindowIdFix_Tests.txt): window-ID prompt fix (E-AI03-09)
- [docs/DebugAndTests/AI03_Stage3_RealModelRun.txt](docs/DebugAndTests/AI03_Stage3_RealModelRun.txt): the real-model run that timed out (E-AI03-08)
- [docs/DebugAndTests/AI03_Stage3_OllamaCompatibilityPreflight.txt](docs/DebugAndTests/AI03_Stage3_OllamaCompatibilityPreflight.txt): Ollama and model metadata (E-AI03-07)
- [docs/Recordings/](docs/Recordings/) and [docs/Screenshots/](docs/Screenshots/): owner-confirmed demonstration recordings and screenshots (not inspected by the assistant)
- Exports: [V1](docs/V1_plan-64a5f783-dd4c-4bd9-a360-0e95526cff44.pdf) and [V2](docs/V2_plan-a6d12df5-3b03-43fb-a640-e6e8d4b72937.pdf) approved plans, with JSON counterparts in `docs/`

**Pending documents:** none of the documents listed in earlier versions of this README (`docs/ARCHITECTURE.md`, `docs/API.md`, `docs/AI_DESIGN.md`, `docs/DATA_DICTIONARY.md`, `docs/SUBMISSION_HANDOVER.md`) is pending any more. **Documents existing does not mean the submission is complete:** the open functional, evidence and administrative gaps are listed in [docs/SUBMISSION_GAP_SUMMARY.md](docs/SUBMISSION_GAP_SUMMARY.md).

**Data**: [data/manuals/README.md](data/manuals/README.md), [data/fixtures/fr02-demo/README.md](data/fixtures/fr02-demo/README.md)
