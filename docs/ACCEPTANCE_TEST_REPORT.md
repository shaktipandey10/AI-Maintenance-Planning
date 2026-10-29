# Acceptance Test Report

Consolidated results for the acceptance scenarios AT01–AT12 in [requirementsforpoc.md](requirementsforpoc.md), section 6 (lines 91–102). This is the single source of truth for acceptance-test status. Requirement status is in [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md). Test-run evidence logs are in `docs/DebugAndTests/`.

Repository state for the runs R-01 to R-12: HEAD `1376af5`, with uncommitted AI03 changes. Runs R-13 to R-20 (final verification) recorded HEAD `1376af5` with the uncommitted changes listed in each log header; the tested `src`, `tests` and `frontend` files matched those at baseline commit `08748af` (see section 10).

## 1. Status rules

| Status | Meaning |
|---|---|
| **Pass — Automated** | A captured run names the acceptance-level test(s), records its command and commit, shows zero failures, **and** the tests cover the whole §6 scenario. |
| **Pass — Manual** | The owner ran the whole section 6 scenario by hand and the result is recorded in section 11. No scenario holds this status at present, because the owner's manual checks were not itemised against a whole scenario. |
| **Partial** | Some evidence exists, but the run is incomplete (no command, commit or test names), or the tests cover only part of the scenario. |
| **Fail** | A captured run shows a failure for the scenario. |
| **Not Verified** | No captured run supports the scenario. Tests may exist in the repository, and development notes may claim a result, but neither counts as evidence. |

Rules that apply throughout:
- Related unit or integration tests passing do not make a scenario Pass when §6 requires broader verification.
- Narrative notes in `docs/DebugAndTests/` (for example `AT01.txt`) are development notes. They are cited as context only, never as results.
- Test IDs AT13–AT16 and the F-series test names appear in the repository, but §6 defines only AT01–AT12. They are not acceptance scenarios and are not counted here.

## 2. Summary

| Status | Count | Scenarios |
|---|---|---|
| Pass — Automated | 10 | AT01, AT02, AT03, AT04, AT06, AT07, AT08, AT09, AT10, AT11 |
| Pass — Manual | 0 | none |
| Partial | 2 | AT05, AT12 |
| Fail | 0 | — |
| Not Verified | 0 | none |

**Status after the final verification runs (R-13 to R-20):** 10 Pass — Automated, 2 Partial (AT05, AT12), 0 Fail, 0 Not Verified. Every Pass is an automated result with a mocked or fixed model interpretation and isolated databases; none is a real-model result. Before those runs the report had 0 Pass, 3 Partial (AT06, AT07, AT10) and 9 Not Verified; that earlier state remains in the run register (R-01 to R-12) and in section 7, which describes it.

## 3. Scenario results

