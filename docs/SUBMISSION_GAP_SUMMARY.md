# Submission Gap Summary

The gaps that remain between the repository and the original submission requirements, for the owner to decide which to fix and which to accept as documented limitations. Updated on 2026-10-29 after the functional assessment and the final verification runs.

**Basis.** Every gap was re-checked against the current source. Focused automated tests were run (R-13 to R-20 in [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md)): all backend test methods and the frontend suite passed, with a mocked model and isolated databases. **No test used the live API, the original database or the real model.** Recordings, screenshots and exported plans are owner-confirmed deliverables and were not inspected.

**Recommendation.** **Not ready to declare complete.** Development gaps D-01, D-02, D-03, D-05 and D-11, verification gaps V-02 and V-03, and the disclosure item R-02 should be closed or explicitly accepted by the owner. All the automated tests pass, but that does not close development gaps, and nothing has been verified with the real model.

## Delivered and verified

| Item | Result |
|---|---|
| Requirements (23) | **12 Implemented, 11 Partially implemented, 0 Not implemented** |
| Backend automated tests | **606 test cases passed** (mocked model; isolated databases) |
| Frontend automated tests | **112 tests passed** |
| Acceptance scenarios | **10 Pass — Automated, 2 Partial (AT05, AT12), 0 Fail, 0 Not Verified** |
| Manual checks by the owner | **Completed — Manually Tested by Owner:** task progress; V1 and V2 exports checked against their planning job IDs; resource edits with replanning |
| Demonstration and exports | **Owner-confirmed:** demonstration videos (including a 16-minute demonstration) and the B1 and B2 PDF and JSON exports |

The gaps below are what remains. Section A lists **missing or incomplete functionality**. Section B lists behaviour that is **implemented but not yet verified** (or only partly verified). Section C lists release and administrative items.
## Final totals

| Classification | Count | Requirements |
|---|---|---|
| Implemented | **12** | FR01, FR02, FR03, FR05, FR06, FR08, AI01, AI07, APP01, APP03, APP04, APP05 |
| Partially implemented (behaviour missing or incomplete) | **11** | FR04, FR07, FR09, FR10, AI02, AI03, AI04, AI05, AI06, APP02, APP06 |
| Not implemented | **0** | none |
| Cannot be established from source | **0** | none |

**Implemented and still awaiting verification: 3** (AI01 and AI07 await a real-model run; APP05 awaits a live process restart and a frontend test). The other 9 implemented requirements have captured passing automated runs.

**Acceptance tests:** 10 Pass — Automated (AT01, AT02, AT03, AT04, AT06, AT07, AT08, AT09, AT10, AT11), 2 Partial (AT05, AT12), 0 Fail, 0 Not Verified. Every Pass is a mocked-model automated result.

**How to read the tables.** Each row has four columns: the requirement or reference, the gap and why it matters (including what is still unverified), the recommended implementation and the evidence that closes it (marked "Owner decision" where it depends on the owner), and the evidence and source for the gap. Section A is missing or incomplete functionality, section B is behaviour that is implemented but not yet verified, and section C is release and administrative items.

## A. Development gaps (required behaviour missing or incomplete)

