# API Specification

This is the API specification (DEL06) for the AI Maintenance Planner. It lists every endpoint, its request and response shapes, its status codes, and the behaviour of validation errors, conflicts, idempotency, approval, cancellation and job progress.

It was written from source at HEAD `1376af5` plus uncommitted changes. **Nothing was run in the stage that wrote it:** no test, no API, no Ollama call, no database access. The examples in section 10 are **illustrative: they are derived from the DTO definitions and from the request bodies and assertions in the integration tests, and they are not captured live responses.** Section 11 states exactly what was and was not verified.

Related documents:
- [ARCHITECTURE.md](ARCHITECTURE.md): components, workflow, states, transactions. §6 is the endpoint overview that this note extends.
- [AI_DESIGN.md](AI_DESIGN.md): the model, prompts and tools behind planning jobs
- [DATA_DICTIONARY.md](DATA_DICTIONARY.md): entities and fields, and the import rules (section 8)
- [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md): test status and run register
- [README.md](../README.md): setup and run commands

## 1. Purpose and conventions

The API is a minimal-API ASP.NET Core service (`src/MaintenancePlanner.Api/Endpoints/`). It lets a client queue a planning job, retrieve candidate plans, approve one (which reserves parts), edit, lock and replan an approved plan, record task progress, export the result, read history, and import reference data. There are **32 endpoints** (the template route `GET /weatherforecast` in `Program.cs` is not part of the planner API and is not listed).

| Item | Value | Source |
|---|---|---|
| Local Development URL | `http://localhost:5083` (profile `http`). Profile `https` lists `https://localhost:7079;http://localhost:5083`. | `Properties/launchSettings.json` (configuration, not a running check) |
| Frontend base URL | `VITE_API_BASE`, default `http://localhost:5083` | `frontend/src/App.tsx` |
| Version prefix | **None.** Routes start with `/api/`. No API version header or route segment exists. | Endpoint source |
| Request bodies | JSON (`application/json`), except imports (`multipart/form-data`) | Endpoint source |
| JSON naming | camelCase. The code sets no custom JSON options, and the tests send camelCase bodies. | `Program.cs`; tests |
| Enumerations in responses | Names as strings (for example `"Queued"`, `"Approved"`, `"InProgress"`), because the response records carry `.ToString()` values | Endpoint source |
| Timestamps | ISO 8601 `DateTimeOffset`. Request timestamps keep their offset. | DTOs |
| Identifiers | Planning-job and draft routes use `:guid` route constraints, so a non-GUID there does not match a route (framework behaviour, not tested here). `{taskId}`, `{assetId}` and `{dataset}` are free strings. | Endpoint source |
| OpenAPI | In the `Development` environment only: `app.MapOpenApi()` and Swagger (`/swagger/v1/swagger.json`, UI via `UseSwaggerUI`). Each endpoint carries a name, summary and `Produces` metadata. No OpenAPI file is committed. | `Program.cs` |
| Not in the API | `GET /weatherforecast` (template route) | `Program.cs` |

## 2. Authentication, authorization and the actor role

- **There is no authentication and no authorization.** `Program.cs` registers neither; no endpoint requires a credential or checks a role.
- **Actor role.** A middleware (`UseBusinessActorFromHeader`) reads the `X-Actor-Role` header on every request. The accepted values are `planner`, `reviewer` and `technician` (case-insensitive). Anything else, or no header, becomes `unspecified`. The role is stored as `PerformedBy` on business-action records and returned by the history endpoint. It is **self-declared and not verified**, and it does not restrict any operation (`BusinessActor.cs`).
- **CORS.** Any origin, any method and any header are allowed, and the `Location` header is exposed so a browser can read the draft ID from an approval response (`FrontendCors.cs`).

## 3. Error conventions

There is no single error format. The shape depends on the endpoint. No global exception handler or problem-details service is registered in `Program.cs`, so an unhandled exception produces the framework default (this was not exercised).

| Shape | Used by | Content |
|---|---|---|
| `ProblemDetails` | `POST /api/planning-jobs` (400, 409) | `title`, `status`, `detail`, and any other fields the framework adds |
| Plain string body | Clarify, replan, candidates, task-edits revision (400), lock `PUT` (400), mark-task-complete (400, 409), task-progress (400), export (409) | A message |
| `ApprovalErrorResponse` | Approve and cancel (409) | `resourceType`, `reason`, `resourceId`, `message` |
| `TaskLockChangeResult` | Lock `PUT` and `DELETE` (404, 409) | `succeeded`, `failureCode`, `failureReason` |
| `TaskStatusUpdateResponse` | Task progress (404, 409) | `succeeded`, `taskId`, `status`, `failureCode`, `failureReason` |
| `EditRevisionResult` | Task-edits revision (409) | `succeeded`, `planningJobId`, `failureCode`, `failureReason`, `violations` |
| `ImportReport` | Imports (200, 400, 422) | See section 9 |
| Empty body | Most `404` responses; task-edits submit and validate (`409`) | None |

