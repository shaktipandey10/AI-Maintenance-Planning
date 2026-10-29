# AI Requirements Traceability

Source: [requirementsforpoc.md](requirementsforpoc.md), section 3, "AI requirements" (AI01–AI07). The related feedback-specific findings are points 22–28 in [FEEDBACK_TRACEABILITY.md](FEEDBACK_TRACEABILITY.md), which refers detailed AI traceability to this document.

- **Integration tests** are in `tests/MaintenancePlanner.IntegrationTests/` and are cited as `File.cs` › `Method`. No test results are claimed except the dated focused runs recorded in the AI03 note.
- **Mocked model:** every integration test that touches interpretation uses a fake Ollama HTTP handler, a controlled interpreter or a fixed workflow. None of them shows real-model behaviour.
- **Evidence:** screenshots and recordings were not inspected unless marked *(inspected)*, and recordings were not watched.
- **Paths:** `src/…` paths are shortened as `Api/…`, `Application/…`, `Domain/…` and `Infrastructure/…`, meaning `src/MaintenancePlanner.<Project>/…`.
- **Gap labels:** Implementation gap, Test gap, Evidence gap and Verification needed. Each row's details are in the note with the same ID below the table.

| Requirement ID | Original requirement | Implementation location | Relevant integration tests | Supporting evidence | Gap / verification needed |
|---|---|---|---|---|---|
| AI01 | Structured interpretation. Convert the request into validated structured fields. Ask a focused follow-up question when a mandatory value is missing. Do not infer urgency or equipment compatibility without a stated rule or supporting data. | `Infrastructure/Planning/OllamaPlanningRequestInterpreter.cs` › `InterpretAsync`, `ParseInterpretation`, `SystemPrompt`; `Application/Planning/PlanningWorkflow.cs` › `ExecuteAsync` (request and interpretation checks, `Clarification`); `Application/Planning/PlanningContracts.cs` › `PlanningInterpretation`, `PlanningClarificationRequiredResult`; `Api/Endpoints/PlanningJobEndpoints.cs` › `POST /api/planning-jobs/{id}/clarify`; `frontend/src/screens/PlanningRequestScreen.tsx` | `OllamaPlanningJobIntegrationTests.cs` › `ApiSubmission_RunsOllamaWorkflowAndPersistsClarificationOutcomeForAmbiguousScope`, `ApiSubmission_RunsOllamaWorkflowAndPersistsCandidatesGenerated` (mocked model); `RequestToAlternativesWorkflowTests.cs` › `EndToEnd_ClarificationWorkflow_PreserveRequestAndResetJob`, `EndToEnd_ClarificationResumeWithWorker_GeneratesCandidatesAfterClarification` | [Clarification_screen.PNG](Screenshots/Clarification_screen.PNG), [Clarification_screen2.PNG](Screenshots/Clarification_screen2.PNG): clarification prompts in the UI; [Testing_Clarificationworkflow.txt](DebugAndTests/Testing_Clarificationworkflow.txt), [AT02.txt](DebugAndTests/AT02.txt): manual and test notes | Verification needed; Evidence gap |
| AI02 | Grounded retrieval. Retrieve asset-specific task instructions from the uploaded manuals and relevant history. Retain document ID, version, page or section and the supporting excerpt. Users must be able to open or inspect the evidence. Do not use a similar asset manual as an exact match without explicit approval. | `PlanningWorkflow.cs` › `RetrieveEvidenceAsync`; `Infrastructure/ManualEvidence/PdfManualEvidenceRetriever.cs`, `ImportedManualEvidenceRetriever.cs`, `TextManualEvidenceRetriever.cs`, `CompositeManualEvidenceRetriever.cs` › `SearchAsync`; `PlanningContracts.cs` › `PlanningEvidenceReference`; `PlanningJobEndpoints.cs` › `GET /api/planning-jobs/{jobId}/candidates`; `frontend/src/screens/AlternativesScreen.tsx` (Evidence References) | `FR02DataImportFileDatabaseTests.cs` › `TextBasedPdfManual_IsImported_CitedByPageAndSection_AndUsableAsATaskSource`; `AT01_FiveAssetAcceptanceTests.cs` › `AT01_EvidenceReferencesContainSourceDocumentDetails`; `AT03_ConflictPersistenceAndApiTests.cs` › `AT03_GenuineConflict_StillBlocksPlanning`, `AT03_ScopeBoundaryStatement_DoesNotBlockPlanning` | [Alternatives Available.PNG](<Screenshots/Alternatives Available.PNG>), [Alternatives Available2.PNG](<Screenshots/Alternatives Available2.PNG>): evidence list in the UI; [AT03.txt](DebugAndTests/AT03.txt): notes on cross-document conflict | Implementation gap; Verification needed |
| AI03 | Application tools. Expose callable capabilities for stock lookup, technician availability, task retrieval, schedule validation, cost calculation and draft saving. Validate tool arguments and results. Tool calls must use actual application data, not invented model responses. | Model tool-calling loop behind `OllamaOptions.EnableTools` (off by default): `Infrastructure/Planning/OllamaPlanningRequestInterpreter.cs` › `InterpretWithToolsAsync`; registration `Infrastructure/Planning/PlanningToolServiceCollectionExtensions.cs` › `AddPlanningTools` (called in `Api/Program.cs`). Tool layer: `Application/Planning/Tools/PlanningToolDispatcher.cs`; tools `StockLookupTool.cs` (`lookup_stock`), `TechnicianAvailabilityTool.cs` (`get_technician_availability`), `TaskCatalogueTool.cs` (`get_task_catalogue`), `ScheduleValidationTool.cs` (`validate_schedule`), `CostCalculationTool.cs` (`calculate_cost`). They wrap `IMaintenancePlannerQueries`, `ISparePartReadModelProvider`, `PlanningCandidateValidator.ValidateAsync` and `CandidateCostCalculator.CalculateMetrics`. Draft saving: `SaveCandidateTool.cs` (`save_candidate`) with `ModelCandidateProposals.cs`, merged into the unapproved job result by `Application/Planning/PlanningWorkflow.cs` › `IncludeModelCandidates`. | `AI03ReadToolsTests.cs`, `AI03ScheduleAndCostToolsTests.cs`, `AI03SaveCandidateTests.cs`, `AI03ToolLoopIntegrationTests.cs` (mocked model) | [AI03_Stage3_ToolLoop_UnitTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_UnitTests.txt), [AI03_Stage3_ToolLoop_IntegrationTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_IntegrationTests.txt), [AI03_WindowIdFix_Tests.txt](DebugAndTests/AI03_WindowIdFix_Tests.txt), [AI03_Stage4_SaveCandidate_Tests.txt](DebugAndTests/AI03_Stage4_SaveCandidate_Tests.txt), [AI03_ApprovalPath_Verification.txt](DebugAndTests/AI03_ApprovalPath_Verification.txt): captured mocked-model test runs. [AI03_Stage3_RealModelRun.txt](DebugAndTests/AI03_Stage3_RealModelRun.txt): one real-model run in which tool calls worked but the job failed with `OLLAMA_TIMEOUT`. | **Partially complete.** All six capabilities are implemented, including draft saving per the project's interpretation (E-AI03-10), and the `windowId` gap is fixed (E-AI03-09); both are covered by mocked tests. A saved candidate becomes a plan only through the existing reviewer approval, which still revalidates and reserves (E-AI03-11). **Verification needed:** there is no successful end-to-end real-model run, because CPU-only requests exceeded the 180-second timeout, and the real model hasn't been run since the `windowId` fix or with `save_candidate`. **Implementation gap:** `validate_schedule` and `calculate_cost` lack the replaced-plan context. See the AI03 note. |
| AI04 | Bounded planning loop. Implement interpret, retrieve, propose, validate and revise steps. The validator returns specific violations. Permit a bounded number of repair attempts, for example three, then return a clear failure or request for user input. Persist enough workflow state to recover from interruption. | `PlanningWorkflow.cs` › `ExecuteAsync` (interpret → retrieve → schedule); `Application/Planning/DeterministicPlanningScheduler.cs` › `ScheduleAsync`, `SchedulingPolicies`, `MaximumCandidates`; `PlanningCandidateValidator.ValidateAsync` (`PlanningValidationViolation`); `Domain/Entities/PlanningJob.cs` › `MaxAttempts`; `Infrastructure/PlanningJobs/PlanningJobCheckpointWriter.cs`, `PlanningJobs.cs` › `SaveCheckpointAsync`; `PlanningWorkflow.ResumeFromCheckpointAsync` | `Stage2CPlanningCheckpointResumeTests.cs` › `RestartAfterCheckpoint_ResumesWithoutReinterpreting_AndSucceeds`, `CrashEquivalentAfterCheckpoint_LeaseExpiryReclaimResumesWithoutReinterpreting`; `PlanningJobWorkerTests.cs` › `Worker_BoundsTransientRetriesByMaxAttempts`, `AT09_RecoveryAfterInterruptionPreservesCheckpointWithoutDuplication`; `AT04_InfeasibilityTests.cs` › `AT04_UnavailableBearingAndReducedWindow_PersistedAsInfeasibleWithBothViolations` | [JobCheckPoints.txt](DebugAndTests/JobCheckPoints.txt), [AT09.txt](DebugAndTests/AT09.txt): checkpoint and recovery notes; [Restart_JobRunScenario.webm](Recordings/Restart_JobRunScenario.webm) | Implementation gap; Evidence gap |
| AI05 | Explanations and uncertainty. Explain why a plan was selected, why work was deferred and which facts are uncertain. Separate sourced facts, calculated values and assumptions. A numeric confidence score is optional; unsupported certainty is not acceptable. | `DeterministicPlanningScheduler.cs` › `SchedulingPolicies` (fixed `Rationale` text per policy), `DetermineDeferralReason`; `CandidateCostCalculator.cs` › `CandidateCostMetrics`, `ApplyRankingMetadata` (`CostWarning`); `PlanningContracts.cs` › `PlanningCandidate`, `PlanningInterpretation.Assumptions`, `PlanningEvidenceReference`; `AlternativesScreen.tsx`, `ApprovalScreen.tsx`, `DraftDetailScreen.tsx` (rationale, cost, deferred tasks) | `FR03FiveAssetPlanningFileDatabaseTests.cs` › `FiveAssetRequest_ProducesTwoDistinctValidatedAlternativesWithTotalsAndEvidence`; `AT01_FiveAssetAcceptanceTests.cs` › `AT01_FiveAssets_GeneratesTwoDistinctFeasibleAlternatives` | [Alternatives Available.PNG](<Screenshots/Alternatives Available.PNG>), [Alternatives Available2.PNG](<Screenshots/Alternatives Available2.PNG>): rationale, cost and deferred tasks in the UI | Implementation gap; Test gap; Evidence gap |
| AI06 | Context across changes. Use the saved request, selected revision and locked decisions when processing a follow-up. Show a before-and-after summary instead of rebuilding an unrelated plan. Conflicting new instructions must trigger clarification or explicit revision of earlier decisions. | `PlanningJobEndpoints.cs` › `POST /api/planning-jobs/{originalJobId}/replan`; `PlanningJobs.CreateReplacementAsync`; `Infrastructure/PlanningJobs/PlanningJobProcessor.cs` › `ProcessAsync` (locks, `CompletedTaskIds`, `InProgressPlacements`, `ReleasableReservedQuantities`); `PlanningWorkflow.ExecuteAsync(…, lockedTaskAssignments)`; `frontend/src/screens/DraftDetailScreen.tsx` › `handleReplan` | `FR05EditsAndLocksFileDatabaseTests.cs` › `UserSelectedLocks_AreChangedAndRemoved_ImpossibleLocksAreRejected_AndReplanningKeepsThem`; `F06CompletedWorkReplanningFileDatabaseTests.cs` › `Replanning_DoesNotScheduleTasksCompletedOnV1_AndV2ReservesNoPartForThem`, `InProgressTaskOnV1_KeepsItsApprovedPlacementInV2_IsNotDuplicated_AndStaysInProgress`; `AT16ReplanRevisionHttpWorkflowTests.cs` › `AT16_Test1_ReplanEndpointCreatesJobWithOriginalJobId` | [V1-V2_lifecycle.webm](Recordings/V1-V2_lifecycle.webm), [V1-V2_lifecycle_Part2.webm](Recordings/V1-V2_lifecycle_Part2.webm), [V3_lockChanges.PNG](Screenshots/V3_lockChanges.PNG), [V3_CompletedTaskNotInReplan.PNG](Screenshots/V3_CompletedTaskNotInReplan.PNG): replanning that carries over locks and completed work | Implementation gap; Evidence gap |
| AI07 | Model output handling. Validate output against a schema. Handle malformed fields, unknown asset IDs, nonexistent parts, missing citations, refusal and timeout. Never let invalid output reach the approval path as a valid plan. | `OllamaPlanningRequestInterpreter.cs` › `ParseInterpretation` (`UnmappedMemberHandling.Disallow`, required fields, `ValidateIdentifiers`, `ValidateTextItems`), `InvalidOutput`, `OllamaPlanningInterpreterException` (`OLLAMA_TIMEOUT`, `OLLAMA_UNAVAILABLE`, `OLLAMA_HTTP_ERROR`, `OLLAMA_INVALID_OUTPUT`); `PlanningWorkflow.ExecuteAsync` (`TASK_NOT_FOUND`, asset-scope clarification, `MANUAL_EVIDENCE_UNAVAILABLE`, `SCHEDULE_SCOPE_MISMATCH`); `PlanningJobProcessor.ProcessAsync`; `DraftApprovalService.ApproveDraftAsync` | `AT10ErrorHandlingIntegrationTests.cs` › `AT10_FailedJobResultCannotBeApproved`, `AT10_DataUnavailableResultCannotBeApproved`, `AT10_ClarificationRequiredResultCannotBeApproved`, `AT10_InfeasibleResultCannotBeApproved` | [AT10.txt](DebugAndTests/AT10.txt): notes listing the model timeout and malformed-output tests | Test gap; Implementation gap |