| Requirement / Reference | Gap and Why It Matters | Recommended Implementation and Evidence That Closes It | Evidence and Source |
|---|---|---|---|
| **D-01**: FR07, AI06; feedback 19 | No before-and-after comparison of revisions and no explanation of material changes. The revision history lists job IDs, status and time only. The reviewer cannot see what a replan changed, and AI06 also asks for a check of new instructions against earlier decisions. Why it matters: named in the mandatory "Application workflow" feedback area and in FR07. | Add a comparison endpoint over two revisions' candidate snapshots (placements, technicians, cost, time, deferred tasks) and show it in the draft screen, with tests. Closes with passing tests and a demonstration. | No comparison code in `src/` or `frontend/src` (searched); `DraftDetailScreen.tsx` revision history; [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md) FR07, AI06 |
| **D-02**: FR10; feedback 16 | The JSON and PDF exports omit assumptions, evidence references and deferred work, which FR10 requires. They hold IDs, approval time, task placements with technicians and status, and progress. **New finding in final verification**, and the reason FR10 was re-rated from Implemented. Why it matters: an explicit requirement and part of the mandatory "exports" feedback area. Still unverified: Whether the exported files match; they were not opened. | Extend `PlanExport` with the approved snapshot's assumptions, evidence references and deferral reasons, render them in the PDF, and add tests that the JSON and PDF agree. Closes with passing tests and a re-export. | `Infrastructure/Planning/PlanExportService.cs` (`PlanExport` record and `BuildExportAsync`); [API.md](API.md) export content |
| **D-03**: AI04; feedback 25 | No revise or repair step: validator violations are not sent back to the model, so the bounded repair loop the requirement describes (for example three attempts) does not exist. Only job attempts and tool calls are bounded. Why it matters: the feedback names "bounded validation and repair". Still unverified: Behaviour once built, with a real model. | After a validation failure, send the violations to the model for a bounded number of retries, then return a clear failure or a clarification request. Persist the attempt count. Closes with tests and a captured run. | `PlanningWorkflow.cs` (interpret, retrieve, propose, validate, no revise); no repair code in `src/` (searched); [AI_DESIGN.md](AI_DESIGN.md) section 7.2 |
| **D-04**: APP02; feedback 6 | No requires-review outcome when a resource is lost after approval. The Draft statuses are Pending (never assigned), Approved, Cancelled and Superseded, and the "Awaiting review" and "Changes requested" plan states are absent. Why it matters: a stated correctness requirement. Still unverified: Required behaviour. | Add a review state for an approved draft whose resources change, with a rule for when it is set. Closes with tests, or the owner accepts a documented definition. Owner decision. | [DATA_DICTIONARY.md](DATA_DICTIONARY.md) section 6; `Domain/Entities/Draft.cs` |
| **D-05**: AI02; feedback 23 | The candidate and draft APIs return evidence as source IDs only, so the excerpt, page and version kept internally cannot be opened by the user. No approval step for a similar-model manual. Why it matters: the feedback names "inspectable evidence". Still unverified: Whether a user can open a source. | Return the full evidence reference and show document, version, page, section and excerpt in the UI. Closes with tests and a screenshot. | `PlanningJobEndpoints.cs` (`GET .../candidates` returns `sourceId`); `AlternativesScreen.tsx` shows the ID; `PlanningContracts.cs` (`PlanningEvidenceReference`) |
| **D-06**: AI03; feedback 24 | `validate_schedule` and `calculate_cost` lack the replaced-plan context (completed tasks, held parts), tool calls are not recorded in the job result, and saved candidates last one attempt. A tool can report a plan feasible that approval later rejects. Still unverified: Effect with the real model. | Give both tools the job's replacement context, record tool calls in the result, and persist saved candidates across attempts. Closes with tests. Owner decision. | [AI_DESIGN.md](AI_DESIGN.md) sections 6 and 10; `ScheduleValidationTool.cs`, `CostCalculationTool.cs`, `ModelCandidateProposals.cs` |
| **D-07**: AI05 | Explanations are a fixed sentence per policy, sourced, calculated and assumed facts are not labelled, and assumptions are not shown in the UI. Still unverified: Explanation quality. | Generate the decision summary from the candidate data, label fact types, and show assumptions. | `DeterministicPlanningScheduler.cs` (policy rationale); matrix row AI05 |
| **D-08**: FR09 | Planning-request submission and clarification are not recorded as business actions (no actor or time). | Record both actions with the `X-Actor-Role` actor, with tests. Owner decision. | `BusinessActions` in `BusinessActor.cs`; `PlanningJobEndpoints.cs` |
| **D-09**: APP06 | No retry action or retry guidance for failed jobs. The UI shows the failure code, message and attempt count. Still unverified: Whether messages are actionable. | Show which failures are transient and offer a resubmit that keeps the request. Owner decision. | `PlanningRequestScreen.tsx`; matrix row APP06 |
| **D-10**: FR04 | No explicit statement when fewer than two alternatives exist, and trade-offs are the policy sentence plus a cost warning. | Show the alternative count and a short trade-off comparison. | `AlternativesScreen.tsx`; matrix row FR04 |
| **D-11**: DEL02, DEL03; requirements section 5 | No seed and reset command. Recreating only the database leaves the imported manuals, so the import script would stop at the manual step (derived from code, not run). Why it matters: the requirement says the reset "must recreate the same baseline locally". Still unverified: Whether a reset recreates the baseline. | Add a script that recreates the database and the manual directory, seeds, and optionally imports the PLANT-01 set; run it twice and capture the same baseline. | [DATA_DICTIONARY.md](DATA_DICTIONARY.md) section 10; `ManualImportService.cs` |