## 4. Endpoint reference

Status codes are those the source produces. Where a row says "string", the body is a plain message. `{id}` is a GUID.

### 4.1 Reference data (read-only)

| Method and route | Purpose | Success | Errors |
|---|---|---|---|
| `GET /api/assets` | List assets | `200` `AssetResponse[]` | none |
| `GET /api/technicians` | List technicians with skills and availability | `200` `TechnicianResponse[]` | none |
| `GET /api/spareparts` | List spare parts and stock | `200` `SparePartResponse[]` | none |
| `GET /api/maintenance-windows` | List maintenance windows | `200` `MaintenanceWindowResponse[]` | none |
| `GET /api/task-catalogue/{assetId}` | An asset's task catalogue, in order | `200` `MaintenanceTaskResponse[]` | `404` unknown asset |

### 4.2 Planning jobs

| Method and route | Purpose | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/planning-jobs` | Queue a planning job | Header `Idempotency-Key` (non-empty GUID, required). Body `{ requestText, assetIds, windowId? }`. | `202` `{ jobId, statusUrl }`, with a `Location` header equal to `statusUrl` | `400` ProblemDetails for a missing or invalid key, or an invalid body. `409` ProblemDetails when the key was used with a different request. |
| `GET /api/planning-jobs/{id}` | Job status and result | none | `200` `PlanningJobStatusResponse` | `404` |
| `POST /api/planning-jobs/{id}/clarify` | Answer a clarification and re-queue the job | Body `{ windowId, additionalContext? }`. `windowId` is required. | `200` `PlanningJobStatusResponse` (status `Queued`) | `404`; `400` string: no result yet, outcome is not `ClarificationRequired`, `windowId` blank, or the job could not be updated |
| `POST /api/planning-jobs/{originalJobId}/replan` | Create a replacement job for an approved plan | Header `Idempotency-Key` (required). Body as for create. | `202` `{ newJobId, statusUrl }` | `404` original job not found. **`409` string** when the original job has no approved draft, when the key is missing or invalid, when the body is invalid, or when the key was used with a different request. |
| `GET /api/planning-jobs/{jobId}/revisions` | The revision chain of a job | none | `200` `PlanningJobRevisionResponse[]`, ordered by creation | `404` when the chain is empty |
| `GET /api/planning-jobs/{jobId}/candidates` | Candidate plans of a completed job | none | `200` `PlanningJobCandidatesResponse` | `404`; `400` string while `Queued` or `Running`, for a `Failed` job, with no result, for outcome `ClarificationRequired`, for any outcome other than `CandidatesGenerated`, or if the result cannot be parsed |
| `GET /api/planning-jobs/{jobId}/history` | Business actions across the revision chain, oldest first | none | `200` `BusinessActionResponse[]` | `404` |

`clarify` ignores `additionalContext`: the handler reads only `windowId` (`PlanningJobEndpoints.cs`).

### 4.3 Drafts: approval, cancellation, retrieval

| Method and route | Purpose | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/drafts/{planningJobId}/approve` | Approve one candidate of a job and reserve parts | Optional body `{ candidateIndex? }` (zero-based) | `202` with no body and `Location: /api/drafts/{draftId}` | `409` `ApprovalErrorResponse`. Every approval failure is a `409`, including an unknown job (`PLANNING_JOB_NOT_FOUND`). |
| `POST /api/drafts/{draftId}/cancel` | Cancel an approved draft and release unconsumed parts | none | `204` | `404` draft not found; `409` `ApprovalErrorResponse` |
| `GET /api/drafts/{draftId}` | Draft detail | none | `200` `DraftDetailResponse` | `404` |
| `GET /api/drafts` | All saved drafts, newest first | none | `200` `SavedPlanResponse[]` | none |

A job that is itself a replacement is approved through the same route: the service supersedes the original approved draft in the same transaction (`ApprovalEndpoints.cs`).

### 4.4 Task edits and revisions