| ID | Requirement (§6, short) | Test type | Automated tests in repository | Evidence-backed actual result | Status | Evidence | Gaps and limitations |
|---|---|---|---|---|---|---|---|
| AT01 | Baseline: five assets, three technicians; two distinct feasible alternatives with totals and source references. | Automated, integration | `AT01_FiveAssets_GeneratesTwoDistinctFeasibleAlternatives`; `AT01_AllRequestedTasksAreScheduled`; `AT01_EvidenceReferencesContainSourceDocumentDetails` (`AT01_FiveAssetAcceptanceTests.cs`). Related: `FiveAssetRequest_ProducesTwoDistinctValidatedAlternativesWithTotalsAndEvidence` (`FR03FiveAssetPlanningFileDatabaseTests.cs`). | R-13, R-14: `AT01_FiveAssets_GeneratesTwoDistinctFeasibleAlternatives`, `AT01_AllRequestedTasksAreScheduled`, `AT01_EvidenceReferencesContainSourceDocumentDetails` and `FiveAssetRequest_ProducesTwoDistinctValidatedAlternativesWithTotalsAndEvidence` passed. | Pass — Automated | [AT01.txt](DebugAndTests/AT01.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Workflow_IntegrationClasses.txt](DebugAndTests/Final_Workflow_IntegrationClasses.txt) | Passed with a mocked or fixed interpreter and an in-memory or temporary database. No real-model run of the baseline. `AT01.txt` is still a narrative note, not a result. |
| AT02 | Clarification: omit window or scope; ask for the missing information before the plan is ready for approval. | Automated, integration and unit | `SubmitClarification_WithoutWindowId_ReturnsBadRequest`; `EndToEnd_ClarificationWorkflow_PreserveRequestAndResetJob` (`RequestToAlternativesWorkflowTests.cs`). Unit: `ExecuteAsync_MissingAssetOrWindow_ReturnsClarification` (`PlanningWorkflowTests.cs`). | R-13 to R-15: `SubmitClarification_WithoutWindowId_ReturnsBadRequest`, `EndToEnd_ClarificationWorkflow_PreserveRequestAndResetJob`, `ExecuteAsync_MissingAssetOrWindow_ReturnsClarification`, `ExecuteAsync_UnresolvedScopeClarifiesWithoutInventingTasksOrQueryingResources` and `AT10_ClarificationRequiredResultCannotBeApproved` (no plan can be approved while clarification is required) passed. | Pass — Automated | [AT02.txt](DebugAndTests/AT02.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Unit_Classes.txt](DebugAndTests/Final_Unit_Classes.txt) | Mocked interpretation: whether the real model flags an omitted scope is not verified. |
| AT03 | Evidence conflict: identify the conflict or exclude the irrelevant source; do not silently select it. | Automated, integration | `AT03_GenuineConflict_StillBlocksPlanning`; `AT03_ScopeBoundaryStatement_DoesNotBlockPlanning`; `AT03_ScopeBoundaryStatement_ProducesCandidates` (`AT03_ConflictPersistenceAndApiTests.cs`) | R-13, R-15: `AT03_GenuineConflict_StillBlocksPlanning`, `AT03_ScopeBoundaryStatement_DoesNotBlockPlanning`, `AT03_ScopeBoundaryStatement_ProducesCandidates`, and unit tests `ExecuteAsync_CrossDocumentStatementNamingOnlyOtherAssets_IsNotAConflictForThisAsset` and `ExecuteAsync_UnsupportedP102MappingForP101RequestRequiresClarification` passed. A genuine conflict is identified, and an irrelevant other-asset manual is excluded instead of being silently selected. | Pass — Automated | [AT03.txt](DebugAndTests/AT03.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Unit_Classes.txt](DebugAndTests/Final_Unit_Classes.txt) | The "similar-model manual" is covered through the P-102 inspection-only manual and other-asset statements. No test uses a manual for a different model of the same asset. The conflict check is a fixed keyword list. |
| AT04 | Infeasibility: four-hour P-101 window with unavailable bearing; reject full replacement; explain both blockers. | Automated, integration | `AT04_UnavailableBearingAndReducedWindow_PersistedAsInfeasibleWithBothViolations`; `AT04_Infeasibility_VisibleViaGetJobApi` (`AT04_InfeasibilityTests.cs`) | R-13: `AT04_UnavailableBearingAndReducedWindow_PersistedAsInfeasibleWithBothViolations` and `AT04_Infeasibility_VisibleViaGetJobApi` passed. | Pass — Automated | [AT04.txt](DebugAndTests/AT04.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt) | Mocked interpretation. Needed a test-fixture path fix to run (section 10). |
| AT05 | Replanning: remove a technician while a task is locked; keep the lock if feasible, otherwise expose the conflict and request a decision. | Automated, integration | `AT05_LockCreatedOnApproval_AndPersistedInDatabase`; `AT05_HistoricalLocksPreserved_WhenNewDraftApproved`; `AT05_IdempotentApproval_DoesNotDuplicateLocks` (`AT05ReplanningTests.cs`) | R-13, R-15: `AT05_LockCreatedOnApproval_AndPersistedInDatabase`, `AT05_HistoricalLocksPreserved_WhenNewDraftApproved`, `AT05_IdempotentApproval_DoesNotDuplicateLocks`, and unit tests `ScheduleAsync_LockedTechnicianAvailable_SchedulesWithLockedAssignment` (lock preserved) and `ScheduleAsync_LockedTechnicianUnavailable_ReturnsInfeasibleWithLockConflict` (conflict exposed) passed. | Partial | [AT05.txt](DebugAndTests/AT05.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Unit_Classes.txt](DebugAndTests/Final_Unit_Classes.txt) | No single test removes a technician's availability after a lock exists and then replans through the job and worker. The "request a decision" step is the Infeasible outcome with a lock-conflict violation; the decision UI was not verified. |
| AT06 | Concurrency: two plans need the final unit of a part in parallel; exactly one succeeds; the other gets a conflict; stock is not over-reserved. | Automated, integration (file database and HTTP) | `AT06_TwoParallelApprovalsCompeteForFinalBR101_OnlyOneSucceeds`; `AT06_TrueParallelApprovalsViaHttpApi_ExactlyOneSucceedsWithFinalBR101` (`AT06ConcurrentApprovalTests.cs`) | R-13, R-14: `AT06_TwoParallelApprovalsCompeteForFinalBR101_OnlyOneSucceeds`, `AT06_TrueParallelApprovalsViaHttpApi_ExactlyOneSucceedsWithFinalBR101`, `AT06_ConcurrentApprovalsRemainDeterministic_RepeatedRunsProduceSameOutcome` and `ParallelApprovalsForLastUnit_OnFileDatabase_ExactlyOneReserves_LosersLeaveNoSideEffects` passed. | Pass — Automated | [CancellationConcurrency.txt](DebugAndTests/CancellationConcurrency.txt); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Workflow_IntegrationClasses.txt](DebugAndTests/Final_Workflow_IntegrationClasses.txt) | The tests assert the final database state (one approved draft, one reservation, reserved quantity 1), but the log prints test results, not that state. `CancellationConcurrency.txt` remains a historical narrative. |
| AT07 | Retry: repeat approval and cancellation; no duplicate reservation and no double release. | Automated, integration | `AT07_RepeatApprovalIsIdempotent_NoDuplicateDraftOrReservation`; `AT07_RepeatCancellationIsIdempotent_InventoryReleasedOnce` (`AT07IdempotentApprovalTests.cs`) | R-13: `AT07_RepeatApprovalIsIdempotent_NoDuplicateDraftOrReservation` and `AT07_RepeatCancellationIsIdempotent_InventoryReleasedOnce` passed. | Pass — Automated | [CancellationConcurrency.txt](DebugAndTests/CancellationConcurrency.txt); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt) | Final state is asserted by the tests, not printed in the log. No AT07 development note exists. |
| AT08 | Replacement: approve V1; draft V2; force a reservation conflict on V2 approval; V1 unchanged and its reservations consistent. | Automated, integration | `AT08_ReplacementDraftV2FailsOnConflict_V1RemainsApprovedAndUnchanged` (`AT08DraftReplacementConflictTests.cs`) | R-13, R-14: `AT08_ReplacementDraftV2FailsOnConflict_V1RemainsApprovedAndUnchanged` and `FailedReplacement_StockShortfall_PersistsNothingForV2_AndLeavesV1Unchanged` passed. | Pass — Automated | None. No AT08 note or log exists.; captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Workflow_IntegrationClasses.txt](DebugAndTests/Final_Workflow_IntegrationClasses.txt) | No AT08 development note exists. |
| AT09 | Recovery: interrupt planning, restart, reopen; resume or restart safely with one coherent result and no duplicated effects. | Automated, integration (in-process restart) | `AT09_CheckpointPersistenceAndRecovery_JobRecoveredWithCheckpoint`; `AT09_LeaseExpirationPreventsStaleWorkerCompletion` (`AT09JobRecoveryTests.cs`) | R-13, R-14: `AT09_CheckpointPersistenceAndRecovery_JobRecoveredWithCheckpoint`, `AT09_LeaseExpirationPreventsStaleWorkerCompletion`, `Worker_RecoversExpiredRunningJobAcrossFileBackedHostRestart` and `RestartAfterCheckpoint_ResumesWithoutReinterpreting_AndSucceeds` passed. | Pass — Automated | [AT09.txt](DebugAndTests/AT09.txt), [JobCheckPoints.txt](DebugAndTests/JobCheckPoints.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Workflow_IntegrationClasses.txt](DebugAndTests/Final_Workflow_IntegrationClasses.txt) | The restart is an in-process host restart on a file database. A real process kill and restart of the running application was not performed (it needs the live API). |
| AT10 | Failure: simulate model timeout, malformed output and inventory lookup failure; distinct, actionable states; no invalid plan approved. | Automated, unit and integration | Unit: `AT10_ModelTimeoutProducesDistinctOllamaTimeoutFailure`; `AT10_MalformedModelOutputProducesDistinctOllamaInvalidOutputFailure`; `AT10_InventoryDataUnavailableProducesDataUnavailableOutcome`. Integration: `AT10_FailedJobResultCannotBeApproved`; `AT10_InfeasibleResultCannotBeApproved`; `AT10_DataUnavailableResultCannotBeApproved`; `AT10_ClarificationRequiredResultCannotBeApproved`; `AT10_JobStateConsistentAfterFailure` (`AT10ErrorHandlingIntegrationTests.cs`) | R-13, R-14, R-15: unit `AT10_ModelTimeoutProducesDistinctOllamaTimeoutFailure`, `AT10_MalformedModelOutputProducesDistinctOllamaInvalidOutputFailure`, `AT10_InventoryDataUnavailableProducesDataUnavailableOutcome`; integration `AT10_FailedJobResultCannotBeApproved`, `AT10_InfeasibleResultCannotBeApproved`, `AT10_DataUnavailableResultCannotBeApproved`, `AT10_ClarificationRequiredResultCannotBeApproved`, `AT10_JobStateConsistentAfterFailure` passed. | Pass — Automated | `test_at10_all.txt`. Narrative: [AT10.txt](DebugAndTests/AT10.txt).; captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Unit_Classes.txt](DebugAndTests/Final_Unit_Classes.txt) | Model timeout and malformed output are simulated with a mocked HTTP handler (labelled, as section 6 of the requirements allows). The real-model timeout (R-12) is consistent with the timeout state but is not this simulation. `test_at10_all.txt` stays a historical, unnamed run. |
| AT11 | Progress and export: complete one task, revise remaining work, reload, export; completed work preserved; PDF and JSON match the chosen approved revision. | Automated, integration | `AT11_MarkTaskComplete_PersistsCompletionState`; `AT11_ReloadPlan_VerifiesCompletionPersists`; `AT11_ExportJson_And_Pdf_MatchContent` (`AT11PlanProgressIntegrationTests.cs`) | R-13, R-14: `AT11_MarkTaskComplete_PersistsCompletionState`, `AT11_ReloadPlan_VerifiesCompletionPersists`, `AT11_ExportJson_And_Pdf_MatchContent` and `Replanning_DoesNotScheduleTasksCompletedOnV1_AndV2ReservesNoPartForThem` passed. | Pass — Automated | [AT11.txt](DebugAndTests/AT11.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt), [Final_Workflow_IntegrationClasses.txt](DebugAndTests/Final_Workflow_IntegrationClasses.txt) | The steps are covered by separate tests, not one end-to-end test. The exports hold tasks, timings, technicians, status and progress only; assumptions, evidence and deferred work are not exported (requirement FR10). Also **Completed — Manually Tested by Owner:** task progress, the exports against the V1 and V2 job IDs (job-ID match only; FR10 stays Partially implemented), and replanning after resource edits (section 11). |
| AT12 | Unseen change: modify a duration, quantity or availability interval during the live demo; the general logic still works. | Automated, integration; live demonstration (manual) | `AT12_ReducedTechnicianAvailability_BecomesInfeasible`; `AT12_PartiallyRestrictedAvailability_StillFeasibleWithDifferentCrew`; `AT12_AvailabilityChangeIsDrivenByInputData_NotHardcodedScenario` (`AT12TechnicianAvailabilityChangeTests.cs`) | R-13: `AT12_ReducedTechnicianAvailability_BecomesInfeasible`, `AT12_PartiallyRestrictedAvailability_StillFeasibleWithDifferentCrew` and `AT12_AvailabilityChangeIsDrivenByInputData_NotHardcodedScenario` passed (availability interval only). | Partial | [AT12.txt](DebugAndTests/AT12.txt) (narrative only); captured: [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt) | No automated test changes a duration or a quantity. The live change at the demonstration is owner-reported and was not verified by this review. `AT12_MANUAL_DEMO.md` is still missing, and the "75/75" claim in `AT12.txt` is still unsupported. |

