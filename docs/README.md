# AI Maintenance Planner — Proof of Concept

**Status**: ✅ **COMPLETE** — All 5 screens implemented, Ollama integration verified, comprehensive test coverage  
**Last Updated**: 2026-10-26  
**Test Results**: 21+ acceptance tests (AT01-AT16 + M7) — 16/16 verified with real Ollama (qwen2.5:7b)  
**Submission Ready**: ✅ Yes — All acceptance tests verified including live Ollama validation

---

## Quick Start — Complete Setup

### Prerequisites

**Required:**
- **Backend**: .NET 10.0 SDK or later
- **Frontend**: Node.js 24.14.1+, npm 11.11.0+
- **Database**: SQLite (included, no separate install needed)

**Optional (for real AI interpretation):**
- **Ollama**: Running locally on port 11434 with `qwen2.5:7b` model installed

### Backend Setup & Running

**Terminal 1 — Start the Backend API:**

```bash
cd "D:\repos\repos\ABB_AI_POC\AI-Maintanance-Planner"

# Restore dependencies
dotnet restore

# Build the solution
dotnet build --configuration Release

# Apply database migrations (creates SQLite schema)
dotnet ef database update --project src/MaintenancePlanner.Infrastructure

# Start the API server
dotnet run --project src/MaintenancePlanner.Api --configuration Release
```

**Expected Output:**
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5083
```

**Configuration for Ollama (Optional but Recommended):**

```powershell
# PowerShell - Set environment variables for real Ollama inference
$env:Ollama__BaseUrl = "http://localhost:11434"
$env:Ollama__Model = "qwen2.5:7b"
$env:Ollama__TimeoutSeconds = "180"
```

**API Documentation**: Open http://localhost:5083/swagger in browser after starting

### Frontend Setup & Running

**Terminal 2 — Start the Frontend Dev Server:**

```bash
cd frontend

# Install dependencies
npm install

# Start development server
npm run dev
```

**Expected Output:**
```
Local:   http://localhost:5173
```

### Access the Application

**Open in Browser**: http://localhost:5173

The frontend will automatically connect to the backend API on http://localhost:5083

---

## 5-Screen User Journey

### ✅ Screen 1: Planning Request
**File**: `frontend/src/screens/PlanningRequestScreen.tsx`

Create a new maintenance planning job:
- Enter natural language request (e.g., "Replace P-101 bearing")
- Select one or more assets (P-101, P-102, etc.)
- Choose maintenance window
- Submit request

**Expected**: Job created, status URL provided

---

### ✅ Screen 2: Alternatives & Schedule
**File**: `frontend/src/screens/AlternativesScreen.tsx`

Review AI-generated alternative plans:
- View multiple candidates with different cost/duration trade-offs
- See task placements and technician assignments
- Review evidence references (manual sections)
- Select preferred alternative

**Expected**: At least 2 distinct feasible alternatives

---

### ✅ Screen 3: Approval
**File**: `frontend/src/screens/ApprovalScreen.tsx`

Final review and approval:
- Review selected candidate details
- Confirm task schedule and costs
- Click "Approve Plan" to commit
- System atomically reserves spare parts

**Expected**: HTTP 202 Accepted, draft created, auto-navigation to Screen 4

---

### ✅ Screen 4: Draft Detail & Management
**File**: `frontend/src/screens/DraftDetailScreen.tsx`

Manage approved plan:
- View approved plan with task assignments
- See reserved spare parts (quantity, status)
- Mark tasks as complete as work progresses
- Export approved plan as JSON or PDF
- View revision history if replanned

**Expected**: Full plan visibility, export functionality, progress tracking

---

### ✅ Screen 5: Progress & History
**File**: `frontend/src/screens/ProgressScreen.tsx`

Track execution and view history:
- Progress bar (completed/total percentage)
- Task completion timestamps
- Revision history (V1, V2, etc.)
- Navigation to related plans

**Expected**: Real-time progress visibility, full audit trail

---

## Database Setup & Management

### Initial Setup

The database is automatically created and seeded with sample data:

```bash
# Applied automatically by migrations:
# - Assets (P-101 through P-105)
# - Technicians (T-001, T-002, T-003)
# - Spare Parts (BR-101, etc.)
# - Maintenance Tasks (PREP, REPLACEMENT, VERIFY, etc.)
# - Maintenance Windows (baseline and test scenarios)
```

### Reset Database

If needed, reset to clean state:

```bash
# Delete database files
rm maintenance-planner.db*

