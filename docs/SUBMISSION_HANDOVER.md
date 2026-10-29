# Submission Handover Note

Handover note (DEL10) and correction report (feedback point 34) for the AI Maintenance Planner. Updated on 2026-10-29 after the Stage 7 functional assessment and focused test runs.

**Basis.** Source and documents were read. Focused automated tests were run on 2026-10-29 with a mocked model and isolated in-memory or temporary databases (run register R-13 to R-20 in [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md)). **The API was not started, the real model was not called, and the original database and its sidecars were not opened.** Where a fact is not in the repository it is marked **Owner confirmation required** and is not estimated.

**Recommendation.** The submission is **not fully verified and not yet complete**. Eleven requirements are only Partially implemented and several development and verification gaps remain (section 11 and [SUBMISSION_GAP_SUMMARY.md](SUBMISSION_GAP_SUMMARY.md)). The automated tests that exist all pass, but no behaviour has been verified with the real model.

## 1. Project overview and documents

The AI Maintenance Planner is a proof of concept. An ASP.NET Core (.NET 10) API with a background worker plans maintenance work from a free-text request. A local Ollama model (`qwen2.5:7b`) interprets the request, a deterministic scheduler and validator build and check candidate plans, and a reviewer approves one, which reserves spare parts. SQLite stores the data. A React, TypeScript and Vite frontend drives the workflow.

| Need | Document |
|---|---|
| Setup, configuration, commands, limitations | [README.md](../README.md) |
| Original requirements and reviewer feedback | [requirementsforpoc.md](requirementsforpoc.md), [Feedback.md](Feedback.md) |
| Requirement status, with implementation and verification kept apart | [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md) |
| Traceability | [FUNCTIONAL_REQUIREMENTS_TRACEABILITY.md](FUNCTIONAL_REQUIREMENTS_TRACEABILITY.md), [AI_REQUIREMENTS_TRACEABILITY.md](AI_REQUIREMENTS_TRACEABILITY.md), [FEEDBACK_TRACEABILITY.md](FEEDBACK_TRACEABILITY.md) |
| Acceptance tests AT01 to AT12 and run register | [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md) |
| Architecture, AI design, API, data dictionary | [ARCHITECTURE.md](ARCHITECTURE.md), [AI_DESIGN.md](AI_DESIGN.md), [API.md](API.md), [DATA_DICTIONARY.md](DATA_DICTIONARY.md) |
| Every remaining gap | [SUBMISSION_GAP_SUMMARY.md](SUBMISSION_GAP_SUMMARY.md) |

## 2. Correction report

One row per feedback area ([Feedback.md](Feedback.md)). **Cause** is given only where the repository records it. **Correction** is what the source now does. **Verification** separates captured runs (R-13 to R-20, mocked model) from what has not been run. Point numbers refer to [FEEDBACK_TRACEABILITY.md](FEEDBACK_TRACEABILITY.md).