Per-scenario notes:
- **Test type** reflects the test classes in the repository. A live demonstration is not automated and has not been performed.
- **Fixtures** are seeded inside each test class (for example `AT01_FiveAssetSeed` for AT01), or come from the anchor seed. They are not separate data files, and they are not listed here beyond the class names.

## 4. Run register (append-only)

Each row records one run. New runs are added at the bottom, and existing rows are never edited or removed. Scenario statuses in section 3 are updated only from rows that name the tests, the command and the commit.

| Run | Captured | Commit | Command | Log | Result | Relevance |
|---|---|---|---|---|---|---|
| R-01 | Not recorded | Not recorded | Not recorded | [CancellationConcurrency.txt](DebugAndTests/CancellationConcurrency.txt) | Summary: 10 passed, 0 failed, 10 total. Narrative names AT06 and AT07 tests. | AT06, AT07 (Partial) |
| R-02 | Not recorded | Not recorded | Not recorded | [AT14.txt](DebugAndTests/AT14.txt) | 8 passed, 0 failed (candidate retrieval) | Not an AT scenario in §6; listed for completeness |
| R-03 | Not recorded | Not recorded | Not recorded; filter absent | `test_at10_all.txt` (repository root) | Raw output: 5 passed, 0 failed, 5 total. No test names in the file. | AT10 (Partial) |
| R-04 | Not recorded | Not recorded | Not recorded | `test_output.txt` (repository root) | Raw output: 1 passed, 0 failed. No test names in the file. | No AT mapping possible |
| R-05 | Not recorded | Not recorded | Filter expression in the log; the filter is invalid | `test_regressions.txt` (repository root) | Incomplete: "Invalid Condition" error; "No test matches the given testcase filter". **No tests executed.** | None |
| R-06 | 2026-10-29 10:29:21 +05:30 | `1376af5` + uncommitted | Recorded in log | [AI03_Stage3_ToolLoop_UnitTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_UnitTests.txt) | 155 passed, 0 failed (mocked model) | AI03 only |
| R-07 | 2026-10-29 10:29:26 +05:30 | `1376af5` + uncommitted | Recorded in log | [AI03_Stage3_ToolLoop_IntegrationTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_IntegrationTests.txt) | 27 passed, 0 failed (mocked model) | AI03 only |
| R-08 | 2026-10-29 10:29:45 +05:30 | `1376af5` + uncommitted | Recorded in log | [AI03_Stage3_PreexistingFailure_OllamaPlanningJobIntegrationTests.txt](DebugAndTests/AI03_Stage3_PreexistingFailure_OllamaPlanningJobIntegrationTests.txt) | 2 failed (pre-existing fixture problem) | Known failing tests; not AT |
| R-09 | 2026-10-29 11:25:22 +05:30 | `1376af5` + uncommitted | Recorded in log | [AI03_WindowIdFix_Tests.txt](DebugAndTests/AI03_WindowIdFix_Tests.txt) | 32 passed and 4 passed, 0 failed (mocked model) | AI03 only |
| R-10 | 2026-10-29 11:31:10 +05:30 | `1376af5` + uncommitted | Recorded in log | [AI03_Stage4_SaveCandidate_Tests.txt](DebugAndTests/AI03_Stage4_SaveCandidate_Tests.txt) | 165 passed and 60 passed, 0 failed (mocked model) | AI03 only |
| R-11 | 2026-10-29 11:44:33 +05:30 | `1376af5` + uncommitted | Recorded in log | [AI03_ApprovalPath_Verification.txt](DebugAndTests/AI03_ApprovalPath_Verification.txt) | 23 passed and 4 passed, 0 failed (mocked model). Approval-path verification (E-AI03-11). | Approval path of saved candidates; not an AT scenario |
| R-12 | 2026-10-29 10:57–11:10 +05:30 | `1376af5` + uncommitted | Real API and Ollama, one planning request | [AI03_Stage3_RealModelRun.txt](DebugAndTests/AI03_Stage3_RealModelRun.txt) | Real model. Job **Failed**, `OLLAMA_TIMEOUT`, after 2 attempts. Tool calls ran; no interpretation was accepted. | AI evaluation (section 5) |
| R-13 | 2026-10-29 14:56:23 to 14:56:41 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_AT01-AT12_IntegrationClasses.txt](DebugAndTests/Final_AT01-AT12_IntegrationClasses.txt) | **50 passed, 0 failed.** The 12 acceptance-scenario integration classes (AT01 to AT12 tests). Mocked or fixed interpretation; isolated databases. | AT01 to AT12 |
| R-14 | 2026-10-29 14:56:59 to 14:57:53 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_Workflow_IntegrationClasses.txt](DebugAndTests/Final_Workflow_IntegrationClasses.txt) | **233 passed, 0 failed.** Critical-workflow integration classes: imports, planning jobs, approval, edits and locks, replacement, progress, export, recovery, history, read endpoints. | AT01 to AT12; FR and APP requirements |
| R-15 | 2026-10-29 14:58:08 to 14:58:15 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_Unit_Classes.txt](DebugAndTests/Final_Unit_Classes.txt) | **143 passed, 0 failed.** Unit classes for interpretation, workflow, validation, scheduling, cost, error handling, import reading and evidence retrieval. Mocked model. | AT02, AT03, AT05, AT10; AI01, AI07; APP01 |
| R-16 | 2026-10-29 14:58:15 to 14:58:30 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_AI03_IntegrationClasses.txt](DebugAndTests/Final_AI03_IntegrationClasses.txt) | **38 passed, 0 failed.** AI03 integration classes. **Mocked model.** | AI03 (not real-model evidence) |
| R-17 | 2026-10-29 14:58:31 to 14:58:35 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_AI03_UnitClasses.txt](DebugAndTests/Final_AI03_UnitClasses.txt) | **116 passed, 0 failed.** AI03 unit classes. **Mocked model.** | AI03 (not real-model evidence) |
| R-18 | 2026-10-29 15:03:49 to 15:03:55 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_Remaining_IntegrationClasses.txt](DebugAndTests/Final_Remaining_IntegrationClasses.txt) | **14 passed, 0 failed.** Task-edit persistence and submission classes. | FR05 |
| R-19 | 2026-10-29 15:03:56 to 15:03:59 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log | [Final_Remaining_UnitClasses.txt](DebugAndTests/Final_Remaining_UnitClasses.txt) | **12 passed, 0 failed.** Workflow result mapping and early plan-progress model classes. | Supporting |
| R-20 | 2026-10-29 14:58:48 to 15:00:18 +05:30 | Recorded in log header: `1376af5` + uncommitted changes in `src`, `tests` and `frontend` | Command in log (`npx vitest run`) | [Final_Frontend_Vitest.txt](DebugAndTests/Final_Frontend_Vitest.txt) | **112 passed in 9 files, 0 failed.** Frontend unit tests; the API client is mocked. | Frontend behaviour |

