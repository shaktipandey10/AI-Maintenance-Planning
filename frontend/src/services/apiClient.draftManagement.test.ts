import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiClient, RevisionRejectedError } from './apiClient';

// A minimal fetch Response stand-in: status, ok, text() and json() over the given body
const response = (status: number, body?: unknown) => ({
  status,
  ok: status >= 200 && status < 300,
  statusText: `HTTP ${status}`,
  headers: { get: () => (body === undefined ? null : 'application/json') },
  text: async () => (body === undefined ? '' : JSON.stringify(body)),
  json: async () => body,
});

const stubFetch = (...responses: ReturnType<typeof response>[]) => {
  const fetchMock = vi.fn();
  responses.forEach((r) => fetchMock.mockResolvedValueOnce(r));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
};

describe('apiClient draft management', () => {
  const draftId = 'draft-1';

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    apiClient.setActorRole(null);
  });

  it('locks a task with PUT { technicianId } and unlocks it with DELETE', async () => {
    const fetchMock = stubFetch(response(204), response(204), response(200, [{ taskId: 'P101-PREP', technicianId: 'TECH-002' }]));

    await apiClient.setTaskLock(draftId, 'P101-PREP', 'TECH-002');
    await apiClient.setTaskLock(draftId, 'P101-PREP', null);
    const locks = await apiClient.getTaskLocks(draftId);

    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/drafts\/draft-1\/locks\/P101-PREP$/);
    expect(fetchMock.mock.calls[0][1].method).toBe('PUT');
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ technicianId: 'TECH-002' });
    expect(fetchMock.mock.calls[1][1].method).toBe('DELETE');
    expect(locks).toEqual([{ taskId: 'P101-PREP', technicianId: 'TECH-002' }]);
  });

  it('rejects an impossible lock with the API conflict code and reason', async () => {
    stubFetch(response(409, {
      succeeded: false,
      failureCode: 'LOCKED_TECHNICIAN_UNQUALIFIED',
      failureReason: 'Task P101-VERIFICATION cannot be locked to technician TECH-001, who lacks required skills: INSTRUMENTATION.',
    }));

    await expect(apiClient.setTaskLock(draftId, 'P101-VERIFICATION', 'TECH-001'))
      .rejects.toThrow('LOCKED_TECHNICIAN_UNQUALIFIED: Task P101-VERIFICATION cannot be locked');
  });

  it('creates a revision from edits with an Idempotency-Key, and keeps every violation of a rejected one', async () => {
    const violations = [{ taskId: 'P101-VERIFICATION', code: 'PREDECESSOR_ORDER_VIOLATION', message: 'Verification starts before the bearing replacement ends' }];
    const fetchMock = stubFetch(
      response(202, { newJobId: 'job-2', statusUrl: '/api/planning-jobs/job-2' }),
      response(409, { succeeded: false, failureCode: 'TASK_EDITS_INVALID', failureReason: 'nothing was created', violations }));

    expect(await apiClient.createRevisionFromEdits(draftId)).toEqual({ newJobId: 'job-2', statusUrl: '/api/planning-jobs/job-2' });
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/drafts\/draft-1\/task-edits\/revision$/);
    expect(init.method).toBe('POST');
    expect(init.headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);

    const rejection = await apiClient.createRevisionFromEdits(draftId).catch((err: unknown) => err);
    expect(rejection).toBeInstanceOf(RevisionRejectedError);
    expect((rejection as RevisionRejectedError).message).toBe('TASK_EDITS_INVALID: nothing was created');
    expect((rejection as RevisionRejectedError).violations).toEqual(violations);
  });

  it('lists saved plans with GET /api/drafts', async () => {
    const plans = [{ draftId: 'draft-2', planningJobId: 'job-2', status: 'Approved', createdAtUtc: '2026-10-24T02:00:00Z', assetIds: ['P-101'] }];
    const fetchMock = stubFetch(response(200, plans));

    expect(await apiClient.getSavedPlans()).toEqual(plans);
    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/drafts$/);
  });

  it('reads the action history of a plan with GET /api/planning-jobs/{jobId}/history', async () => {
    const history = [{ id: 'h-1', action: 'Approved', performedBy: 'reviewer', performedAtUtc: '2026-10-24T02:00:00Z' }];
    const fetchMock = stubFetch(response(200, history), response(404));

    expect(await apiClient.getActionHistory('job-1')).toEqual(history);
    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/planning-jobs\/job-1\/history$/);
    await expect(apiClient.getActionHistory('missing')).rejects.toThrow('Planning job not found: missing');
  });

  it('sends the selected role as X-Actor-Role with recorded actions, keeping their other headers', async () => {
    const fetchMock = stubFetch(response(204), response(200, { succeeded: true, taskId: 'P101-PREP', status: 'InProgress' }), response(204));

    apiClient.setActorRole('reviewer');
    await apiClient.cancelDraft(draftId);
    apiClient.setActorRole('technician');
    await apiClient.updateTaskStatus(draftId, 'P101-PREP', 'InProgress');
    apiClient.setActorRole(null);
    await apiClient.cancelDraft(draftId);

    expect(fetchMock.mock.calls[0][1].headers).toEqual({ 'X-Actor-Role': 'reviewer' });
    expect(fetchMock.mock.calls[1][1].headers).toEqual({ 'Content-Type': 'application/json', 'X-Actor-Role': 'technician' });
    expect(fetchMock.mock.calls[2][1]).toEqual({ method: 'POST' });
  });

  it('cancels a draft with POST /cancel and resolves on 204', async () => {
    const fetchMock = stubFetch(response(204));

    await apiClient.cancelDraft(draftId);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/drafts\/draft-1\/cancel$/);
    expect(init.method).toBe('POST');
  });

  it('rejects a cancellation conflict with the API reason and message', async () => {
    stubFetch(response(409, {
      resourceType: 'Draft',
      reason: 'INVALID_DRAFT_STATE',
      resourceId: draftId,
      message: 'Draft is not in approved state',
    }));

    await expect(apiClient.cancelDraft(draftId))
      .rejects.toThrow('INVALID_DRAFT_STATE: Draft is not in approved state');
  });

  it('submits task edits as { edits } and returns the persisted edits', async () => {
    const edit = { taskId: 'P101-PREP', newStart: '2026-10-24T09:00:00+05:30', newEnd: '2026-10-24T10:00:00+05:30', newTechnicianIds: ['TECH-001'] };
    const fetchMock = stubFetch(response(200, { draftId, editedTasks: [] }));

    const result = await apiClient.submitTaskEdits(draftId, [edit]);

    expect(result).toEqual({ draftId, editedTasks: [] });
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/drafts\/draft-1\/task-edits$/);
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({ edits: [edit] });
  });

  it('reports a task-edit conflict (409 without body) as a non-approved draft', async () => {
    stubFetch(response(409));

    await expect(apiClient.submitTaskEdits(draftId, []))
      .rejects.toThrow('Task edits can only be submitted for an approved draft');
  });

  it('validates task edits with POST /task-edits/validate', async () => {
    const validation = { draftId, isValid: false, violations: [{ taskId: 'P101-PREP', code: 'X', message: 'bad' }], validEditsCount: 0, invalidEditsCount: 1 };
    const fetchMock = stubFetch(response(200, validation));

    expect(await apiClient.validateTaskEdits(draftId)).toEqual(validation);
    expect(fetchMock.mock.calls[0][0]).toMatch(/\/task-edits\/validate$/);
    expect(fetchMock.mock.calls[0][1].method).toBe('POST');
  });

  it('deletes one task edit and all task edits with DELETE', async () => {
    const fetchMock = stubFetch(response(204), response(204));

    await apiClient.deleteTaskEdit(draftId, 'P101-PREP');
    await apiClient.clearTaskEdits(draftId);

    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/drafts\/draft-1\/task-edits\/P101-PREP$/);
    expect(fetchMock.mock.calls[0][1].method).toBe('DELETE');
    expect(fetchMock.mock.calls[1][0]).toMatch(/\/api\/drafts\/draft-1\/task-edits$/);
    expect(fetchMock.mock.calls[1][1].method).toBe('DELETE');
  });

  it('reports a missing task edit on delete', async () => {
    stubFetch(response(404));

    await expect(apiClient.deleteTaskEdit(draftId, 'P101-PREP')).rejects.toThrow('No edit for task P101-PREP');
  });

  it('gets the revision chain of a planning job', async () => {
    const revisions = [{ id: 'job-1', status: 'Succeeded', createdAtUtc: '2026-10-24T00:00:00Z', originalJobId: null }];
    const fetchMock = stubFetch(response(200, revisions));

    expect(await apiClient.getRevisions('job-1')).toEqual(revisions);
    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/planning-jobs\/job-1\/revisions$/);
  });

  it('replans with an Idempotency-Key header and the revised request body', async () => {
    const fetchMock = stubFetch(response(202, { newJobId: 'job-2', statusUrl: '/api/planning-jobs/job-2' }));
    const request = { requestText: 'Revised plan', assetIds: ['P-101'], windowId: 'MW-P101-BASELINE-20261024' };

    const accepted = await apiClient.replanJob('job-1', request);

    expect(accepted.newJobId).toBe('job-2');
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/planning-jobs\/job-1\/replan$/);
    expect(init.method).toBe('POST');
    expect(init.headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
    expect(JSON.parse(init.body)).toEqual(request);
  });

  it('rejects a replan conflict with the API message', async () => {
    stubFetch(response(409, 'Original job has no approved draft; cannot create replacement plan.'));

    await expect(apiClient.replanJob('job-1', { requestText: 'x', assetIds: ['P-101'] }))
      .rejects.toThrow('Original job has no approved draft; cannot create replacement plan.');
  });
});
