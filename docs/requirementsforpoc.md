
    • 
GENIX CANDIDATE PROJECT
AI Maintenance Planning and Coordination
Implementation requirements and submission guide

Build a working AI application that helps a factory team plan maintenance for several assets. It must use equipment documents and resource data, propose feasible alternatives, adapt to changes, and preserve the approved plan and its history.
Your assignment
Deliver an end-to-end prototype, its source code, reproducible sample data and review evidence. A user must be able to request, review, revise, approve and export a maintenance plan. The application must detect when the requested work cannot fit the available resources or time.
The main expectation
AI interprets requests, retrieves evidence and proposes plans. Application code calculates totals, validates constraints and controls reservations and approvals. A fluent answer is not sufficient if the plan is impossible or the saved data is incorrect.
1 Purpose and scope
A maintenance planner needs to coordinate tasks, equipment, parts and technicians within a permitted maintenance window. The information is spread across documents and structured records. Your application should bring these inputs together and help the planner make a justified, reviewable decision.
Mandatory scope
Support five assets at one fictional plant, three technicians, two maintenance windows and a shared spare-parts inventory. Create a browser-based application, backend APIs, persistent storage, a real model integration and background planning. Demonstrate the full workflow on local infrastructure.
Use a simple, explainable scheduling heuristic. A mathematically optimal schedule is not required. Every recommended plan must pass the same deterministic validator. If no feasible plan is found, explain the violated constraints and distinguish an infeasible request from a limitation of your search algorithm.
User roles
Planner: creates requests, reviews alternatives, edits and submits plans. Reviewer: approves, requests changes or cancels plans. Technician: records simulated task progress. A clearly labelled role selector is sufficient; enterprise authentication is not required.
Technology choices
Use a language and framework you can defend. C#/.NET or Python are acceptable for the backend; any suitable web UI is acceptable. Use a persistent relational database or an equivalent store with demonstrated atomic concurrency controls. An agent framework and multiple agents are optional, not scoring requirements.
The main demonstration must use a real language model for request interpretation and evidence-backed planning. Use deterministic mocks only for automated tests or clearly labelled failure simulations. Disclose the provider, model, framework versions and approximate API usage cost.
Boundaries and stretch goals
Excluded: observability dashboards, security products, DevOps pipelines, live equipment control, predictive failure models, production-grade identity and integrations with ERP/CMMS. Do not invent equipment repair instructions; use the fictional supplied-by-you task catalogue and manuals.
Stretch only after mandatory work passes: cloud deployment, a dedicated optimization solver, additional plants, scanned-document OCR or notifications. Document how the application could map to Azure, but deploying it is not mandatory.
Effort and assumptions
Agree the deadline and API access before work begins. Create the synthetic data specified in Section 5 unless the interviewer supplies replacements. Record assumptions and actual effort. If the time budget is reached, submit the working scope and clearly identify gaps; do not present unimplemented features as complete.

2 Functional requirements
FR01  Request and clarification. Accept a natural-language request, selected assets, maintenance window and planning objective. Ask for missing mandatory information such as scope or window. Store the clarified request and explicit assumptions.
FR02  Evidence and resource inputs. Import text-based PDF manuals plus CSV or JSON files for assets, tasks, history, parts and technician availability. Validate required fields, identifiers, units and references. Report invalid rows without silently discarding them.
FR03  Multiple asset planning. Build a schedule across five assets, with predecessor tasks, task durations, required skills, technician capacity and part quantities. Use a documented time granularity such as 30 minutes. Do not overlap assignments for the same technician.
FR04  Alternatives. For a fixture designed to allow alternatives, present at least two distinct feasible plans, such as earliest completion versus lowest labour cost. Show completion time, labour cost, deferred work and trade-offs. If only one or no alternative is available, say so; do not fabricate a second option.
FR05  Review and editing. Let the user edit a task assignment or timing, select an alternative and lock selected tasks or decisions. Revalidate every edit. A lock does not override feasibility: an impossible locked decision must be identified and require user resolution.
FR06  Approval and reservations. Submit a specific revision for review. Approve only a currently valid revision. Atomically reserve its parts and technician slots. If data changed or another plan reserved them, reject the stale approval and show the conflict.
FR07  Replanning and revisions. When stock, technician availability or a window changes, identify affected work and generate a new draft. Preserve feasible locked decisions and explain every material change. Never silently overwrite an approved revision.
FR08  Progress and cancellation. Allow simulated Not started, In progress and Completed task updates. Replanning must preserve completed work. Cancelling a plan releases only unused reservations. Define and demonstrate when reserved parts become consumed.
FR09  Persistence and history. Persist requests, plan revisions, source references, validation results, approvals, reservations and progress. Reload a saved plan after browser refresh and application restart. Record who performed each business action and when.
FR10  Export. Export the selected approved revision to PDF and structured JSON. Both must agree with the saved revision, including tasks, timings, resources, assumptions, evidence and deferred work. Clearly label unapproved exports if you support them.