## 5. AI evaluation summary

| Item | Status | Evidence and limitation |
|---|---|---|
| AI03 real-model end-to-end run | Owner Performed Manual Test | R-12: real tool calls executed within the limit, but the job failed on CPU-only inference exceeding the 180-second timeout. No successful end-to-end run exists. |
| AI03 mocked-model tests | Recorded | R-06, R-07, R-09, R-10, R-11, R-16, R-17. These use scripted model replies and are **not** real-model evidence. |
| Small real-model AI evaluation set (§6, DEL07) | **Not verified** (not created) | No evaluation cases or results exist. This is a missing deliverable. |

AI03 remains Partially implemented. Successful real-model end-to-end verification has not been demonstrated.

## 6. Future test cases

Use this structure when adding a test case or a new execution.

- **New scenario:** add a row to section 3 only if it is an acceptance scenario defined in the source requirements. Do not invent scenario IDs or acceptance criteria. Record the requirement text and its source line.
- **New run:** append a row to section 4 with the next run ID, the capture time, the commit, the exact command, the log path, and the result. Do not edit earlier rows.
- **Status change:** update the scenario's status in section 3 only from a run row that names the tests and records a command and commit. Keep the earlier status in the run register, so history is preserved.
- **Evidence:** a development note or narrative claim is never a result. Store captured output under `docs/DebugAndTests/` and reference it by path.
- **Failures:** record them with their output. A failing run is never replaced by a later passing run; both stay in the register.