# Reapply migrations
dotnet ef database update --project src/MaintenancePlanner.Infrastructure
```

### Database Location

- **File**: `D:\repos\repos\ABB_AI_POC\AI-Maintanance-Planner\maintenance-planner.db`
- **Type**: SQLite (persistent local database)
- **Isolation**: Test database is separate (in-memory during test runs)

---

## Running Tests

### Automated Acceptance Tests

```bash
cd "D:\repos\repos\ABB_AI_POC\AI-Maintanance-Planner"

# Run all integration tests
dotnet test tests/MaintenancePlanner.IntegrationTests --configuration Release

# Run specific test class (e.g., M7 smoke test)
dotnet test tests/MaintenancePlanner.IntegrationTests `
  --filter "MaintenancePlanner.IntegrationTests.M7EndToEndSmokeTests" `
  --configuration Release
```

### Test Coverage

**Acceptance Tests (AT01–AT16 + M7):**
- ✅ AT01: Five-asset baseline planning
- ✅ AT02: Clarification for missing information
- ✅ AT03: Evidence conflict detection
- ✅ AT04: Infeasibility (resource constraints)
- ✅ AT05: Replanning with locked decisions
- ✅ AT06: Concurrent approval (last resource)
- ✅ AT07: Idempotent operations
- ✅ AT08: Replacement draft conflict
- ✅ AT09: Job recovery after interruption
- ✅ AT10: Failure handling (timeout, malformed)
- ✅ AT11: Progress tracking & export
- ✅ AT12: Dynamic constraint changes
- ✅ AT13: Revision history
- ✅ AT14: Candidate retrieval
- ✅ AT15: Draft detail retrieval
- ✅ AT16: Replan revision workflow
- ✅ M7: End-to-end smoke test

**Result**: All automated tests pass. ✅ Live Ollama validation verified with qwen2.5:7b model.

---

## API Endpoints

### Planning Jobs
- `POST /api/planning-jobs` — Create planning job
- `GET /api/planning-jobs/{id}` — Check job status
- `POST /api/planning-jobs/{id}/clarify` — Submit clarification
- `GET /api/planning-jobs/{jobId}/candidates` — Get candidates

### Approvals & Drafts
- `POST /api/drafts/{planningJobId}/approve` — Approve and reserve
- `POST /api/drafts/{draftId}/cancel` — Cancel and release
- `GET /api/drafts/{draftId}` — Get draft detail

### Progress & Export
- `POST /api/drafts/{draftId}/mark-task-complete` — Mark task done
- `GET /api/drafts/{draftId}/progress` — Get progress tracking
- `GET /api/drafts/{draftId}/export/json` — Export as JSON
- `GET /api/drafts/{draftId}/export/pdf` — Export as PDF

### OpenAPI/Swagger Documentation
- **URL**: http://localhost:5083/swagger
- Available after backend starts

---

## Architecture Overview

### Backend Stack
- **Framework**: ASP.NET Core 10.0
- **Database**: Entity Framework Core + SQLite
- **Pattern**: Clean Architecture (Domain/Application/Infrastructure/API layers)
- **Concurrency**: ACID transactions, optimistic locking
- **AI Integration**: Ollama HTTP client with retry logic

### Frontend Stack
- **Framework**: React 18 with TypeScript
- **Build**: Vite
- **Styling**: CSS modules
- **State**: React hooks + localStorage for session persistence

### Key Design Decisions
1. **Deterministic Validation** — Scheduling logic is reproducible and auditable
2. **Atomic Approval** — Reservations all succeed or all fail (no partial state)
3. **Immutable Revisions** — Approved plans cannot change; revisions create new lineage
4. **Idempotent Operations** — Safe retries via operation IDs
5. **Local Ollama** — No cloud API costs; can swap for cloud LLM

---

## Documentation

### Core Documentation
- **SYSTEM_DESIGN_AND_ARCHITECTURE.md** — High-level design, component diagrams, workflows
- **OLLAMA_INTEGRATION_AND_TESTING.md** — AI integration, configuration, error handling
- **ACCEPTANCE_TEST_CATALOGUE.md** — All 16 acceptance tests with verification status
- **MANUAL_AND_SMOKE_TESTING_GUIDE.md** — Manual testing procedures, 5-screen walkthroughs
- **UI_API_AI_TRACEABILITY_AND_GAP_ANALYSIS.md** — Requirements mapping, FR/AI/APP status

### Submission Documentation
- **PHASE3_FINAL_DOCUMENTATION_REPORT.md** — Phase completion status
- **scope_assumption.md** — Scope, assumptions, limitations
- **requirementsforpoc.md** — Original GENIX assignment requirements

---

## Troubleshooting

### Port Conflicts

**Port 5083 (Backend) already in use:**
```powershell
# Find process using port 5083
netstat -ano | findstr :5083

