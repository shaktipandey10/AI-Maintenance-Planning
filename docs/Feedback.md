Thank you for the submission. Please review the implementation against the original assignment document and correct the gaps below. The original scope remains unchanged.


Please investigate the underlying causes, implement the necessary corrections and verify these mandatory areas:

Data and imports: Deliver the complete required dataset and working PDF/CSV/JSON input handling, including validation and clear reporting of invalid records.
Selection and approval: Ensure the alternative selected by the user is the one saved and approved. Revalidate current constraints and prevent conflicting reservations of parts and technician time.
Revisions and retries: Preserve approved history and feasible locks. Demonstrate correct reservation handling during replacement, failed replacement, repeated requests and cancellation.
Task progress and exports: Track individual task states, preserve completed work during replanning, distinguish consumed from unused parts, and provide PDF/JSON exports matching the selected approved revision.
Application workflow: Complete the required editing, replanning, change comparison, saved-plan retrieval and business-action history through the application.
AI requirements: Verify AI01–AI07 against the original scope, including inspectable evidence, callable application capabilities, bounded validation/repair, context across changes and invalid-output handling. Clearly distinguish real-model evidence from mocked tests.
Recovery and failures: Demonstrate interruption/restart recovery, consistent persisted state, duplicate prevention and actionable failure outcomes.
For resubmission, please provide:

A corrected requirement matrix covering every FR, AI and APP requirement, marked Implemented, Partial or Not implemented, with code, test and evidence references.
A concise correction report stating each gap, its cause, the correction made and the verification result.
An updated repository at a fixed commit, reproducible setup instructions, AT01–AT12 results, the required demonstration and approved V1/revised V2 PDF and JSON exports.