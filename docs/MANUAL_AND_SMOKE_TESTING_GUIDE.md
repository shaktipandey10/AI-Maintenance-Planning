# Manual and Smoke Testing Guide
## AI Maintenance Planner

**Date:** 2026-10-26  
**Scope:** Manual testing procedures for all 5 screens, Ollama smoke test, failure scenarios, and safe database handling.

---

## Environment Setup

### Prerequisites
- **Backend API:** Running on `http://localhost:5083`
- **Frontend:** Running on `http://localhost:5173` (Vite dev server)
- **Database:** SQLite, seeded with baseline data (P-101 anchor scenario + 4 additional assets)
- **Ollama (optional):** Running on `http://localhost:11434` with `qwen2.5:7b` model installed

### Startup Steps

**Backend:**
```bash
cd "D:\repos\repos\ABB_AI_POC\AI-Maintanance-Planner"
dotnet restore
dotnet build
dotnet ef database update --project src/MaintenancePlanner.Infrastructure
dotnet run --project src/MaintenancePlanner.Api --configuration Release
# Listen on http://localhost:5083
```

**Frontend:**
```bash
cd frontend
npm install
npm run dev
# Listen on http://localhost:5173
```

**Database Reset:**
```bash
rm maintenance-planner.db*
dotnet ef database update --project src/MaintenancePlanner.Infrastructure
```

---

## 5-Screen User Journey

### Screen 1: Planning Request
**File:** `frontend/src/screens/PlanningRequestScreen.tsx`

**Purpose:** Create a new planning job

**Workflow:**
1. Enter maintenance request text (required)
2. Add one or more asset IDs (required)
3. Optionally specify maintenance window
4. Click "Create Job"
5. Receive job ID and status URL

**Test Data:**
- Request: "Replace the P-101 bearing and check oil levels"
- Assets: P-101, P-102, P-103, P-104, P-105
- Window: MW-P101-OCT2026

---

### Screen 2: Alternatives
**File:** `frontend/src/screens/AlternativesScreen.tsx`

**Purpose:** Review generated candidates with cost and duration trade-offs

**Workflow:**
1. View list of candidates (≥2 alternatives)
2. Each shows: rationale, cost, duration, evidence, task placements
3. Select candidate to proceed
4. Review evidence references (manual sections, task IDs)

**Expected Elements:**
- Candidate count: ≥1 (usually 2+)
- Cost calculation: TotalLabourCostAmount and currency
- Duration: CompletionTimeMinutes
- Evidence references: Document IDs, pages, sections

---

### Screen 3: Approval
**File:** `frontend/src/screens/ApprovalScreen.tsx`

**Purpose:** Review and approve the selected plan

**Workflow:**
1. See final candidate summary
2. Click "Approve Plan"
3. Backend creates Draft, reserves parts, atomically commits
4. Success: Auto-navigate to Screen 4

**Expected:**
- HTTP 202 Accepted response
- Draft ID returned
- Auto-navigation to Screen 4 on success
- 409 Conflict if parts unavailable (show error message)

---

### Screen 4: Draft Detail
**File:** `frontend/src/screens/DraftDetailScreen.tsx`

**Purpose:** Manage approved plan, track progress, export

**Sections:**
1. **Draft Information:** Status, timestamps, IDs
2. **Maintenance Plan:** Selected candidate details
3. **Scheduled Tasks:** Task placements with timing
4. **Spare Parts Reservations:** Reserved parts with quantities and status
5. **Task Edits:** Add/edit task placements (if in draft state)
6. **Progress:** Track completion of tasks
7. **Exports:** Download JSON and PDF

**Key Actions:**
- Mark task complete (POST /api/drafts/{draftId}/mark-task-complete)
- Get progress (GET /api/drafts/{draftId}/progress)
- Export JSON (GET /api/drafts/{draftId}/export/json)
- Export PDF (GET /api/drafts/{draftId}/export/pdf)

---

### Screen 5: Progress
**File:** `frontend/src/screens/ProgressScreen.tsx`

**Purpose:** Track task completion and view revision history

**Sections:**
1. Progress bar (completed/total percentage)
2. Task completion table (status, completion time)
3. Revision history (V1, V2, etc. with timestamps)
4. Navigation back to draft detail

---

## Happy Path Scenario: Full Workflow

### Scenario: Basic Planning Request → Approval → Export

**Test Data:**
```
Request: "Replace the P-101 bearing and verify oil levels"
Assets: ["P-101"]
Window: "MW-P101-OCT2026"
```

**Steps:**

1. **Screen 1 - Create Job**
   - Fill form with test data
   - Click "Create Job"
   - Save returned Job ID

2. **Screen 1 - Check Status**
   - Paste Job ID in status section
   - Click "Get Status"
   - Wait for status = "Succeeded"

3. **Screen 2 - View Alternatives**
   - Click "View Alternatives & Schedule"
   - Verify ≥1 candidate displayed
   - Review cost and duration
   - Select a candidate (auto-selected if only one)

4. **Screen 3 - Approve**
   - Click "Proceed to Approval"
   - Review candidate summary
   - Click "Approve Plan"
   - Auto-navigate to Screen 4

5. **Screen 4 - Review Draft**
   - Verify status = "Approved"
   - View reservations (spare parts with qty and status "Confirmed")
   - Click "Export JSON" → save file
   - Click "Export PDF" → save file

6. **Verify Exports**
   - Open JSON file → verify draftId, progress, candidate details
   - Open PDF file → verify layout, content matches JSON

