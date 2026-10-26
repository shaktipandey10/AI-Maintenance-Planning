# System Design and Architecture
## AI Maintenance Planner

**Date:** 2026-10-26  
**Scope:** Component architecture, workflows, Ollama integration, and persistence design.

---

## High-Level Architecture

```mermaid
graph TB
    Browser["Browser<br/>(React/TypeScript)"]
    Vite["Vite Dev Server<br/>:5173"]
    API["ASP.NET Core API<br/>:5083"]
    Worker["Background Worker<br/>(PlanningJobWorker)"]
    Ollama["Ollama LLM<br/>:11434<br/>(qwen2.5:7b)"]
    DB["SQLite Database"]
    
    Browser -->|HTTP| Vite
    Vite -->|HTTP REST| API
    API -->|HTTP| Ollama
    API <-->|R/W| DB
    Worker -->|Polls| DB
    Worker -->|HTTP| Ollama
    Worker -->|Writes| DB
```

---

## Frontend Architecture (5 Screens)

```mermaid
graph LR
    App["App.tsx<br/>(State Manager)"]
    S1["Screen 1<br/>Planning Request"]
    S2["Screen 2<br/>Alternatives"]
    S3["Screen 3<br/>Approval"]
    S4["Screen 4<br/>Draft Detail"]
    S5["Screen 5<br/>Progress"]
    
    App -->|Route| S1
    App -->|Route| S2
    App -->|Route| S3
    App -->|Route| S4
    App -->|Route| S5
    
    S1 -->|Create Job| S2
    S2 -->|Select| S3
    S3 -->|Approve| S4
    S4 -->|Progress| S5
```

**Implementation Files:**
- `frontend/src/App.tsx` — State and routing
- `frontend/src/screens/PlanningRequestScreen.tsx` — Screen 1
- `frontend/src/screens/AlternativesScreen.tsx` — Screen 2
- `frontend/src/screens/ApprovalScreen.tsx` — Screen 3
- `frontend/src/screens/DraftDetailScreen.tsx` — Screen 4
- `frontend/src/screens/ProgressScreen.tsx` — Screen 5

---

## Backend API Endpoints

| Method | Path | Implementation | Purpose |
|--------|------|---|---|
| POST | `/api/planning-jobs` | PlanningJobEndpoints | Create job |
| GET | `/api/planning-jobs/{id}` | PlanningJobEndpoints | Poll status |
| POST | `/api/planning-jobs/{id}/clarify` | PlanningJobEndpoints | Submit clarification |
| GET | `/api/planning-jobs/{jobId}/candidates` | MaintenancePlannerEndpoints | Get candidates |
| POST | `/api/drafts/{jobId}/approve` | ApprovalEndpoints | Approve plan |
| POST | `/api/drafts/{draftId}/cancel` | ApprovalEndpoints | Cancel plan |
| GET | `/api/drafts/{draftId}` | ApprovalEndpoints | Get draft detail |
| POST | `/api/drafts/{draftId}/mark-task-complete` | PlanProgressEndpoints | Mark task done |
| GET | `/api/drafts/{draftId}/progress` | PlanProgressEndpoints | Get progress |
| GET | `/api/drafts/{draftId}/export/json` | PlanProgressEndpoints | Export JSON |
| GET | `/api/drafts/{draftId}/export/pdf` | PlanProgressEndpoints | Export PDF |
| POST | `/api/drafts/{draftId}/task-edits` | EditEndpoints | Submit edits |
| GET | `/api/drafts/{draftId}/task-edits` | EditEndpoints | Get edits |

---

## Service Architecture

```mermaid
graph LR
    PlanJobs["IPlanningJobs<br/>(Job lifecycle)"]
    Interp["OllamaPlanningRequestInterpreter<br/>(LLM)"]
    Sched["DeterministicPlanningScheduler<br/>(Scheduling)"]
    Valid["PlanningCandidateValidator<br/>(Validation)"]
    Appr["IDraftApprovalService<br/>(Approval)"]
    Prog["IPlanProgressService<br/>(Progress)"]
    Exp["IPlanExportService<br/>(Export)"]
    Edit["ITaskEditorService<br/>(Edits)"]
    
    PlanJobs -->|Uses| Interp
    Interp -->|Pass| Sched
    Sched -->|Validate| Valid
    Valid -->|Return| Appr
    Appr -->|Track| Prog
    Prog -->|Serialize| Exp
    Exp -->|Support| Edit
```