## 7. Limitations, contradictions and missing evidence

> **Historical (written before the final verification runs).** Items 1, 3, 7, 8 and 9 below describe the evidence position at R-01 to R-12. Section 10 records what the final verification runs changed. Items 2, 4, 5, 6 and 11 to 13 still apply.

1. **No captured run names the acceptance tests** for AT01–AT05, AT08, AT09, AT11 and AT12, so nine scenarios are Not verified.
2. **Narrative claims are not corroborated.** `AT12.txt` claims 75/75 across AT01–AT12. `AT01.txt` and others claim all tests pass. No captured output in the repository supports those claims, and they are not used as results.
3. **Commit and command are missing** from every run except the AI03 runs (R-06 to R-12). R-01 to R-05 cannot be tied to any commit.
4. **Missing reference:** `AT12.txt` links to `AT12_MANUAL_DEMO.md`, which does not exist in the repository.
5. **Missing development notes:** AT06, AT07 and AT08 have no `docs/DebugAndTests/` note.
6. **AT12 live change** has not been performed, so the demonstration half of AT12 is unverified.
7. **AT05 scenario** (technician removed while locked) is not named by any test in the repository.
8. **AT03 scenario** (similar-model manual) is not named by any test in the repository.
9. **AT11 scenario** ("revise remaining work") is not named by any test in the repository.
10. **Internal test IDs** AT13–AT16 and the F-series appear in the repository but are not §6 acceptance scenarios, so they are excluded.
11. **Repository-root test output** (`test_output.txt`, `test_at10_all.txt`, `test_regressions.txt`, and `generate_p102_pdf.cs`) is stray working output. It is listed here as evidence only and should be resolved in the final cleanup decision.
12. **Recordings** in `docs/Recordings/` have not been watched. They are not used as acceptance results.
13. **Superseded reference:** `ACCEPTANCE_TEST_CATALOGUE.md` was archived and is not used. §6 of the requirements is the authoritative catalogue.