| Area (points) | Gap | Cause | Correction in the repository | Verification result |
|---|---|---|---|---|
| Data and imports (1 to 3) | Complete dataset; PDF, CSV and JSON import; clear invalid-record reporting | Not recorded | Six structured import datasets plus PDF manuals, with an `ImportReport` per invalid record; `data/fixtures/plant01/` sample set; `data/fixtures/fr02-demo/` invalid-data files | Import tests passed (R-14, R-15) with the tests' own inline data. The import script and fixture files were never run. Invalid-data files exist for assets only. |
| Selection and approval (4 to 6) | Approve the selected alternative; revalidate; prevent conflicting reservations | Not recorded | Approval takes a `candidateIndex`, revalidates, snapshots the candidate, and reserves parts and technician time in one transaction | Selection, stale-candidate and parallel-approval tests passed (R-13, R-14). The tests assert the final database state; the logs do not print it. |
| Revisions and retries (7 to 12) | Preserve approved history and feasible locks; reservation handling on replacement, failed replacement, repeats, cancellation | Not recorded | Replacement approval supersedes V1 and swaps reservations atomically; a failed replacement rolls back; repeated approval and cancellation are idempotent; locks carry into replans | Replacement, failed-replacement, repeat and cancellation tests passed (R-13, R-14). |
| Task progress and exports (13 to 16) | Per-task states; preserve completed work; consumed versus unused parts; exports matching the approved revision | Not recorded | Forward-only task status; consumption recorded; cancellation releases unconsumed stock; JSON and PDF export from the approved snapshot | Progress, consumption, completed-work and export-agreement tests passed (R-13, R-14). **The exports omit assumptions, evidence references and deferred work** (requirement FR10), which is an open development gap. |
| Application workflow (17 to 21) | Editing, replanning, **change comparison**, saved plans, business-action history | Not recorded | Editing, locks, replanning, saved plans and history are implemented | Their tests passed (R-14, R-18). **Change comparison is not implemented** (point 19). |
| AI requirements (22 to 28) | Verify AI01 to AI07; inspectable evidence; callable tools; bounded validation and repair; context across changes; invalid output; real versus mocked evidence | Not recorded for the original gaps. One defect has a recorded cause: the prompt did not carry the request's window ID, so the real model invented one (`AI03_Stage3_RealModelRun.txt`). | Tool-calling loop and six tools behind `Ollama:EnableTools` (off by default); the prompt now carries `selectedWindowId`; real and mocked evidence are separated ([AI_DESIGN.md](AI_DESIGN.md)) | AI03 integration and unit tests passed again with a mocked model (R-16, R-17). **The one real-model run failed with `OLLAMA_TIMEOUT`, and the window-ID fix was not re-run with the real model.** The repair step (AI04), evidence inspection from the UI (AI02) and the before-and-after summary (AI06) remain absent. |
| Recovery and failures (29 to 32) | Interruption recovery; consistent state; duplicate prevention; actionable failures | Not recorded | Lease-based worker with heartbeat, checkpoint after a validated interpretation, idempotency keys, coded failure outcomes | Recovery, checkpoint-resume, idempotency and failure tests passed (R-13, R-14). The restart is an in-process host restart; no live process restart was run. |
| Resubmission 33 | Corrected requirement matrix | Not recorded | [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md) | Exists. Re-rated in Stage 7 (section 3). |
| Resubmission 34 | Correction report | Not recorded | This section | Causes are mostly unknown; the owner can supply them. |
| Resubmission 35 | Fixed commit | Not recorded | The Stage 7 commit (section 8) | Established; see section 8 for what it excludes. |
| Resubmission 36 | Setup instructions | Not recorded | [README.md](../README.md) | A clean setup was **not** run; there is no reset command. |
| Resubmission 37 | AT01 to AT12 results | Not recorded | [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md) | 10 Pass, 2 Partial (AT05, AT12), all with a mocked model. |
| Resubmission 38 | The demonstration | Not recorded | Owner-confirmed demonstration videos (section 7) | Not inspected. |
| Resubmission 39 | Approved V1 and revised V2 exports | Not recorded | Owner-confirmed B1 and B2 exports (section 7) | Not inspected. |

## 3. Requirement coverage

Rated by what the source implements, with verification tracked separately ([REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md)). Missing test evidence alone does not make a requirement Partial.

| Classification | Count | Requirements |
|---|---|---|
| Fully implemented | **12** | FR01, FR02, FR03, FR05, FR06, FR08, AI01, AI07, APP01, APP03, APP04, APP05 |
| Partial (behaviour missing or incomplete) | **11** | FR04, FR07, FR09, FR10, AI02, AI03, AI04, AI05, AI06, APP02, APP06 |
| Not implemented | **0** | none |
| Cannot be established from source | **0** | none |
| **Total** | **23** | FR01 to FR10, AI01 to AI07, APP01 to APP06 |

**Fully implemented and still awaiting verification: 3.** AI01 and AI07 await a real-model run, and APP05 awaits a live process restart and a frontend test of the disabled-submit state. The other nine fully implemented requirements have captured passing automated runs.

**Changes in Stage 7:** FR10 moved from Implemented to Partial after the export code was read (assumptions, evidence and deferred work are not exported). AI01, AI07 and APP05 moved from Partial to Implemented because their earlier Partial reasons were missing verification, not missing behaviour. AI03 stays Partial because two of its tools lack the replanning context and there is no real-model success.