## Notes

**AI01.**
- **Implemented (code read):**
  - The model's JSON is checked against a fixed contract that rejects unknown fields and requires every field (`ParseInterpretation`).
  - The workflow then checks the interpretation against the asset and task records.
  - A missing request text, asset or window, or a non-empty `MissingInformation` list, returns `ClarificationRequired` with a targeted question.
- **Urgency and compatibility:** the contract has no urgency field. The prompt tells the model not to decide inventory, availability or feasibility. Part compatibility comes from the asset–part compatibility data (`IMaintenancePlannerQueries.GetCompatibleSparePartsAsync`), not from the model.
- **Verification needed:**
  - Neither listed clarification test exercises interpretation itself. `EndToEnd_ClarificationResumeWithWorker_GeneratesCandidatesAfterClarification` uses a fixed workflow, and `EndToEnd_ClarificationWorkflow_PreserveRequestAndResetJob` tests the clarify endpoint.
  - The Ollama tests use a fake handler, so real-model behaviour is unverified.
  - Screenshots not inspected.

**AI02.**
- **Implemented (code read):**
  - Manual evidence is retrieved per task from the task's own source document. Evidence from that document must have an excerpt or page, or the result is `MANUAL_EVIDENCE_UNAVAILABLE`.
  - More than one version of a manual returns `MANUAL_EVIDENCE_CONFLICT`. Contradicting statements in other documents return `MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT`.
  - History records are added as evidence.
  - `PlanningEvidenceReference` keeps the document ID, version, page, section and excerpt in the persisted result.