## 8. Next actions to reach Pass

> **Partly superseded.** Actions 1 to 3 were carried out in final verification for the focused test classes (R-13 to R-20, section 10). Actions 4 and 5 are still open for AT05 and AT12, and the live and real-model checks listed at the end of section 10 are still needed.

Each action needs approval before it is run:
1. Run each acceptance test class at one fixed commit, with the command and commit recorded. Start with AT01, AT02, AT03, AT04, AT05, AT08, AT09, AT11 and AT12.
2. Run AT06 and AT07 again, capturing the final database state that §6 requires.
3. Re-run AT10 with a recorded filter so its five tests can be named.
4. Decide how AT12's live change will be demonstrated, and either create the missing `AT12_MANUAL_DEMO.md` or remove the reference.
5. Add the missing scenario tests for AT03 (similar-model manual), AT05 (technician removed while locked) and AT11 (revise remaining work), or record those scenarios as not covered.

## 9. Review note (documentation consolidation, additive)

Added without changing any status, row or run record above.

- **Counts at the time of this note:** 0 Pass, 3 Partial (AT06, AT07, AT10), 0 Fail, 9 Not Verified. No new run was captured, and no test was run for this note. Section 10 and runs R-13 to R-20 supersede these counts.
- **Static checks performed:** all 38 test identifiers cited in section 3 exist in `tests/`, and every `docs/DebugAndTests/` log and root file cited in this report exists. This shows the references resolve. It does not show any test passes.
- **AT12:** `docs/DebugAndTests/AT12_MANUAL_DEMO.md` is still missing, and the "75/75" claim in `AT12.txt` is still unsupported by any captured output.
- **AT06 and AT07 evidence:** `CancellationConcurrency.txt` is a narrative log with no command, date or commit. Its AT06 narrative names `AT06_TwoParallelApprovalsCompeteForFinalBR101_OnlyOneSucceeds` and a sequential-approval test that is not in the current source. It does not name `AT06_TrueParallelApprovalsViaHttpApi_ExactlyOneSucceedsWithFinalBR101`. Test source for the concurrent approvals exists (`AT06ConcurrentApprovalTests.cs`), but no captured run supports it, so AT06 stays Partial.
- **Tests cited as AI01 evidence:** the two `OllamaPlanningJobIntegrationTests` that the traceability documents cite for clarification and candidate generation are the tests that fail in R-08 (fixture problem). They do not currently support AI01.
- **Stray files:** `test_output.txt`, `test_at10_all.txt`, `test_regressions.txt` and `generate_p102_pdf.cs` are still in the repository root.