**Implementation Files:**
- `src/MaintenancePlanner.Infrastructure/Planning/` — Interpreters, schedulers
- `src/MaintenancePlanner.Infrastructure/Approvals/` — Approval service
- `src/MaintenancePlanner.Infrastructure/Queries/` — Read models
- `src/MaintenancePlanner.Infrastructure/Edits/` — Task editor

---

## Planning Workflow

```mermaid
graph TD
    A["User submits request<br/>(Screen 1)"] -->|POST /planning-jobs| B["Queue PlanningJob<br/>(Queued)"]
    B -->|Worker picks up| C["Interpret via Ollama"]
    C -->|Success| D["DeterministicPlanningScheduler"]
    C -->|Clarification needed| E["Return ClarificationRequired"]
    E -->|User responds| F["Update request<br/>Reset to Queued"]
    F -->|Resume| C
    D -->|Generate candidates| G["For each candidate<br/>Validate"]
    G -->|All valid| H["Return CandidatesGenerated"]
    G -->|Infeasible| I["Return NoFeasibleCandidates<br/>with violations"]
    H -->|Persist| J["Job status: Succeeded"]
    I -->|Persist| J
    J -->|User views| K["Screen 2: Alternatives"]
```

---

## Approval and Reservation Workflow

```mermaid
graph TD
    A["User approves<br/>(Screen 3)"] -->|POST /drafts/{jobId}/approve| B["Check job outcome"]
    B -->|CandidatesGenerated| C["Create Draft<br/>(Pending)"]
    B -->|Other| D["Return error"]
    C -->|For each reserved part| E["Check OnHandQuantity"]
    E -->|Insufficient| F["Return 409 Conflict<br/>INSUFFICIENT_STOCK"]
    E -->|Available| G["Create Reservation<br/>(Confirmed)"]
    G -->|All ok| H["Atomic transaction commit"]
    H -->|Update Draft status| I["Draft: Approved<br/>ApprovedAtUtc: now"]
    I -->|Return 202| J["Response: /api/drafts/{draftId}"]
    F -->|No draft created| K["Show error<br/>Parts not reserved"]
    J -->|Auto-navigate| L["Screen 4: Draft Detail"]
```

---

## Database Schema (Key Entities)

```mermaid
erDiagram
    PLANNING_JOB ||--o| DRAFT : has
    PLANNING_JOB ||--o| PLANNING_JOB : replan
    DRAFT ||--o{ RESERVATION : has
    DRAFT ||--o{ EDITED_TASK_PLACEMENT : has
    DRAFT ||--o{ LOCKED_TASK_ASSIGNMENT : has
    ASSET ||--o{ TECHNICIAN_AVAILABILITY : has
    ASSET ||--o{ MAINTENANCE_TASK_DEFINITION : has
    SPARE_PART ||--o{ RESERVATION : has
    TECHNICIAN ||--o{ TECHNICIAN_AVAILABILITY : has
    MAINTENANCE_WINDOW ||--o| TECHNICIAN_AVAILABILITY : specifies
```

**Key Tables:**

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| PlanningJob | Job state & results | Id, Status, RequestPayload, ResultPayload, OriginalJobId, OperationId |
| Draft | Approved plan | Id, PlanningJobId, Status, ApprovedCandidatePayload, ApprovedAtUtc |
| Reservation | Reserved part | Id, DraftId, SparePartId, Quantity, Status, CompletionTime |
| LockedTaskAssignment | User decision lock | Id, DraftId, TaskId, TechnicianIds |
| EditedTaskPlacement | User edits | Id, DraftId, TaskId, NewStart, NewEnd |
| Asset | Equipment | Id, Name, Model |
| MaintenanceTaskDefinition | Task catalogue | Id, AssetId, Name, DurationMinutes, RequiredSkill, SourceDocumentId |
| SparePart | Inventory | Id, Name, OnHandQuantity, ReservedQuantity |
| Technician | Resource | Id, Name, HourlyRate |
| TechnicianAvailability | Time windows | Id, TechnicianId, MaintenanceWindowId, StartTime, EndTime |