3 AI requirements
AI01  Structured interpretation. Convert the request into validated structured fields. Ask a focused follow-up question when a mandatory value is missing. Do not infer urgency or equipment compatibility without a stated rule or supporting data.
AI02  Grounded retrieval. Retrieve asset-specific task instructions from the uploaded manuals and relevant history. Retain document ID, version, page or section and the supporting excerpt. Users must be able to open or inspect the evidence. Do not use a similar asset manual as an exact match without explicit approval.
AI03  Application tools. Expose callable capabilities for stock lookup, technician availability, task retrieval, schedule validation, cost calculation and draft saving. Validate tool arguments and results. Tool calls must use actual application data, not invented model responses.
AI04  Bounded planning loop. Implement interpret, retrieve, propose, validate and revise steps. The validator returns specific violations. Permit a bounded number of repair attempts, for example three, then return a clear failure or request for user input. Persist enough workflow state to recover from interruption.
AI05  Explanations and uncertainty. Explain why a plan was selected, why work was deferred and which facts are uncertain. Separate sourced facts, calculated values and assumptions. A numeric confidence score is optional; unsupported certainty is not acceptable.
AI06  Context across changes. Use the saved request, selected revision and locked decisions when processing a follow-up. Show a before-and-after summary instead of rebuilding an unrelated plan. Conflicting new instructions must trigger clarification or explicit revision of earlier decisions.
AI07  Model output handling. Validate output against a schema. Handle malformed fields, unknown asset IDs, nonexistent parts, missing citations, refusal and timeout. Never let invalid output reach the approval path as a valid plan.
AI and code responsibilities
AI may propose	Application code must decide
Intent, questions and task suggestions	Required fields, allowed identifiers and state transitions
Alternative schedules and explanations	Durations, dependencies, capacity, stock and costs
Changes needed after a constraint update	Approval validity, atomic reservations and version conflicts
Evidence-backed recommendations	Whether cited source records exist and whether constraints pass
Document your orchestration design. Multiple agents should only be used if their responsibilities and added value are clear. Do not expose hidden model reasoning; a concise decision summary, source references and tool outcomes are sufficient.

4 Application correctness and workflow
Minimum business states
Request: Needs clarification, Ready for planning. Planning job: Queued, Running, Succeeded, Failed or Cancelled. Plan revision: Draft, Awaiting review, Changes requested, Approved, Superseded or Cancelled. Task progress is tracked separately. Document allowed transitions and reject invalid transitions.
APP01  Deterministic validation. All tasks must fit their permitted window, follow dependencies and use available qualified technicians. Part reservations must not exceed stock. Use a single declared timezone and units; store unambiguous timestamps. Calculate duration and labour costs in code, using decimal values for money.
APP02  Resource consistency. Approval must be atomic: either all required reservations succeed or none do. Two concurrent approvals for the last part cannot both succeed. Define what happens if a resource becomes unavailable after approval; mark affected plans as requiring review rather than silently retaining a feasible status.
APP03  Idempotent actions. Repeated approval, cancellation, save and retried background steps must not duplicate plans, approvals or reservations. Use stable operation IDs and storage constraints or an equivalent mechanism. Demonstrate the behaviour, not only the design.
APP04  Approved revision replacement. Keep the previous approved revision immutable. A proposed replacement remains draft until reviewed. Replace old reservations with new ones atomically on approval, or explain a demonstrably equivalent consistent approach. A failed replacement must leave the prior approved plan and reservations intact.
APP05  Background recovery. Return a job ID when planning starts. Show status and let the user leave and return. On process restart, recover or safely restart unfinished steps using saved state without duplicating business effects. A browser refresh must not launch a second planning job.
APP06  Visible failure states. Handle unavailable model or resource services, invalid input and interrupted jobs. Show what failed and what can be retried. Missing stock data means availability is unknown, not zero stock or confirmed availability.
Minimum screens
1. Request and input data. 2. Alternatives and schedule with evidence. 3. Edit and replan with differences and locks. 4. Review, approval and reservations. 5. Saved plans, revision history and simulated progress. Screens may be combined if the complete journey remains clear.
Suggested implementation sequence
Start with data and deterministic validation; add saving, approval and reservation rules; connect AI retrieval and planning; add revisions and recovery; then complete tests and the demo. The illustrated UI discussed with the interviewer is conceptual guidance, not a pixel-perfect requirement.