## 10. Final verification runs (appended)

Focused runs R-13 to R-20 were captured on 2026-10-29 between 14:56 and 15:03 +05:30. Nothing was run against the live API, the original database or Ollama.

**Method and safeguards.** Each backend run used `dotnet test --no-build` with a filter naming test classes. Every test fixture builds its own in-memory or temporary database. As an additional guard, the runner set process-scoped `ConnectionStrings__MaintenancePlanner` and `ManualImports__Directory` overrides pointing at a temporary folder and `Ollama__EnableTools=false`. The original `maintenance-planner.db` and its sidecars were not opened by any command. Their sizes and timestamps were unchanged afterwards.

**Coverage.** Together, the eight backend logs ran every test method that `dotnet test --list-tests` reports for the unit and integration projects (465 distinct class-and-method names, 606 test cases counting theory data). All passed. No single full-suite command was issued; the union of the focused groups covered every listed test method. No run used the real model.

**Test-fixture fix made before the runs.** `AT03_ConflictPersistenceAndApiTests`, `AT04_InfeasibilityTests` and `OllamaPlanningJobIntegrationTests` located the repository root by looking for `requirementsforpoc.md` at the root, but the file has been under `docs/` since commit `8d0568f`. They could not run, which is the failure recorded as R-08. Each fixture now accepts either `docs/requirementsforpoc.md` or the root path (the same check `AI03ToolLoopIntegrationTests` already used). No application code changed. After the fix, the two `OllamaPlanningJobIntegrationTests` pass (they use a fake Ollama handler, so they are mocked), and AT03 and AT04 ran for the first time in a captured run. R-08 remains in the register as the historical failure.