---

## Ollama Integration Flow

```mermaid
graph LR
    A["Request<br/>(RequestText,<br/>AssetIds)"] -->|Construct prompt| B["InterpretationPrompt"]
    B -->|Serialize JSON| C["User message"]
    C -->|HTTP POST| D["Ollama<br/>:11434/api/chat"]
    D -->|Inference<br/>qwen2.5:7b| E["Response JSON"]
    E -->|Parse inner JSON| F["InterpreterOutput"]
    F -->|Validate schema| G{"Valid<br/>JSON?"}
    G -->|No| H["InvalidOutput<br/>exception"]
    G -->|Yes| I["PlanningInterpretation"]
    H -->|Catch| J["Job fails:<br/>MALFORMED_OUTPUT"]
    I -->|Pass to scheduler| K["DeterministicPlanningScheduler"]
    
    style D fill:#e8f5e9
    style I fill:#c8e6c9
```

**File:** `src/MaintenancePlanner.Infrastructure/Planning/OllamaPlanningRequestInterpreter.cs`

---

## Revision and State Lifecycle

```mermaid
graph LR
    V1_Job["V1 PlanningJob<br/>(Succeeded)"] -->|Approve| V1_Draft["V1 Draft<br/>(Approved)"]
    V1_Draft -->|Reservations| V1_Reservations["V1 Reservations<br/>(Confirmed)"]
    V1_Job -->|Create replan| V2_Job["V2 PlanningJob<br/>(OriginalJobId=V1)"]
    V2_Job -->|Plan| V2_Draft["V2 Draft<br/>(Pending)"]
    V2_Draft -->|Approve| V2_Approved["V2 Draft<br/>(Approved)"]
    V2_Approved -->|Atomic replace| V2_Reservations["V2 Reservations<br/>(Confirmed)"]
    
    V1_Draft -.->|Immutable| V1_Draft
    V1_Reservations -.->|Unchanged on V2 failure| V1_Reservations
    
    style V1_Draft fill:#c8e6c9
    style V2_Approved fill:#c8e6c9
```

---

## Key Design Decisions

1. **Deterministic Scheduling** — Not ML-optimized; fully auditable and reproducible
2. **Separate Background Worker** — Long-running planning doesn't block API
3. **Immutable Revisions** — Approved plans cannot be changed after approval
4. **Atomic Approval** — Reservations either all succeed or none do
5. **Idempotent Operations** — Safe retries via OperationId
6. **Local Ollama** — No API costs; all inference local; can be swapped for cloud LLM

---

## Azure Architecture (Proposed, Not Implemented)

For future cloud deployment:
- **Frontend:** Azure Static Web Apps (CDN)
- **API:** Azure App Service (auto-scale)
- **Database:** Azure SQL Database (managed)
- **Worker:** Azure Service Bus + Azure Functions
- **Ollama:** Azure Container Instances or dedicated GPU compute
- **Monitoring:** Application Insights

---

## Persistence and Recovery

- **State Persistence:** PlanningJob with checkpoint payload allows resume from interruption
- **Atomic Transactions:** EF Core ensures consistent writes
- **Lease-Based Recovery:** Jobs with expired leases can be reclaimed
- **Idempotency:** OperationId prevents duplicate job creation on retry

---

## Known Limitations

- **Single Model:** Only qwen2.5:7b; can be configured to other models
- **SQLite:** Local testing only; production requires PostgreSQL/SQL Server
- **No Auth:** Role selector is UI-only; no backend authentication
- **Scheduling:** Heuristic-based, not globally optimal
- **No OCR:** Text manuals only; scanned/image manuals not supported