## B. Verification gaps (implemented or partly implemented, not adequately verified)

Tests already run: all backend test methods (606 test cases in eight focused groups, R-13 to R-19) and the frontend suite (112 tests, R-20) **passed** on 2026-10-29 with a mocked model and isolated databases. The items below are what those runs do not show.

| Requirement / Reference | Gap and Why It Matters | Recommended Implementation and Evidence That Closes It | Evidence and Source |
|---|---|---|---|
| **V-01**: AI03, AI01, AI07, FR01; feedback 24, 28 | No successful real-model end-to-end run. The one real-model run (R-12) failed with `OLLAMA_TIMEOUT` on CPU. The window-ID fix, `save_candidate` and approval of a model-saved candidate were never run with the real model. Why it matters: AI implementation is 20% of the evaluation. Still unverified: A tool-enabled run completing, its final interpretation validating, a saved candidate approved. | One owner-approved real-model run on faster hardware or with a longer timeout, captured as before, ending in a Succeeded job and an approval. Or accept in writing. Owner decision. | R-12; [AI_DESIGN.md](AI_DESIGN.md) section 9 |
| **V-02**: DEL07; requirements section 6 | No small real-model AI evaluation set. Why it matters: an explicit deliverable. Still unverified: Everything. | A small set of cases (valid, ambiguous, malformed output, unknown asset ID, missing evidence, timeout) with captured results, real model where feasible. | [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md) section 5 |
| **V-03**: DEL02; feedback 36 | A clean setup has not been run. The frontend dev, build and preview commands, the import script and fixture files on a fresh database, and the API start from nothing were not run in final verification. Why it matters: the requirement says to verify the documented commands from a clean local setup. Still unverified: Whether the documented commands work from an empty checkout. | Clone to an empty folder with a new database and manual directory, run only the documented commands, capture the transcript, and correct the README. Needs approval. | [README.md](../README.md) section 10 |
| **V-04**: AT05 | No single test removes a technician's availability after a lock exists and then replans through the job and worker. The decision UI was not verified. Still unverified: The end-to-end path. | Add an integration test for lock, availability change, replan, and the lock-conflict outcome. Owner decision. | AT05 row, [ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md) |
| **V-05**: AT12 | No automated test changes a duration or quantity. The live change at the demonstration is owner-reported and not verified here. `AT12_MANUAL_DEMO.md` is missing and the "75/75" claim in `AT12.txt` is unsupported. Still unverified: Duration and quantity changes. | Add tests for a duration and a quantity change, and either create the guide or remove the link. Owner decision. | AT12 row |
| **V-06**: AT09; APP05 | The restart test is an in-process host restart on a file database. No live process restart, and no frontend test of the disabled-submit state. Still unverified: Real restart; UI guard. | With approval, restart the running API during a job and capture the result. Add a Vitest test for the disabled button. Owner decision. | AT09 row; `PlanningRequestScreen.tsx` |
| **V-07**: AI03; section 3 of the requirements | No test for prompt-injection resistance. The prompt tells the model to treat request text as untrusted data, which is an instruction, not an enforced control. Still unverified: Behaviour under hostile request text. | Add tests whose request text tries to override the contract, with a mocked model, and the real model where feasible. Owner decision. | [AI_DESIGN.md](AI_DESIGN.md) section 8; tests searched |
| **V-08**: AI07 | No dedicated refusal test (non-JSON output is covered by the malformed-output tests), and no integration test for model-proposed unknown asset IDs. Still unverified: Those cases. | Add the two tests. | Matrix row AI07 |
| **V-09**: FR02 | The import script and the `fr02-demo` expected results were never run; the tests use their own inline data. Still unverified: Fixture files and script. | Run the script and the three demo files against a temporary database and capture the reports (part of V-03). Owner decision. | [DATA_DICTIONARY.md](DATA_DICTIONARY.md) section 8.5 |
| **V-10**: AT06, AT07 | The tests assert the final database state, but the logs print results only, and section 6 of the requirements asks to show that state. Still unverified: Printed final state. | Print the final state in the test output, or capture it from the running application. | AT06 and AT07 rows |
| **V-11**: DEL06 | No committed OpenAPI document and no live API response captured. [API.md](API.md) examples are illustrative. Still unverified: Live responses. | With approval, start the API in Development, export the OpenAPI document and capture a few real responses. | [API.md](API.md) section 11 |
| **V-12**: AT03 | The "similar-model manual" is covered through a different asset's manual and other-asset statements. No test uses a manual for another model of the same asset type. Still unverified: That exact case. | Add one test with such a manual. | AT03 row |
| **V-13**: DEL08 | Demonstration videos (including a 16-minute demonstration) and B1 and B2 exports are owner-confirmed and were not independently inspected. Naming note: repository export files use `V1_`/`V2_`; B1 and B2 are the owner's labels, and no file mapping is recorded. Still unverified: content, as the files were not inspected. | None required beyond keeping the naming note. | [SUBMISSION_HANDOVER.md](SUBMISSION_HANDOVER.md) section 7 |

