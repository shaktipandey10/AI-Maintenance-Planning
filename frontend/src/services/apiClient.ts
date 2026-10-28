import type {
  CreatePlanningJobRequest,
  PlanningJobAcceptedResponse,
  PlanningJobStatusResponse,
  PlanningJobCandidatesResponse,
  DraftDetailResponse,
  ApprovalErrorResponse,
  ApproveDraftRequest,
  PlanProgressResponse,
  TaskCompletionResponse,
  TaskProgressStatus,
  TaskStatusUpdateResponse,
  EditedTaskPlacementResponse,
  TaskEditRequest,
  TaskEditsResponse,
  EditValidationResult,
  PlanningJobRevisionResponse,
  ReplanAcceptedResponse,
  SavedPlanResponse,
  BusinessActionResponse,
  TaskLockResponse,
  TaskLockChangeResult,
  EditRevisionResult,
  EditValidationViolation
} from '../types/api';

// A revision from task edits was rejected; every violation of the edited plan is kept for display
export class RevisionRejectedError extends Error {
  readonly violations: EditValidationViolation[];

  constructor(message: string, violations: EditValidationViolation[]) {
    super(message);
    this.name = 'RevisionRejectedError';
    this.violations = violations;
  }
}

const API_BASE = import.meta.env.VITE_API_BASE || 'http://localhost:5083';

// Header the API records as the performer of business actions (FR09)
export const ACTOR_ROLE_HEADER = 'X-Actor-Role';

export class ApiClient {
  private actorRole: string | null = null;

  private generateIdempotencyKey(): string {
    return crypto.randomUUID();
  }

  // The role chosen in the role selector; sent with every action the API records in its history
  setActorRole(role: string | null): void {
    this.actorRole = role;
  }

  private withActor(init: RequestInit): RequestInit {
    if (!this.actorRole) {
      return init;
    }

    return { ...init, headers: { ...(init.headers as Record<string, string> | undefined), [ACTOR_ROLE_HEADER]: this.actorRole } };
  }

  async createPlanningJob(
    request: CreatePlanningJobRequest
  ): Promise<PlanningJobAcceptedResponse> {
    const response = await fetch(`${API_BASE}/api/planning-jobs`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': this.generateIdempotencyKey(),
      },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const contentType = response.headers.get('content-type');
      let errorDetail = `Failed to create planning job: ${response.statusText}`;

      if (contentType?.includes('application/json')) {
        try {
          const error = await response.json();
          errorDetail = error.detail || error.message || errorDetail;
        } catch {
          // Response is not valid JSON
        }
      }

      throw new Error(errorDetail);
    }