## 4. Acceptance-test status

As recorded in [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md): **10 Pass** (AT01, AT02, AT03, AT04, AT06, AT07, AT08, AT09, AT10, AT11), **2 Partial** (AT05, AT12), 0 Fail, 0 Not verified. Earlier state: 0 Pass, 3 Partial, 9 Not verified.

What the evidence is and is not:
- All backend test methods that `dotnet test --list-tests` reports were run in eight focused groups and passed (606 test cases). The frontend suite passed (112 tests in 9 files). Each log header records the command, time and repository state.
- Every Pass is an **automated result with a mocked or fixed model interpretation**. No acceptance scenario was verified with the real model. The recorded real-model run (R-12) failed with `OLLAMA_TIMEOUT`.
- AT05 is Partial because no single test removes a technician's availability after a lock exists and replans through the job. AT12 is Partial because no test changes a duration or quantity and the live change is not verified by this review.
- AT09 is an in-process restart, not a live process restart.
- A test-fixture path was fixed first: three fixtures looked for `requirementsforpoc.md` at the repository root. That is the failure recorded as R-08, and it kept AT03 and AT04 from running.
- No small real-model AI evaluation set exists.

## 5. Known limitations

**Functional** (details in [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md)):
- No before-and-after change comparison between revisions (FR07, AI06, feedback point 19).
- **Exports omit assumptions, evidence references and deferred work** (FR10).
- No revise or repair step: validator violations are not fed back to the model (AI04).
- No requires-review outcome after a resource is lost post-approval (APP02). The "Awaiting review" and "Changes requested" plan states are absent.
- The candidate and draft APIs return evidence as source IDs only, so the excerpt, page and version are not shown to the user (AI02). No approval step for a similar-model manual.
- Planning-request submission and clarification are not recorded as business actions (FR09).
- No retry action or retry guidance for failed jobs (APP06). FR04 has no explicit statement when fewer than two alternatives exist.
- Selection rationale is a fixed sentence per policy, and sourced, calculated and assumed facts are not labelled (AI05).
- AI03: `validate_schedule` and `calculate_cost` lack the replaced-plan context, tool calls are not recorded in the job result, saved candidates last one attempt, and no test covers prompt-injection resistance.

**Technical:**
- SQLite single-file storage. Concurrency relies on SQLite transactions (a code comment says they begin `IMMEDIATE`; the parallel-approval tests pass, which supports but does not prove this).
- No authentication or authorization. Roles are self-declared. CORS allows any origin.
- Labour cost currency is `UNKNOWN`. The model is identified by tag only, with no sampling options set and no digest recorded.
- The frontend is React with Vite, while [scope_assumption.md](scope_assumption.md) names Angular. The requirements allow any suitable web UI.
- A template route `/weatherforecast` and an unused key `ConnectionStrings:Maintenance` remain. No committed OpenAPI document.

## 6. Setup and reproducibility status

- **Clean setup: not verified.** The documented commands were not run from an empty checkout. The frontend dev, build and preview commands, the import script on a fresh database and the API start were not run in Stage 7.
- **Reset: no command exists.** The requirement asks for a seed and reset command. The startup seed is automatic and the six-file import is a manual script ([DATA_DICTIONARY.md](DATA_DICTIONARY.md) sections 9 and 10). Imported manuals live outside the database, so recreating only the database leaves them.
- **No `.env.example`** (no credentials are used). `frontend/package-lock.json` exists; no NuGet lock file or `global.json`.
- **Database files:** `maintenance-planner.db` and `maintenance-planner copy.db` are tracked by git although `.gitignore` lists `*.db`. `maintenance-planner.db` shows as modified and **was not committed**. Its sidecars exist at the repository root.
- **Stray root files:** `test_output.txt`, `test_at10_all.txt`, `test_regressions.txt`, `generate_p102_pdf.cs`, `reduced-request.json` (left as they were).
- **Tests:** all backend and frontend tests pass in the focused runs. The earlier fixture failure is fixed (section 4).

## 7. Demonstration and export inventory