- **Implementation gap:** `GET /api/planning-jobs/{jobId}/candidates` reduces each evidence reference to its `sourceId` string, so `AlternativesScreen.tsx` shows only that ID. The document ID, version, page, section and excerpt are not shown, and there is no way to open the cited manual.
- **Similar-model manuals:** evidence from another document is used only for conflict detection, never as an exact match. However, no mechanism was found for a user to explicitly approve a similar-model manual.
- **Verification needed:** `AT01_EvidenceReferencesContainSourceDocumentDetails` only asserts that at least one reference has a document ID.

**AI03.**

- **Verified baseline (before AI03 work):**
  - No tool or function definitions are sent to the model. `OllamaChatRequest` has no tools field, and nothing in `src/` defines model tools.
  - The interpreter sends one request with `format: "json"` and parses one reply. There is no tool-call loop.
  - The model only interprets the request. Stock, availability, tasks, validation, cost and draft saving are called directly by application code, never by the model.
  - Confirms feedback point 24.
- **Implementation decision: draft saving.** This is the project's interpretation, not wording from the requirement.
  - For AI03, "draft saving" means saving a validated, model-proposed candidate into the planning job's **unapproved** candidate result.
  - It must not create a `Draft` entity, approve a plan, reserve stock, create locks, supersede an approved plan or write business-action records. In this codebase a `Draft` row exists only as the output of approval (`DraftApprovalService`).
  - Approval stays a separate reviewer action through the existing endpoint, which revalidates on the server.