    return response.json();
  }

  async getPlanningJobStatus(jobId: string): Promise<PlanningJobStatusResponse> {
    const response = await fetch(`${API_BASE}/api/planning-jobs/${jobId}`);

    if (response.status === 404) {
      throw new Error(`Job not found: ${jobId}`);
    }

    if (!response.ok) {
      const contentType = response.headers.get('content-type');
      let errorDetail = `Failed to get planning job status: ${response.statusText}`;

      if (contentType?.includes('application/json')) {
        try {
          const error = await response.json();
          errorDetail = error.detail || error.message || errorDetail;
        } catch {
          // Response is not valid JSON
        }
      }

      throw new Error(errorDetail);
    }

    return response.json();
  }

  async getCandidates(jobId: string): Promise<PlanningJobCandidatesResponse> {
    const response = await fetch(`${API_BASE}/api/planning-jobs/${jobId}/candidates`);

    if (!response.ok) {
      const contentType = response.headers.get('content-type');
      let errorDetail = `Failed to get candidates: ${response.statusText}`;

      if (contentType?.includes('application/json')) {
        try {
          const error = await response.json();
          errorDetail = error.detail || error.message || errorDetail;
        } catch {
          // Response is not valid JSON
        }
      }

      throw new Error(errorDetail);
    }

    return response.json();
  }

  async submitClarification(
    jobId: string,
    windowId: string,
    additionalContext?: string
  ): Promise<PlanningJobStatusResponse> {
    const response = await fetch(`${API_BASE}/api/planning-jobs/${jobId}/clarify`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        windowId,
        additionalContext: additionalContext || undefined,
      }),
    });

    if (response.status === 404) {
      throw new Error(`Job not found: ${jobId}`);
    }

    if (!response.ok) {
      const contentType = response.headers.get('content-type');
      let errorDetail = `Failed to submit clarification: ${response.statusText}`;

      if (contentType?.includes('application/json')) {
        try {
          const error = await response.json() as { detail?: string; message?: string };
          errorDetail = error.detail || error.message || errorDetail;
        } catch {
          // Response is not valid JSON
        }
      }

      throw new Error(errorDetail);
    }

    return response.json();
  }

  async approveDraft(planningJobId: string, candidateIndex: number): Promise<{ draftId: string }> {
    const body: ApproveDraftRequest = { candidateIndex };
    const response = await fetch(`${API_BASE}/api/drafts/${planningJobId}/approve`, this.withActor({
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(body),
    }));

    console.log('[Approval] Response status:', response.status);
    console.log('[Approval] Response statusText:', response.statusText);

    if (response.status === 202) {
      const location = response.headers.get('location');
      console.log('[Approval] Raw location header:', JSON.stringify(location));
      console.log('[Approval] Location header type:', typeof location);
      console.log('[Approval] Location header length:', location?.length);

      if (location) {
        console.log('[Approval] Attempting regex match on location:', location);
        // Try to extract draft ID from location header
        // Matches patterns like: /api/drafts/uuid or /drafts/uuid
        const match = location.match(/\/(?:api\/)?drafts\/([a-fA-F0-9-]+)$/);
        console.log('[Approval] Regex match result:', match);
        if (match) {
          console.log('[Approval] Match groups:', match.length);
          console.log('[Approval] Match[0] (full):', match[0]);
          console.log('[Approval] Match[1] (captured):', match[1]);
        }

        if (match && match[1]) {
          console.log('[Approval] ✓ Successfully extracted draftId from location header:', match[1]);
          return { draftId: match[1] };
        }
        console.log('[Approval] ✗ Location header found but pattern did not match');
        console.log('[Approval] Expected format: /api/drafts/{uuid} or /drafts/{uuid}');
      } else {
        console.log('[Approval] ✗ No location header in 202 response');
      }

      // Fallback: try to parse response body for draftId
      console.log('[Approval] Attempting fallback: checking response body');
      try {
        const contentType = response.headers.get('content-type');
        console.log('[Approval] Content-Type header:', contentType);
        if (contentType?.includes('application/json')) {
          const body = await response.json() as any;
          console.log('[Approval] Response body:', body);
          if (body.draftId) {
            console.log('[Approval] ✓ Found draftId in response body:', body.draftId);
            return { draftId: body.draftId };
          }
        }
      } catch (e) {
        console.log('[Approval] Fallback body parsing failed:', e);
      }

      throw new Error('Approval succeeded but draft ID not found in response');
    }

    if (response.status === 409) {
      const errorData = await response.json() as ApprovalErrorResponse;
      const errorMsg = `Approval failed: ${errorData.reason || 'Unknown error'}\n${errorData.message || ''}`;
      const error = new Error(errorMsg);
      (error as any).conflictData = errorData;
      throw error;
    }

    const contentType = response.headers.get('content-type');
    let errorDetail = `Failed to approve draft: ${response.statusText}`;

    if (contentType?.includes('application/json')) {
      try {
        const error = await response.json();
        errorDetail = error.detail || error.message || errorDetail;
      } catch {
        // Response is not valid JSON
      }
    }

    throw new Error(errorDetail);
  }

  async getDraftDetail(draftId: string): Promise<DraftDetailResponse> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}`);

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (!response.ok) {
      const contentType = response.headers.get('content-type');
      let errorDetail = `Failed to get draft detail: ${response.statusText}`;

      if (contentType?.includes('application/json')) {
        try {
          const error = await response.json();
          errorDetail = error.detail || error.message || errorDetail;
        } catch {
          // Response is not valid JSON
        }
      }

      throw new Error(errorDetail);
    }

    return response.json();
  }

  async getPlanProgress(draftId: string): Promise<PlanProgressResponse> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/progress`);

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (!response.ok) {
      throw new Error(`Failed to get plan progress: ${response.statusText}`);
    }

    return response.json();
  }

  async exportDraftAsJson(draftId: string): Promise<void> {
    const url = `${API_BASE}/api/drafts/${draftId}/export/json`;
    console.log('[API] Fetching JSON export from:', url);
    const response = await fetch(url);
    console.log('[API] JSON export response status:', response.status);

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (response.status === 409) {
      const message = await response.text();
      throw new Error(`Cannot export: ${message}`);
    }

    if (!response.ok) {
      throw new Error(`Failed to export: ${response.statusText}`);
    }

    const blob = await response.blob();
    const url2 = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url2;
    link.download = `plan-${draftId}.json`;
    document.body.appendChild(link);
    link.click();
    window.URL.revokeObjectURL(url2);
    document.body.removeChild(link);
    console.log('[API] JSON export completed');
  }

  async exportDraftAsPdf(draftId: string): Promise<void> {
    const url = `${API_BASE}/api/drafts/${draftId}/export/pdf`;
    console.log('[API] Fetching PDF export from:', url);
    const response = await fetch(url);
    console.log('[API] PDF export response status:', response.status);

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (response.status === 409) {
      const message = await response.text();
      throw new Error(`Cannot export: ${message}`);
    }

    if (!response.ok) {
      throw new Error(`Failed to export: ${response.statusText}`);
    }

    const blob = await response.blob();
    const url2 = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url2;
    link.download = `plan-${draftId}.pdf`;
    document.body.appendChild(link);
    link.click();
    window.URL.revokeObjectURL(url2);
    document.body.removeChild(link);
    console.log('[API] PDF export completed');
  }

  async markTaskComplete(draftId: string, sparePartId: string): Promise<TaskCompletionResponse> {
    const url = `${API_BASE}/api/drafts/${draftId}/mark-task-complete?sparePartId=${encodeURIComponent(sparePartId)}`;
    console.log('[API] Marking task complete:', url);
    const response = await fetch(url, this.withActor({ method: 'POST' }));

    if (response.status === 404) {
      throw new Error('Draft not found');
    }

    if (response.status === 400) {
      const message = await response.text();
      throw new Error(`Invalid request: ${message}`);
    }

    if (response.status === 409) {
      const message = await response.text();
      throw new Error(`Cannot complete task: ${message}`);
    }

    if (!response.ok) {
      throw new Error(`Failed to mark task complete: ${response.statusText}`);
    }

    return response.json();
  }

  // Moves one planned task forward (NotStarted → InProgress → Completed); rejected moves throw with the API's reason.
  async updateTaskStatus(
    draftId: string,
    taskId: string,
    status: TaskProgressStatus
  ): Promise<TaskStatusUpdateResponse> {
    const url = `${API_BASE}/api/drafts/${draftId}/tasks/${encodeURIComponent(taskId)}/progress`;
    const response = await fetch(url, this.withActor({
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ status }),
    }));

    if (response.ok) {
      return response.json();
    }

    if (response.status === 400) {
      throw new Error(`Invalid request: ${await response.text()}`);
    }

    let reason = `Failed to update task ${taskId}: ${response.statusText}`;
    if (response.headers.get('content-type')?.includes('application/json')) {
      try {
        const rejection = await response.json() as TaskStatusUpdateResponse;
        reason = rejection.failureReason || reason;
      } catch {
        // Response is not valid JSON
      }
    }

    throw new Error(reason);
  }

  // Cancels an approved draft (POST /api/drafts/{draftId}/cancel); unused reservations are released.
  async cancelDraft(draftId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/cancel`, this.withActor({ method: 'POST' }));

    if (response.status === 204 || response.ok) {
      return;
    }

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    throw new Error(await readErrorMessage(response, `Failed to cancel draft: ${response.statusText}`));
  }

  async getTaskEdits(draftId: string): Promise<EditedTaskPlacementResponse[]> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/task-edits`);

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to get task edits: ${response.statusText}`));
    }

    return response.json();
  }

  async submitTaskEdits(draftId: string, edits: TaskEditRequest[]): Promise<TaskEditsResponse> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/task-edits`, this.withActor({
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ edits }),
    }));

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (response.status === 409) {
      throw new Error('Task edits can only be submitted for an approved draft');
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to submit task edits: ${response.statusText}`));
    }

    return response.json();
  }

  async validateTaskEdits(draftId: string): Promise<EditValidationResult> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/task-edits/validate`, { method: 'POST' });

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (response.status === 409) {
      throw new Error('Task edits can only be validated for an approved draft');
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to validate task edits: ${response.statusText}`));
    }

    return response.json();
  }

  async deleteTaskEdit(draftId: string, taskId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/api/drafts/${draftId}/task-edits/${encodeURIComponent(taskId)}`,
      this.withActor({ method: 'DELETE' })
    );

    if (response.status === 404) {
      throw new Error(`No edit for task ${taskId} on draft ${draftId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to delete task edit: ${response.statusText}`));
    }
  }

  async clearTaskEdits(draftId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/task-edits`, this.withActor({ method: 'DELETE' }));

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to delete task edits: ${response.statusText}`));
    }
  }

  async getRevisions(jobId: string): Promise<PlanningJobRevisionResponse[]> {
    const response = await fetch(`${API_BASE}/api/planning-jobs/${jobId}/revisions`);

    if (response.status === 404) {
      throw new Error(`Planning job not found: ${jobId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to get revisions: ${response.statusText}`));
    }

    return response.json();
  }

  // Creates a replacement planning job (V2) for an approved job; approve it later through the normal approve call.
  async replanJob(originalJobId: string, request: CreatePlanningJobRequest): Promise<ReplanAcceptedResponse> {
    const response = await fetch(`${API_BASE}/api/planning-jobs/${originalJobId}/replan`, this.withActor({
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': this.generateIdempotencyKey(),
      },
      body: JSON.stringify(request),
    }));

    if (response.status === 404) {
      throw new Error(`Planning job not found: ${originalJobId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to replan: ${response.statusText}`));
    }

    return response.json();
  }

  async getTaskLocks(draftId: string): Promise<TaskLockResponse[]> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/locks`);

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to get task locks: ${response.statusText}`));
    }

    return response.json();
  }

  // Locks a task to a technician (null unlocks it); a lock that cannot be honoured throws with the API's conflict
  async setTaskLock(draftId: string, taskId: string, technicianId: string | null): Promise<void> {
    const url = `${API_BASE}/api/drafts/${draftId}/locks/${encodeURIComponent(taskId)}`;
    const response = await fetch(url, technicianId === null
      ? this.withActor({ method: 'DELETE' })
      : this.withActor({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ technicianId }),
      }));

    if (response.ok) {
      return;
    }

    let reason = `Failed to change the lock of task ${taskId}: ${response.statusText}`;
    try {
      const body = await response.json() as TaskLockChangeResult | string;
      reason = typeof body === 'string'
        ? body
        : [body.failureCode, body.failureReason].filter(Boolean).join(': ') || reason;
    } catch {
      // Response is not valid JSON
    }

    throw new Error(reason);
  }

  // Turns the draft's task edits into a revision to review and approve; a rejection lists every violation
  async createRevisionFromEdits(draftId: string): Promise<ReplanAcceptedResponse> {
    const response = await fetch(`${API_BASE}/api/drafts/${draftId}/task-edits/revision`, this.withActor({
      method: 'POST',
      headers: { 'Idempotency-Key': this.generateIdempotencyKey() },
    }));

    if (response.status === 202 || response.ok) {
      return response.json();
    }

    if (response.status === 404) {
      throw new Error(`Draft not found: ${draftId}`);
    }

    if (response.status === 409) {
      const result = await response.json() as EditRevisionResult;
      throw new RevisionRejectedError(
        [result.failureCode, result.failureReason].filter(Boolean).join(': ') || 'The revision was rejected',
        result.violations ?? []
      );
    }

    throw new Error(await readErrorMessage(response, `Failed to create the revision: ${response.statusText}`));
  }

  // Saved plans, newest first, so a plan can be opened without knowing its draft ID
  async getSavedPlans(): Promise<SavedPlanResponse[]> {
    const response = await fetch(`${API_BASE}/api/drafts`);

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to get saved plans: ${response.statusText}`));
    }

    return response.json();
  }

  // Business-action history of a plan and all its revisions, oldest first
  async getActionHistory(jobId: string): Promise<BusinessActionResponse[]> {
    const response = await fetch(`${API_BASE}/api/planning-jobs/${jobId}/history`);

    if (response.status === 404) {
      throw new Error(`Planning job not found: ${jobId}`);
    }

    if (!response.ok) {
      throw new Error(await readErrorMessage(response, `Failed to get action history: ${response.statusText}`));
    }

    return response.json();
  }
}

// Reads an error body: a JSON string, an ApprovalErrorResponse or problem details, or plain text.
async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  try {
    const text = await response.text();
    if (!text) {
      return fallback;
    }

    try {
      const body = JSON.parse(text);
      if (typeof body === 'string') {
        return body;
      }
      if (body?.reason || body?.message) {
        return [body.reason, body.message].filter(Boolean).join(': ');
      }
      return body?.detail || body?.title || fallback;
    } catch {
      return text;
    }
  } catch {
    return fallback;
  }
}

export const apiClient = new ApiClient();