**Owner-confirmed deliverables.** The owner reports that demonstration videos have been prepared, including a 16-minute demonstration, and that B1 and B2 PDF and JSON exports have been produced. These are recorded here as owner-confirmed. **Their content, duration, lineage and approval were not inspected or assessed for this documentation.** The 16-minute demonstration is not identified among the files below, and the repository exports are named `V1_` and `V2_`, so the owner should confirm which files are the B1 and B2 exports.

**Files present in the repository** (names and counts only):
- `docs/Recordings/`: 12 `.webm` files (about 149 MB): `AlternativeApproval_EndtoEnd`, `Cancelation_SparePartRelease`, `Candidate_Approval_Draft_Progressing_flow`, `ImportOfDataValidAndInvalid`, `Infeasible_scenerio`, `JobScheduling`, `Locking_Revision_Replan_Idempotency_ClarificationWindow`, `Restart_JobRunScenario`, `TaskProgres_ReplanWorkPreservation`, `V1-V2_lifecycle`, `V1-V2_lifecycle_Part2`, `V1-V2_lineageDemo`.
- `docs/Screenshots/`: 53 files.
- `docs/` exports: 11 files: `V1_plan`, `V1_latest_plan` (`.json`, `.pdf`), `V1_Superseded` (`.pdf`), `V1_Draft` (`.pdf`), `V2_plan`, `V2_latest_plan` (`.json`, `.pdf`), `V2_Draft` (`.pdf`).
- `docs/DebugAndTests/`: the test logs, including the Stage 7 logs `Final_*.txt`.

**Not in the Stage 7 commit:** the recordings, screenshots and exports, and `docs/DebugAndTests/AlternativeSelection.txt`, a manual test note that pre-dates this work and was not reviewed for it. They were left untouched and uncommitted so the owner can decide how to package them. Documents that link to them link to files that exist in the working tree but not in the commit.

## 8. Repository state

| Item | Value |
|---|---|
| HEAD before Stage 7 | `1376af5`, 2026-10-28 12:02:31 +05:30, "Add post-feedback maintenance planner corrections" |
| Branch | `feedback-corrections` |
| Remote | `origin` is configured to `https://github.com/shaktipandey10/AI-Maintenance-Planning.git`. Whether a reviewer can access it was **not checked**. |
| Fixed submission commit | The commit that adds this file, on `feedback-corrections`. Its hash is in the git log and in the Stage 7 report (a file cannot contain its own commit hash). The source, test and frontend files in it are the ones the Stage 7 runs tested. |
| Committed | Documentation (including the test logs and this note), the AI03 source and tests, the frontend changes and the import-screen files, the three-file test-fixture path fix, and `data/fixtures/fr02-demo/` |
| Not committed (left as found) | `maintenance-planner.db` (modified) and its sidecars; the 12 deleted archived documents; the recordings, screenshots and exports; `AlternativeSelection.txt` |

No reset, clean, restore or deletion was performed.

## 9. Azure mapping (proposal only)

**This is a design proposal. Nothing was deployed, built or tested on Azure, and no deployment evidence exists.** Each mapping needs code or configuration changes that have not been made or sized.

| Current component | Proposed Azure service | What would change |
|---|---|---|
| React and Vite frontend | Azure Static Web Apps | Static hosting; set `VITE_API_BASE`; restrict CORS (now any origin) |
| ASP.NET Core API | Azure App Service or Azure Container Apps | Configuration from application settings; HTTPS and a real hostname |
| Background worker (hosted service in the API process) | The same host, or a separate container | The worker already uses a database lease, so several instances are designed to be safe; this was not tested |
| SQLite file | Azure SQL Database or Azure Database for PostgreSQL | EF Core provider change and new migrations. SQLite-specific code exists and concurrency behaviour must be re-verified. |
| Ollama with `qwen2.5:7b` on the local machine | A hosted model endpoint (for example Azure OpenAI or Azure AI Foundry), or a GPU VM running Ollama | Replace or adapt the HTTP client and tool-calling contract; re-run the AI evaluation. The recorded run timed out on CPU. |
| Imported manual files | Azure Blob Storage | Replace the file-system manual store |
| Roles chosen in the UI | Microsoft Entra ID | Replace the self-declared `X-Actor-Role` header |
| Logs | Application Insights | Add telemetry |
| Secrets | Azure Key Vault | None are used today |