5 Sample data and reproducibility
Create and include a self-contained fictional dataset. No private factory data or real equipment access is needed. All task durations, compatibility rules, procedures and costs are exercise assumptions, clearly labelled as such.
Dataset	Minimum content
Assets	5 assets with stable IDs, plant, model, priority and permitted maintenance windows.
Task catalogue and manuals	10 to 15 tasks with asset/model, duration, predecessors, skill, crew size, part quantity and source. At least 2 text-based PDF manuals with page/section references.
Technicians	3 technicians with IDs, skills, availability intervals and hourly rates. Include at least one skill constraint.
Parts	At least 6 part types with unit, on-hand quantity and applicable assets or approved compatibility rules.
History	At least 10 prior maintenance records linked to assets. Include one record that differs from a manual so a conflict can be demonstrated.
Planning windows	2 dated windows in a declared timezone. Ensure the baseline has at least 2 feasible alternatives.
Change fixtures	Reduced window, missing part, absent technician, conflicting manual, invalid row, concurrent last-part claim and an interrupted job.
Required anchor scenario
Pump P-101 requires preparation for 1 hour, bearing replacement for 3 hours and verification for 2 hours, in that sequence. It requires one BR-101 bearing and two qualified technicians for the replacement phase; specify crew requirements for the other phases in your catalogue. The initial window is Saturday 08:00 to 16:00.
In the changed scenario, the bearing is unavailable and the window is 08:00 to 12:00. The full replacement must be rejected as infeasible. An inspection-only plan is a different scope and cannot be substituted without explicit reviewer agreement. Use fixed dates in the fixtures so the demo remains reproducible.
Data setup
Include schema definitions, a data dictionary and a seed/reset command. The reset must recreate the same baseline locally. Include the bad-data fixtures separately and document expected errors. Do not hard-code outcomes for named demonstration cases; the same logic must work with modified inputs.
Dependencies
Provide an environment-variable example without credentials, pinned or locked dependencies and documented model access requirements. If the interviewer does not provide a model account, agree an alternative before starting. Do not assume access to internal ABB APIs or paid cloud resources.

6 Acceptance scenarios
Run and record the following tests. Use their IDs in your test report and requirements traceability. Automated tests must cover the deterministic rules; include a small real-model evaluation for AI behaviour. Mock responses are appropriate for repeatable failure tests, but label them.
ID	Test and required outcome
AT01	Baseline: plan five assets using three technicians. On the prepared fixture, show two distinct feasible alternatives with calculated totals and source references.
AT02	Clarification: omit the window or scope. Ask for the missing information before treating a plan as ready for approval.
AT03	Evidence conflict: introduce a similar-model manual or conflicting instruction. Identify the conflict or exclude the irrelevant source; do not silently select it.
AT04	Infeasibility: use the four-hour P-101 window and unavailable bearing. Reject full replacement and explain both blockers.
AT05	Replanning: remove a technician while a task is locked. Preserve the lock if feasible; otherwise expose the conflict and request a decision.
AT06	Concurrency: approve two plans requiring the final unit of the same part in parallel. Exactly one succeeds; the other receives a conflict; stock is not over-reserved.
AT07	Retry: repeat approval and cancellation requests. No duplicate reservations or double release occurs.
AT08	Replacement: approve V1, draft V2 and force a reservation conflict on V2 approval. V1 remains unchanged and its reservations remain consistent.
AT09	Recovery: interrupt planning, restart the application and reopen the job. It resumes or safely restarts with one coherent result and no duplicated effects.
AT10	Failure: simulate model timeout, malformed output and inventory lookup failure. Show distinct, actionable states; no invalid plan becomes approved.
AT11	Progress and export: complete one task, revise remaining work, reload and export. Completed work is preserved; PDF and JSON match the chosen approved revision.
AT12	Unseen change: modify a duration, quantity or availability interval during the live demo. The general validation and planning logic still works.
For each test record the input fixture, expected result, actual result, pass/fail, evidence file and any limitation. For concurrency tests show the final database state, not just screenshots of success messages.