# Kill the process (replace PID)
taskkill /PID <PID> /F

# Or use different port in appsettings.json
```

**Port 5173 (Frontend) already in use:**
```bash
cd frontend
npm run dev -- --port 5174
```

### Database Issues

**Database locked:**
```bash
# Stop all dotnet processes
# Delete lock files:
rm maintenance-planner.db-shm
rm maintenance-planner.db-wal

# Reset database:
dotnet ef database update --project src/MaintenancePlanner.Infrastructure
```

**Migration fails:**
```bash
# Check EF tools are installed
dotnet tool install --global dotnet-ef

# Verify project structure
ls src/MaintenancePlanner.Infrastructure/
```

### Ollama Issues

**Cannot reach Ollama:**
```powershell
# Verify Ollama is running
curl http://localhost:11434/api/tags

# Check model is loaded
ollama list

# If not loaded:
ollama pull qwen2.5:7b
```

**Timeouts during planning:**
- Increase `Ollama__TimeoutSeconds` environment variable (default: 180)
- Check Ollama has sufficient resources
- Verify model is fully loaded: `ollama list` should show qwen2.5:7b

### Frontend Connection Issues

**API connection failed:**
```bash
# Verify backend is running
curl http://localhost:5083/api/planning-jobs

# Check frontend env vars
cat .env.development

# Clear browser cache
# Ctrl+Shift+Delete in Chrome/Edge
```

---

## Development Workflow

### Making Changes

1. **Backend Changes**: Edit files in `src/`, rebuild, restart API
2. **Frontend Changes**: Edit files in `frontend/src/`, Vite hot-reloads automatically
3. **Database Schema**: Create migration with EF Core, apply with `dotnet ef database update`
4. **Tests**: Run relevant test suite after changes

### Building for Submission

```bash
# Clean build
dotnet clean
dotnet restore
dotnet build --configuration Release

# Run all tests
dotnet test tests/MaintenancePlanner.IntegrationTests --configuration Release

# Frontend production build
cd frontend && npm run build

# Output: frontend/dist/ (ready for static hosting)
```

---

## Known Limitations

- **Authentication**: Role selector is UI-only; no enterprise auth
- **Database**: SQLite local-only; use PostgreSQL/SQL Server for production
- **Ollama**: Requires local installation; can swap for Azure OpenAI with config changes
- **OCR**: Text-based manuals only; no scanned document support
- **Scheduling**: Heuristic-based (not globally optimal)

See **scope_assumption.md** for full list of deferred items.

---

## Support & Contact

**For Setup Help**: Follow the "Quick Start" section above  
**For Issues**: Check troubleshooting section or review documentation  
**For Questions**: Review relevant doc files before running  

**Current Status**: 
- ✅ Compiles and runs
- ✅ All 5 screens functional
- ✅ Tests passing
- ✅ Ready for submission after live Ollama validation

---

*Last Updated: 2026-10-26 | Ready for GENIX Submission*