## C. Release, documentation and administrative items

| Requirement / Reference | Gap and Why It Matters | Recommended Implementation and Evidence That Closes It | Evidence and Source |
|---|---|---|---|
| **R-01**: Feedback 35; DEL01 | Baseline commit `08748af` was created after the final verification runs, and the source and test results were reviewed against it. The final documents (including the manual-verification notes and consistency fixes) are in the documentation follow-up commit `7b32b28`, which follows it. Neither commit includes the database files, the deleted archived documents, 9 of the 12 recordings, 36 of the 53 screenshots, all 11 exports, or `AlternativeSelection.txt` (the other 3 recordings and 17 screenshots are tracked in earlier commits). Documentation edits made after `7b32b28` are uncommitted. Why it matters: part of the evidence the documents link to is in the working tree but in no commit. Still unverified: Whether the reviewer receives the evidence. | The owner decides how to package the evidence (the requirement suggests an `evidence/` folder) and adds it to a later commit or the archive. Owner decision. | [SUBMISSION_HANDOVER.md](SUBMISSION_HANDOVER.md) section 8; `git status` |
| **R-02**: DEL10 | Actual effort, approximate model cost, which code was reused or generated, and the AI-assistance disclosure are not recorded in the repository. Why it matters: mandatory handover content that only the owner can supply. Still unverified: All four facts; whether Copilot or other tools were used. | The owner supplies the facts and the handover is updated. | Handover section 10 |
| **R-03**: Requirements ("comply with applicable licences") | Licence review. iTextSharp 5.5.13.3 is used for PDF export and its package metadata links to the GNU AGPL page. There is no `LICENSE` file. Only a few packages' licences were looked up, from local metadata. No legal conclusion is drawn. Why it matters: a compliance obligation with an unknown outcome. Still unverified: How the terms apply to this submission; the other packages. | The owner or a legal adviser reviews the licences, then keeps, replaces or documents the PDF library, checks the other packages, and adds a `LICENSE` or notice file. Owner decision. | Handover section 10; `*.csproj`; local package metadata |
| **R-04**: Pre-submission checks | `maintenance-planner.db` (tracked, modified) and a stale tracked copy; the sidecars; five stray root files; 12 archived documents deleted in the working tree. None was changed or committed by this documentation work; the untracked recordings (9), screenshots (36) and exports (11) are also uncommitted. Why it matters: "remove unrelated files" applies. Still unverified: Contents (no database was opened). | Decide whether the seeded database ships; untrack or keep it knowingly; remove or archive the stray files; decide whether to commit the archived-document deletions. Owner decision. | `git status`; directory listing (names and sizes only) |
| **R-05**: Feedback 34 | The correction report (handover section 2) exists, but most causes are "not recorded". Still unverified: Causes. | The owner supplies the causes they know. Owner decision. | Handover section 2 |
| **R-06**: DEL10 | The Azure mapping is a proposal. Nothing is deployed or tested. Why it matters: the requirement asks for a mapping without deployment. Still unverified: Feasibility. | None required. | Handover section 9 |
| **R-07**: DEL01, DEL02 | Smaller items: no `.env.example` (no credentials are used), no NuGet lock file, no `global.json`, template leftovers (`/weatherforecast`, unused `ConnectionStrings:Maintenance`), Angular named in the original scope document while the app is React. Still unverified: Whether a reviewer needs them. | Add the files or state they are not needed. | [README.md](../README.md) section 10 |
| **R-08**: Section 1 of the requirements | Production-hardening limits: no authentication, self-declared roles, CORS allows any origin, currency `UNKNOWN`, SQLite single file. Why it matters: a proof of concept; each is documented. | None. | [ARCHITECTURE.md](ARCHITECTURE.md) §10; [API.md](API.md) section 2 |

