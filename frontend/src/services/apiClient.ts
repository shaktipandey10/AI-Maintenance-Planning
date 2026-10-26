import type {
  CreatePlanningJobRequest,
  PlanningJobAcceptedResponse,
  PlanningJobStatusResponse,
  PlanningJobCandidatesResponse,
  DraftDetailResponse,
  ApprovalErrorResponse,
  PlanProgressResponse,
  TaskCompletionResponse
} from '../types/api';

const API_BASE = import.meta.env.VITE_API_BASE || 'http://localhost:5083';

export class ApiClient {
  private generateIdempotencyKey(): string {
    return crypto.randomUUID();
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

  async approveDraft(planningJobId: string): Promise<{ draftId: string }> {
    const response = await fetch(`${API_BASE}/api/drafts/${planningJobId}/approve`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
    });

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
    const response = await fetch(url, { method: 'POST' });

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
}

export const apiClient = new ApiClient();