- **Known limitation: technician availability.**
  - The availability tool returns declared availability from `IMaintenancePlannerQueries.GetTechniciansAsync`. It does not subtract time committed to other approved plans. The schedule-validation tool (`PlanningCandidateValidator.ValidateAsync`) does not check that either.
  - Committed time is checked only at approval, by the private `DraftApprovalService.FindTechnicianTimeConflictAsync`. No existing read-only service exposes it.
  - Tool results state this limitation. Approval checks are unchanged and are not duplicated.
- **Staged scope:**
  1. Tool abstraction and dispatcher, with read-only tools for stock, technician availability and task retrieval, all with argument and result validation.
  2. Schedule-validation and cost tools, as thin wrappers over `PlanningCandidateValidator` and `CandidateCostCalculator`.
  3. A bounded Ollama tool-call loop behind a feature flag that is off by default. With the flag off, the interpreter behaves as before.
  4. Save-candidate (draft saving as defined above). Implemented after stages 1–3 were reviewed (see below).
- **Ollama compatibility (documentation checked 2026-10-29; live behaviour not verified):**
  - **Stated in the docs** (docs.ollama.com/api/chat, docs.ollama.com/capabilities/tool-calling):
    - Tools are sent as `{"type":"function","function":{name, description, parameters}}`.
    - The model's calls come back in `message.tool_calls[].function`, with `arguments` as a JSON object. There is no `id` field.
    - Results go back as role `tool` with `tool_name`.
  - **Not stated:**
    - Whether `format` and `tools` can be combined in one request. The implementation therefore never combines them.
    - A minimum Ollama version.
    - Whether `qwen2.5:7b` is tool-capable.
  - **Confirmed locally (preflight 2026-10-29, owner-approved metadata commands only; evidence E-AI03-07):**
    - `ollama --version` reports `ollama version is 0.40.12`.
    - `ollama show qwen2.5:7b` reports architecture `qwen2`, 7.6B parameters, context length 32768, quantization `Q4_K_M`, and capabilities `completion` and `tools`.
  - **Real-model run (2026-10-29, owner-approved, one request against a verified database copy; evidence E-AI03-08):**
    - **Confirmed:** `qwen2.5:7b` on Ollama 0.40.12 returned well-formed `tool_calls`. The application ran six real tool calls, stayed within `MaxToolCalls` (6), returned real data or coded errors (`UNKNOWN_WINDOW` for an invented window ID), and sent the final request in JSON mode without tools.
    - **Job outcome:** Failed with `OLLAMA_TIMEOUT` after 2 attempts. The model ran on CPU, and individual requests took 17.8 s to 261.8 s. Two requests exceeded the 180-second timeout, so no interpretation reached the application and scheduling never ran.
    - **Implementation gap found:** the interpreter's prompt does not include the request's `windowId`. The model invented a window ID and a technician ID.
      - **Fixed afterwards (E-AI03-09):** with tools on, the prompt now carries `selectedWindowId`, set from the request's `MaintenanceWindowId` (application data), and the tool instructions tell the model to use it for every window argument and to use only identifiers from the request or tool results.
      - The flag-off prompt is unchanged.
      - A final answer carrying its own window is rejected (`OLLAMA_INVALID_OUTPUT`). Scheduling always uses the request's window.
      - Covered by mocked tests only; not yet re-run with the real model.
    - **Boundaries held:** the only database change was the planning job itself, with no draft, reservation, lock, edit, progress or business-action records and no stock change.
  - **Still not established:**
    - Whether a tool-enabled run completes, and whether its final interpretation passes validation, on faster hardware or with a longer timeout.
    - Whether `format` can be combined with `tools`. The implementation still never combines them.