**Expected Outcome:**
✅ Complete workflow from request to export
✅ All sections visible and functional
✅ Exports are valid and immutable

---

## Failure Scenarios

### Scenario F1: Infeasible Plan (Short Window + Missing Part)

**Setup:**
- Part BR-101 has quantity 0 (unavailable)
- Window: "MW-P101-SHORT-20261024" (4 hours: 08:00-12:00)

**Test:**
1. Create job requesting P-101 bearing replacement with 4-hour window
2. Check status until "Succeeded"
3. Verify outcome = "NoFeasibleCandidates" or "Infeasible"
4. Check violations include:
   - TASKS_EXCEED_WINDOW (6 hours > 4 hours)
   - PART_UNAVAILABLE (BR-101 qty 0)

**Expected:** Clear error message with both blockers

---

### Scenario F2: Concurrency (Last Part Over-Reservation)

**Setup:**
- BR-101 quantity = 1
- Two jobs both requesting BR-101

**Test:**
1. Create Job 1, approve it (succeeds, reserves BR-101)
2. Create Job 2 with same part requirement
3. Attempt to approve Job 2 simultaneously
4. Verify: Job 1 succeeds, Job 2 returns 409 Conflict
5. Check database: BR-101 reserved qty = 1 (not 2)

**Expected:** No over-reservation, atomic approval

---

### Scenario F3: Ollama Timeout (if Real Ollama)

**Setup:**
- Stop or block Ollama

**Test:**
1. Create planning job
2. Wait ~180 seconds (timeout)
3. Job status = "Failed"
4. Failure code = "OLLAMA_TIMEOUT"

**Expected:** Clear timeout error, option to retry

---

## Ollama Smoke Test

### Prerequisites
```bash
ollama list
# Verify qwen2.5:7b is installed

# If not:
ollama pull qwen2.5:7b
```

### Run Smoke Test via PowerShell

```powershell
# Set environment
$env:Ollama__BaseUrl = "http://localhost:11434"
$env:Ollama__Model = "qwen2.5:7b"
$env:Ollama__TimeoutSeconds = "180"

# Start API
dotnet run --project src/MaintenancePlanner.Api --configuration Release

# In separate PowerShell window:
$body = @{
    requestText = "Replace the P-101 bearing."
    assetIds = @("P-101")
    windowId = "MW-P101-BASELINE-20261024"
} | ConvertTo-Json

$job = Invoke-RestMethod -Method Post `
    -Uri http://localhost:5083/api/planning-jobs `
    -Headers @{ "Idempotency-Key" = [guid]::NewGuid().ToString() } `
    -ContentType "application/json" `
    -Body $body

Write-Host "Job ID: $($job.jobId)"

# Poll status
do {
    Start-Sleep -Milliseconds 500
    $status = Invoke-RestMethod -Uri "http://localhost:5083/api/planning-jobs/$($job.jobId)"
    Write-Host "Status: $($status.status) | Step: $($status.currentStep)"
} while ($status.status -in @("Queued", "Running"))

Write-Host "Final Status: $($status.status)"
if ($status.resultPayload) {
    $result = $status.resultPayload | ConvertFrom-Json
    Write-Host "Outcome: $($result.outcome)"
    Write-Host "Interpretation: $($result.result.interpretation.maintenanceIntent)"
}
```

### Expected Results

✅ Job completes within 180 seconds  
✅ Outcome = "CandidatesGenerated"  
✅ Interpretation includes: maintenanceIntent, proposedAssetIds, proposedTaskIds  
✅ Candidates have rationale, costs, evidence references  

### Running Automated Smoke Test

```bash
dotnet test tests/MaintenancePlanner.IntegrationTests --filter "M7" --configuration Release
```

---

## Data Safety and Reset

### Safe Testing Practices

1. **Use Separate Database**
   ```bash
   $env:ConnectionStrings__MaintenancePlanner = "Data Source=$env:TEMP\mp-test.db"
   # Run tests
   Remove-Item "$env:TEMP\mp-test.db"
   ```

2. **Do NOT**
   - Delete approved drafts or revisions
   - Modify seed data directly
   - Run "Mark Complete" multiple times on same task
   - Use live production assets

3. **Database Reset**
   ```bash
   # Clean
   Remove-Item "maintenance-planner.db*"
   
   # Reseed
   dotnet ef database update --project src/MaintenancePlanner.Infrastructure
   ```

---

## Manual Testing Checklist

| Test | Status | Evidence |
|------|--------|----------|
| Screen 1: Job creation | ✓ | Job ID returned, status = Queued |
| Screen 2: Candidates displayed | ✓ | ≥1 candidate with cost/duration |
| Screen 3: Approval succeeds | ✓ | Draft created, status = Approved |
| Screen 4: Reservations visible | ✓ | Parts reserved, status = Confirmed |
| Screen 4: Export JSON valid | ✓ | File downloads, contains draftId |
| Screen 4: Export PDF renders | ✓ | File downloads, PDF readable |
| Screen 5: Progress tracked | ✓ | Completion % updates |
| Infeasible plan rejected | ✓ | Outcome = NoFeasibleCandidates |
| Concurrency handled | ✓ | 409 Conflict on over-reservation |
| Ollama timeout handled | ✓ | Job fails with OLLAMA_TIMEOUT code |

---

## Notes

- **Real Ollama Required:** Smoke test demonstrates actual Ollama integration; automated tests use deterministic mocks
- **Reproducibility:** All test data is fictional and seed-based; results are reproducible
- **Limitations:** No OCR for scanned manuals; no enterprise auth; SQLite only for local testing