**What the Pass statuses do not show.**
- No scenario was verified with the real model. R-12 (real model) still ended in `OLLAMA_TIMEOUT`, and **a successful real-model end-to-end run has not been demonstrated.** The small real-model AI evaluation set required by section 6 of the requirements still does not exist.
- AT05 and AT12 stay Partial for the reasons in their rows.
- AT09 is an in-process restart; a live process restart was not run.
- The live demonstration steps of AT12 and the recordings are owner-reported deliverables and were not inspected.
- Frontend behaviour was run only through unit tests with a mocked API client (R-20). Browser-level behaviour was not run.

**Remaining verification needed (all need the live API, the original database or Ollama, so not run in final verification):** a real-model end-to-end planning run through approval; a live process restart for AT09; a live duration or quantity change for AT12; a clean-setup run from an empty checkout.

## 11. Manual verification reported by the owner (appended)

These checks were carried out by the owner in the running application and reported on 2026-10-29. The assistant did not repeat or inspect them, so they are recorded as owner-reported. They are not automated runs and are not in the run register. The automated results in section 3 are unchanged by them.

| Check | Verification | Related scenario or requirement |
|---|---|---|
| Task progress | **Completed — Manually Tested by Owner** | AT11 (progress); FR08 |
| V1 and V2 exports checked against their planning job IDs (job-ID match only; FR10 stays Partially implemented) | **Completed — Manually Tested by Owner** | AT11 (export); FR10 |
| Resource edits followed by replanning | **Completed — Manually Tested by Owner** | AT11 (revise remaining work); FR05, FR07 (replanning) |
| Demonstration videos (including a 16-minute demonstration) and B1 and B2 exports | **Owner-confirmed deliverables** | Feedback points 38 and 39 |

AT11 is already **Pass — Automated**; these checks add manual confirmation of the same steps. AT12 stays **Partial**: the owner's resource edits were not itemised as a duration, quantity or availability change at a live demonstration, so the AT12 criterion is not marked as met by manual testing. If one of those edits was the AT12 change, the owner should say which, and the status can be revisited.