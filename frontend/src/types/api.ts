export interface CreatePlanningJobRequest {
  requestText: string;
  assetIds: string[];
  windowId?: string;
}

export interface PlanningJobAcceptedResponse {
  jobId: string;
  statusUrl: string;
}

export interface PlanningJobStatusResponse {
  jobId: string;
  status: string;
  attemptCount: number;
  maxAttempts: number;
  currentStep: string;
  resultPayload: string | null;
  clarificationPayload: string | null;
  failureCode: string | null;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  errorMessage: string | null;
}

export interface TaskPlacement {
  taskId: string;
  start: string; // ISO 8601 DateTimeOffset
  end: string;   // ISO 8601 DateTimeOffset
  technicianIds: string[];
}

export interface CandidateResponse {
  rationale: string;
  requestedTaskIds: string[];
  dependencyTaskIds: string[];
  taskPlacements: TaskPlacement[];
  evidenceReferences: string[];
  totalLabourCostAmount: number;
  totalLabourCostCurrency: string;
  completionTimeMinutes: number;
  deferralReasons: DeferralReasonResponse[];
  isLowestCostCandidate: boolean;
  costComparabilityWarning?: string;
}

export interface DeferralReasonResponse {
  taskId: string;
  reason: string;
}

export interface InterpretationResponse {
  maintenanceIntent: string;
  scope: string;
  proposedAssetIds: string[];
  proposedTaskIds: string[];
}

export interface EvidenceReference {
  sourceKind: string;
  sourceId: string;
  documentId?: string;
  version?: string;
  page?: number;
  section?: string;
  excerpt?: string;
}

export interface PlanningJobCandidatesResponse {
  jobId: string;
  status: string;
  outcome: string;
  candidates: CandidateResponse[];
  interpretation?: InterpretationResponse;
}

export interface DraftDetailResponse {
  draftId: string;
  planningJobId: string;
  status: string;
  createdAtUtc: string;
  approvedAtUtc?: string;
  selectedCandidate?: CandidateResponse;
  reservations: ReservationResponse[];
  interpretation?: InterpretationResponse;
}

export interface ReservationResponse {
  reservationId: string;
  sparePartId: string;
  quantityReserved: number;
  status: string;
  createdAtUtc: string;
  confirmedAtUtc?: string;
}

// Body of POST /api/drafts/{planningJobId}/approve: the index of the selected
// candidate in the job's candidates list (GET /api/planning-jobs/{jobId}/candidates).
export interface ApproveDraftRequest {
  candidateIndex: number;
}

export interface ApprovalErrorResponse {
  resourceType: string;
  reason: string;
  resourceId?: string;
  message: string;
}

export interface ApprovalSuccessResponse {
  draftId: string;
}

export interface PlanProgressResponse {
  draftId: string;
  totalRequired: number;
  completedQuantity: number;
  remainingQuantity: number;
  tasks: TaskProgressResponse[];
  // Status of every task placed in the approved plan, in plan order.
  taskStatuses?: TaskStatusResponse[];
}

export type TaskProgressStatus = 'NotStarted' | 'InProgress' | 'Completed';

export interface TaskStatusResponse {
  taskId: string;
  status: TaskProgressStatus;
  updatedAtUtc?: string | null;
}

// Result of POST /api/drafts/{draftId}/tasks/{taskId}/progress.
export interface TaskStatusUpdateResponse {
  succeeded: boolean;
  taskId: string;
  status?: TaskProgressStatus | null;
  failureCode?: string | null;
  failureReason?: string | null;
}

export interface TaskProgressResponse {
  sparePartId: string;
  requiredQuantity: number;
  completedQuantity: number;
  remainingQuantity: number;
}

export interface TaskCompletionResponse {
  succeeded: boolean;
  failureReason?: string;
}

// One task edit as submitted to POST /api/drafts/{draftId}/task-edits.
export interface TaskEditRequest {
  taskId: string;
  newStart: string; // ISO 8601 DateTimeOffset
  newEnd: string;   // ISO 8601 DateTimeOffset
  newTechnicianIds: string[];
}

// A persisted task edit (GET /api/drafts/{draftId}/task-edits).
export interface EditedTaskPlacementResponse {
  taskId: string;
  originalStart: string;
  originalEnd: string;
  originalTechnicianIds: string[];
  newStart: string;
  newEnd: string;
  newTechnicianIds: string[];
  validationStatus: string;
}

// Result of POST /api/drafts/{draftId}/task-edits.
export interface TaskEditsResponse {
  draftId: string;
  editedTasks: EditedTaskPlacementResponse[];
}

export interface EditValidationViolation {
  taskId: string;
  code: string;
  message: string;
}

// Result of POST /api/drafts/{draftId}/task-edits/validate.
export interface EditValidationResult {
  draftId: string;
  isValid: boolean;
  violations: EditValidationViolation[];
  validEditsCount: number;
  invalidEditsCount: number;
}

// One planning job in a revision chain (GET /api/planning-jobs/{jobId}/revisions).
export interface PlanningJobRevisionResponse {
  id: string;
  status: string;
  createdAtUtc: string;
  originalJobId?: string | null;
}

// A task's technician lock on a draft (GET /api/drafts/{draftId}/locks).
export interface TaskLockResponse {
  taskId: string;
  technicianId: string;
}

// Rejection of a lock change (PUT/DELETE /api/drafts/{draftId}/locks/{taskId}).
export interface TaskLockChangeResult {
  succeeded: boolean;
  failureCode?: string | null;
  failureReason?: string | null;
}

// Rejection of POST /api/drafts/{draftId}/task-edits/revision, with every violation of the edited plan.
export interface EditRevisionResult {
  succeeded: boolean;
  planningJobId?: string | null;
  failureCode?: string | null;
  failureReason?: string | null;
  violations?: EditValidationViolation[] | null;
}

// One saved plan (GET /api/drafts), newest first.
export interface SavedPlanResponse {
  draftId: string;
  planningJobId: string;
  originalJobId?: string | null;
  status: string;
  createdAtUtc: string;
  approvedAtUtc?: string | null;
  assetIds: string[];
  windowId?: string | null;
  requestText?: string | null;
}

// One recorded business action (GET /api/planning-jobs/{jobId}/history), oldest first.
export interface BusinessActionResponse {
  id: string;
  action: string;
  performedBy: string;
  performedAtUtc: string;
  planningJobId?: string | null;
  draftId?: string | null;
  taskId?: string | null;
  details?: string | null;
}

// Result of POST /api/planning-jobs/{originalJobId}/replan.
export interface ReplanAcceptedResponse {
  newJobId: string;
  statusUrl: string;
}