- **Implemented so far (stages 1–2 in `src/MaintenancePlanner.Application/Planning/Tools/`; stage 3 in `src/MaintenancePlanner.Infrastructure/Planning/`):**
  - **Dispatcher (`PlanningToolDispatcher`):** turns these into error results for the model: an unknown tool (`UNKNOWN_TOOL`), malformed or non-object JSON (`INVALID_ARGUMENTS`), a tool exception (`TOOL_EXECUTION_FAILED`, without the exception text) and a malformed tool result (`INVALID_TOOL_RESULT`). Cancellation propagates.
  - **Argument checks (`PlanningToolArguments`):** unknown fields, wrong types, blank or padded identifiers, identifiers over 128 characters, lists over 50 items, and timestamps without an explicit offset are all rejected.
  - **Read tools (stage 1):**
    - `lookup_stock` reads the window-aware stock provider and rejects inconsistent stock records.
    - `get_technician_availability` returns declared availability with the committed-time limitation in every result.
    - `get_task_catalogue` rejects inconsistent task records.
    - An unknown window, asset, part or technician is an error, not empty data.
  - **`validate_schedule` (stage 2):**
    - Reads `assetIds`, `windowId` and `placements` into the existing `PlanningCandidateValidationRequest` and returns `PlanningCandidateValidator.ValidateAsync`'s feasibility and violations unchanged. It adds no rules of its own. Duplicate technicians, inverted intervals, unknown tasks and unknown technicians come back as the validator's own violations.
    - An unknown window or asset is an error.
    - Every result lists its limitations: committed technician time is not checked; the completed work and held parts of a replaced plan are not considered; approval revalidates.
  - **`calculate_cost` (stage 2):**
    - Calculates cost only for a candidate that passes the same validator; otherwise it returns `INVALID_CANDIDATE` with the validator's violations. The candidate must therefore also pass the stock check, even though cost doesn't depend on stock.
    - Rates come from `GetTechniciansAsync` and are never accepted from the model.
    - `CandidateCostCalculator.CalculateMetrics` output is returned unchanged, including currency `UNKNOWN` and the warning for a negative stored rate.
  - **Tool-calling loop (stage 3):** `Infrastructure/Planning/OllamaPlanningRequestInterpreter.cs` › `InterpretWithToolsAsync`.
    - **Feature flag:** `OllamaOptions.EnableTools` is off by default. With it off, the interpreter sends its single JSON-mode request exactly as before.
    - **Tool rounds:** sent with the five tool definitions and without `format`. Each requested call runs through `PlanningToolDispatcher`, and its result goes back as a `tool` message.
    - **Final answer:** once the model stops calling tools, one JSON-mode request without tools asks for the interpretation, which is parsed by the unchanged `ParseInterpretation`.
    - **Call limit:** `OllamaOptions.MaxToolCalls` (default 6, validated between 1 and 20) bounds the total number of calls. A round that would exceed it fails the interpretation with `OLLAMA_TOOL_CALL_LIMIT`, which is not retried, before any call in that round runs.
    - **Tool results:** larger than 32,768 characters, or impossible to serialize, are replaced by `INVALID_TOOL_RESULT`. Cancellation propagates.
    - **Missing tools:** if the flag is on but no tools are registered, the interpretation fails with `OLLAMA_TOOLS_UNAVAILABLE`.
  - **Registration:** `Infrastructure/Planning/PlanningToolServiceCollectionExtensions.cs` › `AddPlanningTools`, called from `Api/Program.cs`. The `appsettings` files are unchanged, so the flag stays off.
  - **Draft saving (stage 4):** `Application/Planning/Tools/SaveCandidateTool.cs` (`save_candidate`) and `ModelCandidateProposals.cs`, with the merge in `Application/Planning/PlanningWorkflow.cs` › `IncludeModelCandidates`.
    - **Bound to the job, not the model.** The workflow sets the job context from the request before interpretation: assets, window, completed tasks and releasable parts. The tool accepts only `placements`; any `windowId`, `assetIds`, approval or rate argument is rejected as an unknown argument.
    - **Validated and costed by the application.** The candidate must pass `PlanningCandidateValidator` with that context, so the replanning context applies to saved candidates. Its cost comes from `CandidateCostCalculator` with stored rates.
    - **Limits.** A repeated save returns the earlier proposal number. At most 3 distinct candidates are kept per job.
    - **Merge.** After scheduling, a saved candidate is added to the job's **unapproved** `CandidatesGenerated` candidates only if it covers exactly the required task scope and isn't a copy of a scheduler candidate. Every saved candidate gets an entry in the result's new `modelCandidateNotes` list, which is omitted when nothing was saved, saying whether it was included or why not. For other outcomes, saved candidates are noted as not included.
    - **Persistence.** It goes through the existing lease-guarded job completion. Approval stays the separate reviewer action (`DraftApprovalService`, which reads the candidate by index and revalidates it).
    - **What saving never does:** create a `Draft`, approve, reserve, lock, write business actions, or touch approved plans.
  - **Not done:**
    - Recording tool calls in the job result.
    - Giving `validate_schedule` and `calculate_cost` the replaced-plan context during replanning. Only `save_candidate` has it.
    - A real-model run of `save_candidate`, of the `windowId` fix, or of the approval path of a saved candidate.
    - Approving a saved candidate in a replacement (V1 → V2) job, and concurrent approvals of saved candidates. Both use the same `DraftApprovalService` path that existing tests cover for scheduler candidates, but they weren't exercised with a saved candidate.