## Manual verification: completed and still to do

Several verification items can only be done by hand in the running application. They are tracked here as manual items, not as test gaps.

| Manual item | Verification status |
|---|---|
| Task progress through the application | **Completed — Manually Tested by Owner** |
| Exports checked against the V1 and V2 planning job IDs | **Completed — Manually Tested by Owner.** The missing export content (D-02) still stands, because it comes from the export code. |
| Resource edits and replanning | **Completed — Manually Tested by Owner.** The missing before-and-after comparison (D-01) still stands unless the owner points to where it appears. |
| Demonstration videos (including a 16-minute demonstration) and B1 and B2 exports | **Owner-confirmed** |
| Real-model end-to-end planning run (V-01) | **Pending Verification**, manual; needs the owner's approval and faster hardware or a longer timeout |
| Clean setup from an empty checkout (V-03) | **Pending Verification**, manual |
| Import script and demo fixture files on a fresh database (V-09) | **Pending Verification**, manual |
| Live process restart during a job (V-06, AT09) | **Pending Verification**, manual |
| Live duration or quantity change (V-05, AT12) | **Pending Verification**, manual; the owner reports a prepared demonstration but did not itemise this change |
| Double submit in a browser (V-06, APP05) | **Pending Verification**, manual and quick |

## Prioritised view

**Mandatory items to fix or close with evidence:** D-01 (change comparison), D-02 (export content), D-03 (repair step), D-05 (inspectable evidence), D-11 (reset command), V-02 (AI evaluation set), V-03 (clean setup), R-02 (disclosures).

**Owner decisions:** D-04, D-06, D-08, D-09, V-01, V-04, V-05, V-06, V-07, V-09, R-01, R-03, R-04, R-05.

**Acceptable as documented limitations:** D-07, D-10, V-08, V-10, V-11, V-12, V-13, R-06, R-07, R-08.

## Dependencies between gaps

- Development gaps D-01 to D-11 change code. Any chosen must be finished and re-tested before the next commit, and the test logs refreshed.
- V-01, V-02 and V-07 depend on the same real-model decision.
- V-03 and V-09 are the same clean-setup run. D-11 (reset) should come first, so the run can use it.
- R-05 and the verification column of the handover depend on the test runs staying current.

## What this review did not check

- The content, duration or lineage of any recording, screenshot or export.
- The behaviour of the live API, the real model, a browser, or a clean setup.
- The licence terms of any package beyond the metadata links in the handover.
- The contents of any database.
