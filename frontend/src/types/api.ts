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