| Method and route | Purpose | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/drafts/{draftId}/task-edits` | Submit edits for tasks of an approved draft | `{ edits: [ { taskId, newStart, newEnd, newTechnicianIds } ] }` | `200` `TaskEditsResponse` | `404`; `409` empty body (draft not approved) |
| `GET /api/drafts/{draftId}/task-edits` | List a draft's edits | none | `200` `EditedTaskPlacementResponse[]` | `404` |
| `DELETE /api/drafts/{draftId}/task-edits/{taskId}` | Delete one edit | none | `204` | `404` draft or edit not found |
| `DELETE /api/drafts/{draftId}/task-edits` | Delete all edits | none | `204` | `404` |
| `POST /api/drafts/{draftId}/task-edits/validate` | Validate all edits and store the result per edit | none | `200` `EditValidationResult` | `404`; `409` empty body (draft not approved) |
| `POST /api/drafts/{draftId}/task-edits/revision` | Create a reviewable revision from the edits | Header `Idempotency-Key` (required) | `202` `{ newJobId, statusUrl }` | `400` string for a missing or invalid key; `404` draft not found; `409` `EditRevisionResult` (no edits, invalid edits with every violation, or other failure; nothing is created) |

The revision is a replacement job with the edited plan as its only candidate. Review it with `GET /api/planning-jobs/{newJobId}/candidates` and approve it with `POST /api/drafts/{newJobId}/approve`, which supersedes the draft (endpoint description in `EditEndpoints.cs`).

### 4.5 Task locks

| Method and route | Purpose | Request | Success | Errors |
|---|---|---|---|---|
| `GET /api/drafts/{draftId}/locks` | Locks of a draft, ordered by task ID | none | `200` `TaskLockResponse[]` | `404` |
| `PUT /api/drafts/{draftId}/locks/{taskId}` | Lock a task to a technician | `{ technicianId }` (required) | `204` | `400` string if `technicianId` is blank; `404` and `409` `TaskLockChangeResult` |
| `DELETE /api/drafts/{draftId}/locks/{taskId}` | Remove a lock | none | `204`. Removing a lock that does not exist also succeeds. | `404` and `409` `TaskLockChangeResult` |

`404` is returned for `DRAFT_NOT_FOUND` and `TASK_NOT_IN_PLAN`; every other failure code is `409`.

### 4.6 Progress and export

| Method and route | Purpose | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/drafts/{draftId}/tasks/{taskId}/progress` | Move one task forward | `{ status }`, one of `NotStarted`, `InProgress`, `Completed` (case-insensitive; numeric strings rejected) | `200` `TaskStatusUpdateResponse` | `400` string for an invalid status; `404` `TaskStatusUpdateResponse` for `DRAFT_NOT_FOUND` or `TASK_NOT_IN_PLAN`; `409` `TaskStatusUpdateResponse` for `DRAFT_NOT_APPROVED` or `INVALID_TASK_TRANSITION` |
| `POST /api/drafts/{draftId}/mark-task-complete?sparePartId=...` | Mark one reservation of a spare part as consumed per call (the endpoint's description text says "all"; the service code marks one) | Query parameter `sparePartId` (required) | `200` `TaskCompletionResponse`. **A service failure is still `200`, with `succeeded` false and `failureReason`** (no reservation for the part, or all already consumed). | `400` string if `sparePartId` is blank; `404`; `409` string "Draft must be approved to mark tasks complete" |
| `GET /api/drafts/{draftId}/progress` | Quantities and task statuses | none | `200` `PlanProgressResponse` | `404` |
| `GET /api/drafts/{draftId}/export/json` | Export the approved draft | none | `200` file `application/json`, named `plan-{draftId without hyphens}.json` | `404`; `409` string "Only approved drafts can be exported" (or the service's message) |
| `GET /api/drafts/{draftId}/export/pdf` | Export the approved draft | none | `200` file `application/pdf`, named `plan-{draftId without hyphens}.pdf` | Same as JSON |

**Export content (read from `Infrastructure/Planning/PlanExportService.cs`).** The JSON (camelCase, indented) has `draftId`, `approvedAtUtc`, `planningJobId`, `progress` (`totalRequired`, `completedQuantity`, `remainingQuantity`, `percentComplete`), `tasks` (per spare part: `sparePartId`, `requiredQuantity`, `completedQuantity`, `remainingQuantity`) and `taskPlacements` (`taskId`, `start`, `end`, `technicianIds`, `status`, `statusUpdatedAtUtc`). The PDF lists the draft and job IDs, the status "Approved" and each placement, and then embeds the same JSON text. **The export does not contain assumptions, evidence references or deferred work** (requirement FR10).

### 4.7 Imports

| Method and route | Purpose | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/imports/{dataset}` | Import a structured dataset | `multipart/form-data`, field `file` (`.csv` or `.json`, at most 10 MB). `{dataset}` is one of `assets`, `spare-parts`, `technicians`, `technician-availability`, `tasks`, `maintenance-history`. | `200` `ImportReport` | `422` `ImportReport` (nothing saved); `400` `ImportReport` for a non-multipart request, a missing `file`, an oversize file, a wrong extension or an unknown dataset |
| `POST /api/imports/manuals` | Import a text-based PDF manual | `multipart/form-data`: `file` (`.pdf`, at most 20 MB), `documentId`, `version` | `200` `ImportReport` | `422` `ImportReport`; `400` `ImportReport` |

The columns, validation rules and error messages of each dataset are in [DATA_DICTIONARY.md](DATA_DICTIONARY.md) section 8. Imports are insert-only and all-or-nothing.

### 4.8 Route precedence

`POST /api/imports/manuals` is registered before `POST /api/imports/{dataset}` so the literal route wins. The dataset route's error message for an unknown dataset lists the supported slugs and `manuals`.

## 5. Response shapes

Field names are camelCase on the wire. `?` means nullable.

| Shape | Fields |
|---|---|
| `PlanningJobAcceptedResponse` | `jobId`, `statusUrl` |
| `ReplanAcceptedResponse` | `newJobId`, `statusUrl` |
| `PlanningJobStatusResponse` | `jobId`, `status`, `attemptCount`, `maxAttempts`, `currentStep`, `resultPayload?`, `clarificationPayload?`, `failureCode?`, `createdAtUtc`, `startedAtUtc?`, `completedAtUtc?`, `errorMessage?` |
| `PlanningJobRevisionResponse` | `id`, `status`, `createdAtUtc`, `originalJobId?` |
| `PlanningJobCandidatesResponse` | `jobId`, `status`, `outcome`, `candidates[]`, `interpretation?` |
| `CandidateResponse` | `rationale`, `requestedTaskIds[]`, `dependencyTaskIds[]`, `taskPlacements[]`, `evidenceReferences[]` (strings), `totalLabourCostAmount`, `totalLabourCostCurrency`, `completionTimeMinutes`, `deferralReasons[]` (`{ taskId, reason }`), `isLowestCostCandidate`, `costComparabilityWarning?` |
| `InterpretationResponse` | `maintenanceIntent`, `scope`, `proposedAssetIds[]`, `proposedTaskIds[]` |
| `DraftDetailResponse` | `draftId`, `planningJobId`, `status`, `createdAtUtc`, `approvedAtUtc?`, `selectedCandidate?`, `reservations[]`, `interpretation?` |
| `ReservationResponse` | `reservationId`, `sparePartId`, `quantityReserved`, `status`, `createdAtUtc`, `confirmedAtUtc?` |
| `SavedPlanResponse` | `draftId`, `planningJobId`, `originalJobId?`, `status`, `createdAtUtc`, `approvedAtUtc?`, `assetIds[]`, `windowId?`, `requestText?` |
| `BusinessActionResponse` | `id`, `action`, `performedBy`, `performedAtUtc`, `planningJobId?`, `draftId?`, `taskId?`, `details?` |
| `TaskEditsResponse` | `draftId`, `editedTasks[]` |
| `EditedTaskPlacementResponse` | `taskId`, `originalStart`, `originalEnd`, `originalTechnicianIds[]`, `newStart`, `newEnd`, `newTechnicianIds[]`, `validationStatus` |
| `EditValidationResult` | `draftId`, `isValid`, `violations[]` (`{ taskId, code, message }`), `validEditsCount`, `invalidEditsCount` |
| `TaskLockResponse` | `taskId`, `technicianId` |
| `TaskLockChangeResult` | `succeeded`, `failureCode?`, `failureReason?` |
| `TaskStatusUpdateResponse` | `succeeded`, `taskId`, `status?`, `failureCode?`, `failureReason?` |
| `TaskCompletionResponse` | `succeeded`, `failureReason?` |
| `PlanProgressResponse` | `draftId`, `totalRequired`, `completedQuantity`, `remainingQuantity`, `tasks[]` (`{ sparePartId, requiredQuantity, completedQuantity, remainingQuantity }`), `taskStatuses[]` (`{ taskId, status, updatedAtUtc? }`) |
| `ApprovalErrorResponse` | `resourceType`, `reason`, `resourceId?`, `message` |
| `EditRevisionResult` | `succeeded`, `planningJobId?`, `failureCode?`, `failureReason?`, `violations?[]` |
| `AssetResponse` | `id`, `plantId`, `name`, `model`, `priority` |
| `TechnicianResponse` | `id`, `name`, `hourlyRate`, `skills[]`, `availability[]` (`{ availableFrom, availableTo }`) |
| `SparePartResponse` | `id`, `name`, `unit`, `onHandQuantity`, `reservedQuantity`, `availableQuantity` |
| `MaintenanceWindowResponse` | `id`, `name`, `start`, `end`, `timeZoneId` |
| `MaintenanceTaskResponse` | `id`, `assetId`, `name`, `durationMinutes`, `requiredSkills`, `requiredCrewSize`, `requiredPartId?`, `requiredPartQuantity`, `predecessorTaskId?`, `sourceDocumentId`, `sourceSection` |

**Notes on specific fields (from source):**
- `resultPayload` and `clarificationPayload` are **JSON text inside a string**, not nested objects. A client parses them. `resultPayload` is an envelope `{ "outcome": ..., "result": ... }`.
- The `outcome` is one of `CandidatesGenerated`, `ClarificationRequired`, `Infeasible`, `InvalidInput`, `DataUnavailable`, `SearchLimitReached` (`PlanningJobProcessor.cs`, `PlanningJobEndpoints.cs`).
- `clarificationPayload` holds the job's previous result (the clarification-required result) after a `clarify` call resets the job (`PlanningJobs.UpdateAndResetAsync`).
- `taskPlacements` is typed as a list of objects. Its items carry the placement fields `taskId`, `start`, `end` and `technicianIds` (`PlanningTaskPlacement`).
- `evidenceReferences` is a list of **strings** in both candidate responses. In `GET .../candidates` each entry is the reference's `sourceId`. In `GET /api/drafts/{draftId}` an entry that is an object in the stored snapshot is returned as its raw JSON text. The full reference fields are in [AI_DESIGN.md](AI_DESIGN.md) section 4.2.
- Draft `status` is `Approved`, `Cancelled` or `Superseded` in practice. `Pending` exists in the enumeration but is never assigned ([DATA_DICTIONARY.md](DATA_DICTIONARY.md) section 6).
- `ValidationStatus` of an edit is free text: `pending`, `valid` or `invalid`.
- `selectedCandidate` in the draft detail is the candidate snapshot stored at approval. If a draft predates snapshots, the first candidate of the job result is returned instead (code comment in `ApprovalEndpoints.cs`).

## 6. Job status and progress

`GET /api/planning-jobs/{id}` is the polling endpoint. `status` is one of `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled` (the last is never assigned).

| Field | Meaning |
|---|---|
| `status` | `Queued` until a worker claims the job; `Running` while it is processed; `Succeeded` once a business result is stored (this includes outcomes such as `ClarificationRequired` and `Infeasible`); `Failed` after a technical failure or exhausted attempts |
| `attemptCount`, `maxAttempts` | Attempts made and allowed (default maximum 2) |
| `currentStep` | A text step label (default `Queued`) |
| `failureCode`, `errorMessage` | Set for a failed job. Codes include the `OLLAMA_*` codes in [AI_DESIGN.md](AI_DESIGN.md) section 7.3 and the job codes in [ARCHITECTURE.md](ARCHITECTURE.md) §9. |
| `createdAtUtc`, `startedAtUtc`, `completedAtUtc` | Lifecycle timestamps |

**Typical flow (derived from the endpoints):**
1. `POST /api/planning-jobs` with an `Idempotency-Key`, then poll `statusUrl`.
2. If `status` is `Succeeded` and the outcome is `ClarificationRequired`, call `POST .../clarify` with a `windowId`; the job returns to `Queued`.
3. If the outcome is `CandidatesGenerated`, call `GET .../candidates`, then `POST /api/drafts/{jobId}/approve` with the chosen `candidateIndex`.
4. Use `GET /api/drafts/{draftId}` (the draft ID is in the approval `Location` header), then the progress, lock, edit and export endpoints.
5. To change an approved plan, call `POST .../replan` or the edit revision endpoint, review the new job's candidates, and approve it; the earlier draft becomes `Superseded`.

`GET /api/drafts/{draftId}/progress` returns spare-part quantities (`totalRequired`, `completedQuantity`, `remainingQuantity` and one entry per part) and the status of every planned task in `taskStatuses`. Task statuses are `NotStarted`, `InProgress`, `Completed`, moving forward only.

## 7. Validation failures

| Endpoint | Condition | Response |
|---|---|---|
| `POST /api/planning-jobs` | `Idempotency-Key` missing, repeated as multiple values, not a GUID, or an empty GUID | `400` ProblemDetails, title "Invalid idempotency key" |
| `POST /api/planning-jobs` | `requestText` blank; `assetIds` empty or containing a blank entry | `400` ProblemDetails, title "Invalid planning job request", with the messages joined in `detail` |
| `POST /api/planning-jobs` | Body is `null`, an array, a number, `{}`, or omits `requestText` or `assetIds` | `400` (asserted by test; the body was not asserted) |
| `POST .../clarify` | `windowId` blank; job has no result or is not `ClarificationRequired` | `400` string |
| `POST .../replan` | Header or body invalid | **`409` string** (not `400`) |
| `POST .../task-edits/revision` | Header missing or invalid | `400` string "Provide a non-empty GUID in the Idempotency-Key header." |
| `PUT .../locks/{taskId}` | `technicianId` blank | `400` string "technicianId is required" |
| `POST .../tasks/{taskId}/progress` | `status` missing or not a status name | `400` string "status must be one of NotStarted, InProgress or Completed" |
| `POST .../mark-task-complete` | `sparePartId` blank | `400` string "sparePartId is required" |
| Imports | Wrong extension, no file, oversize, unknown dataset, not multipart | `400` `ImportReport` with one file-level issue |
| Imports | Any invalid record, header or manual | `422` `ImportReport` listing every issue; nothing is saved |

## 8. Conflicts and idempotency

### 8.1 `409` conflict codes

| Area | Codes (`reason` or `failureCode`) |
|---|---|
| Approval (`ApprovalErrorResponse.reason`) | `PLANNING_JOB_NOT_FOUND`, `NOT_A_REPLACEMENT_JOB`, `PLANNING_JOB_NO_RESULT`, `PLANNING_RESULT_UNREADABLE`, `PLANNING_JOB_NOT_APPROVABLE`, `PLANNING_JOB_NO_CANDIDATES`, `CANDIDATE_SELECTION_REQUIRED`, `CANDIDATE_NOT_FOUND`, `CANDIDATE_SELECTION_MISMATCH`, `CANDIDATE_UNREADABLE`, `PLANNING_REQUEST_INCOMPLETE`, `CANDIDATE_HAS_NO_TASKS`, `NO_APPROVED_ORIGINAL_DRAFT`, `CANDIDATE_NO_LONGER_VALID`, `TECHNICIAN_TIME_CONFLICT`, `INSUFFICIENT_INVENTORY`, `SPARE_PART_NOT_FOUND`, `APPROVED_SNAPSHOT_UNAVAILABLE`, `DRAFT_CANCELLED`, `DRAFT_SUPERSEDED`, `INVALID_DRAFT_STATE`, `CONCURRENT_DRAFT_CREATION`, `CONCURRENT_MODIFICATION` |
| Cancellation (`ApprovalErrorResponse.reason`) | `INVALID_DRAFT_STATE`, `SPARE_PART_NOT_FOUND` |
| Locks (`failureCode`) | `404`: `DRAFT_NOT_FOUND`, `TASK_NOT_IN_PLAN`. `409`: `DRAFT_NOT_APPROVED`, `TASK_IN_PROGRESS`, `TASK_ALREADY_COMPLETED`, `LOCKED_TECHNICIAN_NOT_FOUND`, `LOCKED_TECHNICIAN_UNQUALIFIED`, `LOCKED_TECHNICIAN_UNAVAILABLE` |
| Task progress (`failureCode`) | `404`: `DRAFT_NOT_FOUND`, `TASK_NOT_IN_PLAN`. `409`: `DRAFT_NOT_APPROVED`, `INVALID_TASK_TRANSITION` |
| Edit revision (`failureCode`) | `DRAFT_NOT_APPROVED`, `NO_TASK_EDITS`, `TASK_EDITS_INVALID` (with `violations`), `APPROVED_SNAPSHOT_UNAVAILABLE`, `OPERATION_ID_REUSED`. `DRAFT_NOT_FOUND` gives `404`. |
| Edit violations (`violations[].code`) | `TASK_NOT_IN_PLAN`, `TASK_ALREADY_COMPLETED`, `TASK_IN_PROGRESS`, `LOCKED_ASSIGNMENT_CHANGED`, `NO_REMAINING_WORK`, plus the schedule-validation codes in [AI_DESIGN.md](AI_DESIGN.md) section 6.5 |
| Job creation and replan | An `Idempotency-Key` reused with a different request: `409` (ProblemDetails on create, string on replan) |
| Export, mark-task-complete | A draft that is not approved: `409` string |

When a task in progress on the replaced draft is not preserved by the candidate, the conflict is `CANDIDATE_NO_LONGER_VALID` with `resourceType` `Candidate` and `resourceId` `IN_PROGRESS_TASK_NOT_PRESERVED` (`DraftApprovalService.cs`).

The `resourceType` values observed in tests are `SparePart`, `Draft` and `PlanningJob`; tests assert the `reason`, and for these three cases the `resourceType` too (`ApprovalConflictResponseTests`). Other `resourceType` values were not enumerated.

### 8.2 Idempotency

| Operation | Mechanism | Repeated request |
|---|---|---|
| `POST /api/planning-jobs` | `Idempotency-Key` header, stored as the job's `OperationId` (unique index), plus a fingerprint of the request | Same key and same request: `202` with the existing job (test: repeated and concurrent submissions create one job). Same key, different request: `409`. |
| `POST .../replan` | Same | Same |
| `POST .../task-edits/revision` | `Idempotency-Key`; reuse is reported as `OPERATION_ID_REUSED` in the failure codes | Not asserted beyond the code list |
| `POST /api/drafts/{jobId}/approve` | At most one draft per job (unique index). No header. | A repeat request returns `202` for the same candidate. A different `candidateIndex` returns `409 CANDIDATE_SELECTION_MISMATCH`. A cancelled or superseded draft returns `409` (`DRAFT_CANCELLED`, `DRAFT_SUPERSEDED`). |
| `POST /api/drafts/{id}/cancel` | Status check | An already cancelled draft succeeds (`204`) |
| `PUT` and `DELETE .../locks/{taskId}` | Existing-lock check | Same technician succeeds; deleting a missing lock succeeds |
| `POST .../tasks/{taskId}/progress` | Forward-only transitions | A repeat is `409 INVALID_TASK_TRANSITION` |
| Imports | Insert-only | A repeat is `422` with "already exists" issues |

**Version conflicts.** The API has no version or `ETag` header and no `If-Match`. The optimistic-concurrency token of a planning job (`ConcurrencyVersion`) is internal and is not in any response. A concurrent change to the same draft surfaces as a `409` approval conflict (`CONCURRENT_DRAFT_CREATION`, `CONCURRENT_MODIFICATION`).

### 8.3 Approval and cancellation behaviour

Summarised here; the step-by-step checks and transaction boundaries are in [ARCHITECTURE.md](ARCHITECTURE.md) §5.
- **Approve** requires a job that `Succeeded` with outcome `CandidatesGenerated`. The `candidateIndex` may be omitted only when exactly one candidate exists; otherwise `409 CANDIDATE_SELECTION_REQUIRED`. An out-of-range or negative index is `409 CANDIDATE_NOT_FOUND` (asserted by `F02SelectedAlternativeApprovalTests`). The candidate is revalidated against current data, parts are reserved, and the draft is created `Approved`, in one transaction. Success is `202` with the draft ID in `Location`.
- **Cancel** releases the draft's unconsumed reservations and marks the draft `Cancelled`; success is `204`.

## 9. Import report

All import responses use `ImportReport`:

| Field | Meaning |
|---|---|
| `dataset` | The dataset slug (or `manuals`) |
| `format` | `csv`, `json`, `pdf`, or `unknown` for early rejections |
| `fileName` | The uploaded file name, or null |
| `imported` | True only if every record was saved |
| `totalRecords`, `importedRecords`, `invalidRecords` | Counts. `invalidRecords` counts distinct record numbers with an issue. |
| `issues[]` | `{ recordNumber?, location, recordId?, field?, message }`. `recordNumber` is the CSV line (header is line 1), the one-based JSON array position, or the one-based PDF page. File-level issues have no number and `location` `file`. |

## 10. Examples (illustrative, not captured)

These examples show the shapes. **They are not live responses.** Identifiers follow the seed data. Values marked `<...>` are placeholders. Request bodies follow what the integration tests send.

**Queue a planning job** (`POST /api/planning-jobs`, header `Idempotency-Key: 6f1c2d3e-0000-4000-8000-00000000a001`, a placeholder GUID):

```json
{
  "requestText": "Plan full bearing replacement for P-101.",
  "assetIds": ["P-101"],
  "windowId": "MW-P101-BASELINE-20261024"
}
```

Response `202`, with a `Location` header equal to `statusUrl`:

```json
{
  "jobId": "<job-guid>",
  "statusUrl": "/api/planning-jobs/<job-guid>"
}
```

**Job status while queued** (`GET /api/planning-jobs/{id}`; the shape is from `PlanningJobStatusResponse`, and a test asserts the `Queued` status and null timestamps):

```json
{
  "jobId": "<job-guid>",
  "status": "Queued",
  "attemptCount": 0,
  "maxAttempts": 2,
  "currentStep": "Queued",
  "resultPayload": null,
  "clarificationPayload": null,
  "failureCode": null,
  "createdAtUtc": "<timestamp>",
  "startedAtUtc": null,
  "completedAtUtc": null,
  "errorMessage": null
}
```

**Missing idempotency key** (`400`, `ProblemDetails`; fields from the endpoint code, framework fields such as `type` may also appear):

```json
{
  "title": "Invalid idempotency key",
  "status": 400,
  "detail": "Provide a non-empty GUID in the Idempotency-Key header."
}
```

**Candidates** (`GET /api/planning-jobs/{jobId}/candidates`, abbreviated; the placement times and cost are placeholders):

```json
{
  "jobId": "<job-guid>",
  "status": "Succeeded",
  "outcome": "CandidatesGenerated",
  "candidates": [
    {
      "rationale": "<text>",
      "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
      "dependencyTaskIds": ["P101-PREP", "P101-VERIFICATION"],
      "taskPlacements": [
        { "taskId": "P101-PREP", "start": "<ISO 8601>", "end": "<ISO 8601>", "technicianIds": ["TECH-001"] }
      ],
      "evidenceReferences": ["P-101", "MW-P101-BASELINE-20261024"],
      "totalLabourCostAmount": 0.0,
      "totalLabourCostCurrency": "UNKNOWN",
      "completionTimeMinutes": 0,
      "deferralReasons": [],
      "isLowestCostCandidate": true,
      "costComparabilityWarning": null
    }
  ],
  "interpretation": {
    "maintenanceIntent": "<text>",
    "scope": "<text>",
    "proposedAssetIds": ["P-101"],
    "proposedTaskIds": ["P101-BEARING-REPLACEMENT"]
  }
}
```

**Approve candidate 1** (`POST /api/drafts/{planningJobId}/approve`):

```json
{ "candidateIndex": 1 }
```

Response `202`, no body, header `Location: /api/drafts/<draft-guid>`.

**Approval conflict** (`409`; the `reason` and `resourceType` pair is asserted by `ApprovalConflictResponseTests` for a service-level result; `resourceId` and `message` are placeholders):

```json
{
  "resourceType": "SparePart",
  "reason": "INSUFFICIENT_INVENTORY",
  "resourceId": "<part-id>",
  "message": "<text>"
}
```

**Lock a task** (`PUT /api/drafts/{draftId}/locks/{taskId}`):

```json
{ "technicianId": "TECH-002" }
```

Response `204`. A rejected lock returns `409` with:

```json
{
  "succeeded": false,
  "failureCode": "LOCKED_TECHNICIAN_UNQUALIFIED",
  "failureReason": "<text>"
}
```

**Move a task forward** (`POST /api/drafts/{draftId}/tasks/{taskId}/progress`):

```json
{ "status": "InProgress" }
```

Response `200`: `{ "succeeded": true, "taskId": "P101-PREP", "status": "InProgress", "failureCode": null, "failureReason": null }`. A skipped or repeated step returns `409` with `succeeded` false and `failureCode` `INVALID_TASK_TRANSITION`.

**Submit a task edit** (`POST /api/drafts/{draftId}/task-edits`; the body shape follows the test fixture):

```json
{
  "edits": [
    {
      "taskId": "P101-PREP",
      "newStart": "<ISO 8601 with offset>",
      "newEnd": "<ISO 8601 with offset>",
      "newTechnicianIds": ["TECH-001"]
    }
  ]
}
```

**Import an asset file** (`POST /api/imports/assets`, `multipart/form-data`, field `file`, an invalid CSV). Response `422`, abbreviated; the messages are those listed in `data/fixtures/fr02-demo/README.md`, which derives them from the validator code and not from a recorded run:

```json
{
  "dataset": "assets",
  "format": "csv",
  "fileName": "fr02-invalid-assets.csv",
  "imported": false,
  "totalRecords": 5,
  "importedRecords": 0,
  "invalidRecords": 4,
  "issues": [
    { "recordNumber": 3, "location": "row 3", "recordId": "DEMO-FR02-B02", "field": "priority", "message": "priority must be between 1 and 99, but was 0." }
  ]
}
```

## 11. Verification status

| Level | Status |
|---|---|
| **Source inspection** (this stage) | All 32 routes, their request and response records, route constraints, status codes and error shapes were read from `src/MaintenancePlanner.Api/` and the `Application` contracts. The conflict codes come from the service constants and `DraftApprovalService.cs`. |
| **Automated tests (source)** | Integration test files exist that reference each route and assert HTTP status codes (for example `PlanningJobEndpointsTests`, `F02SelectedAlternativeApprovalTests`, `FR05EditsAndLocksFileDatabaseTests`, `FR02DataImportFileDatabaseTests`, `AT14CandidateRetrievalTests`, `AT15DraftDetailTests`, `F06ConsumptionAndExportFileDatabaseTests`, `MaintenancePlannerReadEndpointsTests`). This mapping is by file (route text and status assertions in the same file), not by individual assertion. |
| **Captured test evidence** | Only the runs in the [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md) run register count. Runs R-13 to R-20 (2026-10-29) passed the integration test classes that exercise these routes in process, over HTTP, with isolated databases and a mocked model (50, 233 and 38 integration tests, among others). The register now shows 10 scenarios Pass and 2 Partial. Before those runs, 0 scenarios were Pass. An individual status code in this document is supported only if a passing test in those logs asserts it; the route-to-test mapping was done by file, not by assertion. |
| **Live API verification** | **None in this stage.** The only recorded live-API use is R-12 (one real planning request against a database copy). The job failed with `OLLAMA_TIMEOUT`, so it did not exercise approval, edits, locks, progress, export or imports. Screenshots and recordings may show other calls; they were not inspected. |
| **OpenAPI document** | Not generated or compared. The Swagger and OpenAPI paths are from configuration, not from a check. |

**Uncertain or unverified:**
- The exported JSON and PDF were read from source only (the export content paragraph under section 4.6); no exported file was opened.
- The exact `resourceType` and `message` of most approval conflicts (only `SparePart/INSUFFICIENT_INVENTORY`, `Draft/DRAFT_CANCELLED`, `PlanningJob/PLANNING_JOB_NOT_FOUND` and `Draft/INVALID_DRAFT_STATE` are asserted at the service level).
- The body of `400` responses to a malformed or non-object JSON body (tests assert the status only).
- Behaviour of a non-GUID in a `{id:guid}` route, and of an unhandled exception (framework defaults, not exercised).
- The exact fields of the stored `taskPlacements` objects beyond the four placement fields, and the wire casing of the stored result payload (the payload is written by the job processor).
- The Swagger UI path and the default OpenAPI document path.
- Request-size limits other than the 10 MB and 20 MB import limits.