## 10. Effort, cost, reuse and AI-assistance disclosure

| Item | What the repository shows | Status |
|---|---|---|
| Actual effort | Nothing is recorded. Git history has three commits from 2026-10-26 to 2026-10-28; file dates range from 2026-10-21 to 2026-10-29. That is a span, not effort. | **Owner confirmation required** |
| Model and API cost | The runtime model is local (Ollama), so no per-call API cost is recorded. No cost record exists for any hosted assistant or other service used during development. | **Owner confirmation required** |
| Runtime model | `qwen2.5:7b` through Ollama 0.40.12. `ollama show` output records the model's licence as Apache License 2.0 (`AI03_Stage3_OllamaCompatibilityPreflight.txt`). | Recorded |
| Reused libraries | Backend: ASP.NET Core and Entity Framework Core 10.0.13 with SQLite, PdfPig 0.1.16, iTextSharp 5.5.13.3, Swashbuckle.AspNetCore 10.3.1, Microsoft.AspNetCore.OpenApi 10.0.8; tests: xUnit 2.9.3, FluentAssertions 8.11.0, Moq 4.21.0, coverlet 6.0.4. Frontend: React 19.3.1, html2pdf.js 0.14.0, Vite, Vitest, Testing Library, oxlint, TypeScript. | Recorded from `*.csproj` and `package.json`; which parts were written by hand is **owner confirmation required** |
| Licence information found | From local package metadata only: PdfPig `Apache-2.0`, Swashbuckle.AspNetCore `MIT`, html2pdf.js `MIT`, React, Vite and Vitest `MIT`. The iTextSharp 5.5.13.3 package metadata links to the GNU AGPL page. No other licence was checked. There is **no `LICENSE` file**. | **No legal conclusion is drawn.** See the licence item in [SUBMISSION_GAP_SUMMARY.md](SUBMISSION_GAP_SUMMARY.md). |
| Templates | The frontend started from the Vite React TypeScript template (`frontend/README.md` still holds template text). The API still contains the .NET weather-forecast template route. | Recorded; extent of reuse **owner confirmation required** |
| AI coding assistance | In the working sessions recorded for this documentation, **Claude Code** (an AI coding assistant) wrote the AI03 source and tests, the three-file test-fixture fix, the Stage 7 test runs and their logs, and the requirement-status, traceability, README, architecture, data-dictionary, AI-design, API, acceptance-report and handover documents, under the owner's step-by-step instructions. The instructions were headed "Copilot Agent", so the owner must say whether GitHub Copilot or other tools were also used, and which tools produced the earlier code (the commits before 2026-10-29). | **Owner confirmation required** for the whole project |

## 11. Submission blockers and recommended next actions

Full detail, severity and the evidence that closes each item is in [SUBMISSION_GAP_SUMMARY.md](SUBMISSION_GAP_SUMMARY.md). In order of risk:

**Development work**
1. **Change comparison** between revisions (FR07, AI06; feedback point 19).
2. **Complete the exports** with assumptions, evidence references and deferred work (FR10).
3. **Repair step** for model output (AI04), and **inspectable evidence** in the candidate and draft APIs and UI (AI02).
4. **Reset command** (the original deliverables require seed and reset tooling).
5. Decide on the other Partial requirements: requires-review outcome (APP02), retry guidance (APP06), request recording (FR09), explanations (AI05), AI03 limits, and the single-alternative statement (FR04).

**Verification**
6. **A successful real-model end-to-end run** (needs approval, faster hardware or a longer timeout), or an explicit acceptance of the limitation.
7. **The small real-model AI evaluation set** the requirements ask for.
8. **A clean setup run** from an empty checkout, and a live process restart (AT09), a live duration or quantity change (AT12) and the import script on a fresh database.

**Administrative and disclosure**
9. **Supply the section 10 facts** (effort, cost, reuse, AI-assistance disclosure) and review licences, including iTextSharp 5.x.
10. **Package the evidence** (recordings, screenshots, B1 and B2 exports) with the commit, and decide what to do with the database files, the deleted archived documents and the stray root files.