7 Required submission deliverables
Submit one ZIP archive or an accessible repository at a fixed commit, together with the demo recording and review documents. A reviewer must be able to run and assess the project without requesting missing setup files.
ID	Deliverable and required contents
DEL01	Source code: frontend, backend, orchestration, validation, persistence and background-worker code. Include dependency locks, migrations/schema and the final commit identifier.
DEL02	README: prerequisites, model configuration, exact install/start/seed/reset/test commands, local URLs, demo roles and troubleshooting. Verify these from a clean local setup.
DEL03	Sample data: manuals, structured fixtures, data dictionary, seed/reset tooling and all changed or invalid datasets used for acceptance tests.
DEL04	Architecture note: component diagram, request flow, entity relationships, state transitions, transaction boundaries, reservation design and restart strategy. Explain key trade-offs.
DEL05	AI design note: prompts, output schemas, retrieval method, source-reference format, tool contracts, retry limits, model/version, assumptions and known failure modes.
DEL06	API specification: endpoints or OpenAPI document, request/response examples, validation errors, job status, version conflict and idempotency behaviour.
DEL07	Tests and results: runnable automated tests; AT01-AT12 report; small AI evaluation set; fixture and evidence references. Report failures honestly.
DEL08	Review evidence: 8-12 minute screen recording of the main flow and key failure cases; screenshots; approved V1 and revised V2 exports in PDF and JSON.
DEL09	Traceability and status: map every FR, AI and APP requirement to code, relevant tests and evidence. Mark Implemented, Partial or Not implemented with explanations.
DEL10	Handover note: known limitations, actual effort, approximate model cost, reused libraries/templates, AI coding assistance, and priorities for further work. Include an Azure architecture mapping without requiring deployment.
Recommended package layout
src/  frontend, backend and worker
data/  manuals, fixtures and seed scripts
docs/  architecture, AI design, API, traceability and limitations
tests/  automated tests and AI evaluation cases
evidence/  test results, screenshots, demo recording and exports
README.md and an environment example without credentials

8 Demonstration and review criteria
Live demonstration
Allow approximately 45 minutes: 5 minutes for setup and architecture; 15 minutes for the end-to-end workflow; 15 minutes for changed constraints, concurrency and recovery; 10 minutes for code explanation and a small live change. The reviewer may use modified fixtures rather than only your prepared examples.
Demonstrate: import and clarify; produce alternatives; inspect evidence; approve and reserve; change resources and replan; compare revisions; preserve completed work; export the approved result; then show a failure and its recovery. Be ready to explain the critical code path and the tests that support it.
Evaluation matrix
Area	Evidence reviewed	Weight
Core development	Working APIs, data model, readable code and a live modification.	15%
AI implementation	Grounding, tools, structured output, bounded planning and uncertainty handling.	20%
Planning correctness	Validated schedules, resource constraints, calculations and infeasibility handling.	20%
State and consistency	Revisions, atomic reservations, retries and recovery.	15%
Architecture	Clear responsibilities and justified trade-offs, including Azure mapping.	10%
Tests and evidence	Repeatable acceptance tests, AI evaluation and transparent results.	10%
User experience	Usable review, editing, replanning and export.	5%
Ownership and handover	Understanding of reused/generated code, setup quality and documented limitations.	5%
Completion standard
A complete submission demonstrates every mandatory requirement and provides the required deliverables. Approval of infeasible plans, over-reservation of stock, silent loss of approved history or invented supporting evidence are correctness failures that must be explicitly reported and resolved. Visual polish and extra agents do not compensate for these failures.
Use of AI coding tools
You may use coding assistants and open-source libraries. Disclose significant reuse and assistance, comply with applicable licences, and explain the resulting code. You remain responsible for correctness and must be able to modify the implementation during review.
Before submission
Run the documented clean setup and reset process. Verify the submitted commit matches the recording and results. Remove credentials and unrelated files. Include failed tests and unfinished requirements in the status report. Confirm the reviewer can access the repository or archive and open the evidence files.