- **Status:**
  - **Implementation:** all six capabilities are implemented: stock, technician availability, task retrieval, schedule validation, cost and draft saving (per the project's interpretation), behind `Ollama:EnableTools`, which is off by default. Stages 1–3 have been reviewed; stage 4 and the `windowId` fix are awaiting review.
  - **Approval path of a saved candidate (verified with mocked tests, E-AI03-11):**
    - Saving approves nothing.
    - The existing endpoint `POST /api/drafts/{planningJobId}/approve` requires an explicit `candidateIndex` (otherwise `CANDIDATE_SELECTION_REQUIRED`).
    - Approving the saved candidate creates one Approved draft holding that schedule, one confirmed BR-101 reservation, three locks and one `Approved` business action. A repeat approval writes nothing more.
    - A saved candidate that became stale is rejected at approval (`CANDIDATE_NO_LONGER_VALID` / `TECHNICIAN_UNAVAILABLE`, and `INSUFFICIENT_INVENTORY`), with nothing written.
    - No application code changed for this verification.
  - **Real-model verification:** partial. Tool calling works with the real model (E-AI03-08), but there is no successful end-to-end run yet, because CPU-only inference exceeded the 180-second timeout.
  - **Tests (run 2026-10-29, focused runs only, not the full suite):**
    - Unit tests: `AI03PlanningToolsTests` and `AI03ScheduleAndCostToolsTests` in `tests/MaintenancePlanner.UnitTests/`, run together with the existing `PlanningCandidateValidatorTests`, `CandidateCostCalculatorTests` and `ScenarioAwareSparePartProviderTests`: 130 passed, 0 failed.
    - Integration tests: `AI03ReadToolsTests` and `AI03ScheduleAndCostToolsTests`, run with `MaintenancePlannerQueriesTests`: 18 passed, 0 failed.
    - The integration tests show that tool results match the validator and calculator called directly, and that tool calls, including failing ones, change no jobs, drafts, reservations, locks, edits, progress, business actions or stock.
    - No test involves a model.
    - Stage 3 run (2026-10-29, mocked model): unit tests `AI03OllamaToolLoopTests` together with `OllamaPlanningRequestInterpreterTests`, `PlanningWorkflowTests`, `AT10ErrorHandlingTests` and the Stage 1–2 tool tests: 155 passed, 0 failed. Integration tests `AI03ToolLoopIntegrationTests` together with the Stage 1–2 tool tests and `Stage2CPlanningCheckpointResumeTests`: 27 passed, 0 failed.
    - The existing `OllamaPlanningJobIntegrationTests` (2 tests) fail in their own fixture, independent of AI03: `FindRepositoryRoot` looks for `requirementsforpoc.md` at the repository root, but the file has been under `docs/` since commit `8d0568f`. The flag-off job path is covered instead by `AI03ToolLoopIntegrationTests.PlanningJob_ToolsDisabled_SendsTheSingleUnchangedRequest_EvenWithToolsRegistered`.
  - **Evidence (stage 3):** captured test logs. **Every model reply is mocked**, so none of this shows real Ollama or `qwen2.5:7b` behaviour. Each file's header gives the exact command, the capture time and the HEAD commit (`1376af5`, with uncommitted AI03 changes).

    | Evidence ID | Requirement or feedback | File | Captured by | What it shows | Limitations |
    |---|---|---|---|---|---|
    | E-AI03-01 | AI03; feedback point 24 | [AI03_Stage3_ToolLoop_UnitTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_UnitTests.txt) | The `dotnet test` command in the file header (155 tests) | `FlagOn_MultipleToolTurns_…`: every request and reply of a two-round tool cycle, with tools offered without `format`, results returned with `tool_name`, and a final JSON-mode request without tools | Mocked model; fake tools |
    | E-AI03-02 | AI03 (validate tool arguments and results) | Same file | Same run | `FlagOn_UnknownToolAndMalformedArguments_…`, `FlagOn_MalformedOrOversizedToolResults_…` and `FlagOn_ToolException_…` send the model `UNKNOWN_TOOL`, `INVALID_ARGUMENTS`, `INVALID_TOOL_RESULT` and `TOOL_EXECUTION_FAILED`, without the exception text | Mocked model |
    | E-AI03-03 | AI03 (bounded loop) | Same file | Same run | `RESULT:` lines: `OLLAMA_TOOL_CALL_LIMIT` (not transient) once the limit is reached, and cancellation propagating after one request | Mocked model |
    | E-AI03-04 | AI03 (flag off, no regression) | Same file, and [AI03_Stage3_ToolLoop_IntegrationTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_IntegrationTests.txt) | Both runs | `FlagOff_WithToolsRegistered_…` and `PlanningJob_ToolsDisabled_…`: one request with only `model`, `stream`, `format` and `messages`, as before AI03 | Mocked model |
    | E-AI03-05 | AI03 (actual application data; read-only) | [AI03_Stage3_ToolLoop_IntegrationTests.txt](DebugAndTests/AI03_Stage3_ToolLoop_IntegrationTests.txt) | The `dotnet test` command in the file header (27 tests) | `Interpreter_WithTheFiveRealTools_…`: all five real tools return seeded data (P-101 tasks, BR-101 available 1, feasible candidate, cost 360.50 in currency `UNKNOWN`). The `SNAPSHOT` lines show no change to jobs, drafts, reservations, locks, edits, progress, business actions or stock. `PlanningJob_ToolsEnabledThroughProductionRegistration_…`: a planning job succeeds through `AddOllamaPlanningInterpreter` and `AddPlanningTools`, and only the job row is added. | Mocked model; in-memory SQLite |
    | E-AI03-06 | Test status disclosure | [AI03_Stage3_PreexistingFailure_OllamaPlanningJobIntegrationTests.txt](DebugAndTests/AI03_Stage3_PreexistingFailure_OllamaPlanningJobIntegrationTests.txt) | The `dotnet test` command in the file header | The two existing `OllamaPlanningJobIntegrationTests` fail with `DirectoryNotFoundException` in their fixture, before any interpreter code runs | Failure is unrelated to AI03 and not fixed (outside scope) |
    | E-AI03-07 | AI03 (real-model prerequisite) | [AI03_Stage3_OllamaCompatibilityPreflight.txt](DebugAndTests/AI03_Stage3_OllamaCompatibilityPreflight.txt) | `ollama --version` and `ollama show qwen2.5:7b`, output captured directly (both exit 0) | The installed Ollama version is 0.40.12, and the local `qwen2.5:7b` lists the `tools` capability | Metadata only: no inference was run, so it does not show working tool calls for this application |
    | E-AI03-08 | AI03; feedback point 24 (real model) | [AI03_Stage3_RealModelRun.txt](DebugAndTests/AI03_Stage3_RealModelRun.txt) | One owner-approved planning request ("Replace the P-101 bearing.") through the API, running against a SHA-256-verified database copy, with every Ollama exchange captured by a pass-through logging proxy. Raw files and their hashes are kept outside the repository. | The real model made well-formed tool calls (6 executed, within the limit), and invented identifiers were rejected with `UNKNOWN_WINDOW`. The job failed with `OLLAMA_TIMEOUT` after 2 attempts (CPU-only inference against the 180-second timeout). Only the planning-job row was added to the database. | A single request on CPU-only hardware; no successful end-to-end run. The prompt lacks `windowId`. Reading the original database's journal mode afterwards created `-wal` and `-shm` files beside it (finding F7). |
    | E-AI03-09 | AI03 (actual application data: the selected window reaches the model) | [AI03_WindowIdFix_Tests.txt](DebugAndTests/AI03_WindowIdFix_Tests.txt) | The `dotnet test` commands in the file header. Unit: `AI03OllamaToolLoopTests` and `OllamaPlanningRequestInterpreterTests`, 32/32 passed, exit 0. Integration: `AI03ToolLoopIntegrationTests`, 4/4 passed, exit 0. | `FlagOn_TheModelIsGivenTheApplicationSelectedWindowId_AndToldToUseIt`: every tool-mode request carries `selectedWindowId` equal to the request's window. `FlagOff_…`: the prompt keeps its four pre-AI03 fields. `FlagOn_AFinalAnswerCarryingItsOwnWindow_…`: rejected with `OLLAMA_INVALID_OUTPUT`. `PlanningJob_ModelUsingAnInventedWindow_…`: the invented window from E-AI03-08 is rejected with `UNKNOWN_WINDOW`, and every placement in the resulting plan lies inside `MW-P101-BASELINE-20261024`. | Mocked model; no real inference after the fix |
    | E-AI03-10 | AI03 (draft saving; validate arguments and results; actual application data) | [AI03_Stage4_SaveCandidate_Tests.txt](DebugAndTests/AI03_Stage4_SaveCandidate_Tests.txt) | The `dotnet test` commands in the file header. Unit: all `AI03*` tests plus `PlanningWorkflowTests`, `PlanningJobProcessorTests` and `OllamaPlanningRequestInterpreterTests`, 165/165 passed, exit 0. Integration: all `AI03*` tests plus `Stage2CPlanningCheckpointResumeTests`, `FR03FiveAssetPlanningFileDatabaseTests` and `F06CompletedWorkReplanningFileDatabaseTests`, 60/60 passed, exit 0. | **`AI03SaveCandidateTests`:** refused outside a planning job; a valid candidate is validated, costed (360.50, currency `UNKNOWN`) and kept, and a repeat save returns the same number; `windowId`, `assetIds`, `approved` and `hourlyRate` arguments are rejected; an unknown technician, an unqualified technician and `PART_UNAVAILABLE` are refused; `TASK_ALREADY_COMPLETED` is enforced through the replanning context; at most 3 candidates are kept; snapshot unchanged. **`AI03ModelCandidateProposalsTests`:** 32 concurrent saves keep one. **`PlanningJob_SavedCandidate_…`:** through the production registration, the saved candidate becomes candidate 3 of the unapproved result, with a note; the only write is the job row. **`PlanningJob_SavedCandidateNotCoveringTheFinalTaskScope_…`:** left out, with the reason recorded. | Mocked model. Approving a saved candidate through the existing endpoint was not exercised here. Saved candidates live only for the current attempt, so a retried or checkpoint-resumed job starts without them. |
    | E-AI03-11 | AI03 (draft saving through the existing approval workflow); feedback point 24 | [AI03_ApprovalPath_Verification.txt](DebugAndTests/AI03_ApprovalPath_Verification.txt) | The `dotnet test` commands in the file header. Integration: `AI03ToolLoopIntegrationTests` and `AI03SaveCandidateTests`, 23/23 passed, exit 0. Unit: `AI03ModelCandidateProposalsTests`, 4/4 passed, exit 0. The planning and approval endpoints are hosted in-process over an isolated in-memory database. | **`SavedCandidate_IsApprovedOnlyByTheExplicitApprovalAction_…`:** saving writes only the job row; approving without an index gives 409 `CANDIDATE_SELECTION_REQUIRED`; approving the saved candidate's index gives 202 with one Approved draft holding the saved schedule, one confirmed BR-101 ×1 reservation, three locks and one `Approved` action; a repeat approval writes nothing more. **`SavedCandidate_ThatBecameStaleAfterSaving_…`** (2 cases): rejected with 409 `CANDIDATE_NO_LONGER_VALID` (`TECHNICIAN_UNAVAILABLE`) or `INSUFFICIENT_INVENTORY` (BR-101), with nothing written. | Mocked model. The test host has no business actor, so the action is recorded as "unspecified". Stale data is created by editing the test database directly. Replacement-job and concurrent approvals of saved candidates weren't exercised. |

  - **Real-model validation:** attempted once (E-AI03-08). The tool-calling protocol works with the real model, but the run did not complete: it failed with `OLLAMA_TIMEOUT`. A successful end-to-end real-model run is still outstanding.

**AI04.**
- **Implemented:**
  - Interpret, retrieve, propose (two fixed scheduling policies, up to `MaximumCandidates`) and validate (`PlanningCandidateValidator`, with coded `PlanningValidationViolation`s) are in place.
  - The search is bounded and returns `SearchLimitReached`.
  - Transient model errors are retried up to `MaxAttempts` (default 2).
  - The validated interpretation is checkpointed and resumed after an interruption.
- **Implementation gap:** no "revise" step was found. Validator violations are never sent back to the model for repair. Invalid model output (`OLLAMA_INVALID_OUTPUT`, not transient) fails the job without a retry. Confirms feedback point 25.
- **Evidence gap:** the recording was not watched.

**AI05.**
- **Implemented:**
  - Each candidate has a policy rationale, deferral reasons and calculated cost and completion time, plus a cost-comparability warning where costs can't be compared.
  - Evidence references and model assumptions are kept as separate fields in the result.
  - The "no candidate" message states that it "does not prove that no feasible schedule exists".
- **Implementation gaps:**
  - The rationale is a fixed sentence per policy, not an explanation of why this plan was selected.
  - No uncertainty or confidence field exists.
  - `PlanningInterpretation.Assumptions` is not shown in the alternatives or draft screens. `InterpretationResponse` leaves it out, and `PlanningRequestScreen.tsx` reads it but never renders it.
  - The UI doesn't label which values are sourced facts, calculated values or assumptions.
  - `PlanExportService` contains no evidence, assumption, deferral or rationale content.
- **Test gap:** no integration test asserts explanation, deferral-reason or assumption content.
- **Evidence gap:** screenshots not inspected.

**AI06.**
- **Implemented:**
  - A replan creates a replacement job linked to the approved job (`OriginalJobId`).
  - The processor carries the job's locks, completed tasks, in-progress placements and releasable reservations into the new plan.
- **Implementation gaps:**
  - The replan endpoint takes a new request body (`CreatePlanningJobRequest`). Neither the endpoint nor `handleReplan` loads the saved request.
  - No before-and-after summary exists (see feedback point 19).
  - No check was found that compares new instructions with earlier decisions to trigger clarification. The interpreter receives only the new request text.
  - Confirms feedback points 19 and 26.
- **Evidence gap:** recordings not watched, and screenshots not inspected.

**AI07.**
- **Implemented:**
  - Schema checks (unknown members rejected, required fields, enum value, unique identifiers, task-to-mapping consistency).
  - Timeout and HTTP-error handling.
  - Unknown task IDs (`TASK_NOT_FOUND`) and asset IDs outside the selection (clarification) are handled in the workflow.
  - Missing manual evidence (`MANUAL_EVIDENCE_UNAVAILABLE`) returns `DataUnavailable`.
  - Non-candidate outcomes are blocked from approval.
- **Implementation gap:**
  - No refusal-specific handling. A non-conforming reply is treated as `OLLAMA_INVALID_OUTPUT`.
  - The model does not propose parts, so "nonexistent parts" applies only to application data.
- **Test gap:**
  - Timeout and malformed output are tested only in unit tests (`tests/MaintenancePlanner.UnitTests/AT10ErrorHandlingTests.cs` › `AT10_ModelTimeoutProducesDistinctOllamaTimeoutFailure`, `AT10_MalformedModelOutputProducesDistinctOllamaInvalidOutputFailure`).
  - Unknown task IDs and missing manual evidence are also unit-only (`tests/MaintenancePlanner.UnitTests/PlanningWorkflowTests.cs` › `ExecuteAsync_UnknownTask_ReturnsInvalidInputWithoutScheduling`, `ExecuteAsync_MissingManualEvidenceReturnsDataUnavailableWithoutScheduling`).
  - No integration test was found for refusal, unknown asset IDs in model output or missing citations.
  - Confirms feedback point 27.

## Real-model evaluation evidence

No real-model evaluation evidence was found: no recorded runs against a stated model with expected-versus-actual results.

- [BaselineScenerio_Successful.txt](DebugAndTests/BaselineScenerio_Successful.txt), [ReducerScenerioTest_Successful.txt](DebugAndTests/ReducerScenerioTest_Successful.txt) and [Testing_Positivescenario.txt](DebugAndTests/Testing_Positivescenario.txt) contain manual API calls to a locally running API and their responses, including `interpretation` and `assumptions`. `Api/Program.cs` registers the Ollama interpreter (`AddOllamaPlanningInterpreter`), and `Api/appsettings.json` sets model `qwen2.5:7b`. However, the logs do not record the model, its version or the commit used, so this review cannot confirm they came from a real model.
- The recordings in `docs/Recordings/` were not watched.
- `docs/AI_smoke_test_report.txt` and `docs/OLLAMA_INTEGRATION_AND_TESTING.md` are deleted in the current working tree.

## Stage 7 notes (additive)

Added after the focused test runs R-13 to R-20 ([ACCEPTANCE_TEST_REPORT.md](ACCEPTANCE_TEST_REPORT.md)). The rows and notes above are unchanged. Statuses are kept in [REQUIREMENTS_STATUS.md](REQUIREMENTS_STATUS.md).

- **AI01 and AI07 re-rated to Implemented.** Their earlier Partial reasons were missing verification, not missing behaviour. The unit and integration tests cited in those rows (`OllamaPlanningRequestInterpreterTests`, `PlanningWorkflowTests`, `AT10ErrorHandlingTests`, `AT10ErrorHandlingIntegrationTests`) passed with a mocked model (R-13 to R-15), and the two `OllamaPlanningJobIntegrationTests` cited for AI01 now pass after a test-fixture path fix (see the report, section 10). **Awaiting verification** for both: a real-model run. There is no dedicated refusal test; non-JSON output is covered by the malformed-output tests.
- **AI03 stays Partial.** The AI03 unit and integration classes passed again with a mocked model (R-16: 38 tests, R-17: 116 tests). The real-model position is unchanged: one run (E-AI03-08) timed out, and no successful real-model end-to-end run exists. Known implementation limits remain: `validate_schedule` and `calculate_cost` do not receive the replaced-plan context, tool calls are not recorded in the job result, and saved candidates last one attempt.
- **AI02 stays Partial.** The candidate and draft APIs return evidence as source IDs only (`GET .../candidates` returns each reference's `sourceId`), so the excerpt, page and version retained internally are not available to the user.
- **AI04, AI05 and AI06 stay Partial.** No revise or repair step exists. No search found a test for prompt-injection resistance.