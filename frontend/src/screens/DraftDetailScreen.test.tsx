/// <reference types="vitest" />
/// <reference types="@testing-library/jest-dom" />

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { DraftDetailScreen } from './DraftDetailScreen';
import { apiClient } from '../services/apiClient';

vi.mock('html2pdf.js', () => ({ default: vi.fn() }));
vi.mock('../services/apiClient', () => ({
  apiClient: {
    getDraftDetail: vi.fn(),
    getPlanProgress: vi.fn(),
    getTaskEdits: vi.fn(),
    getRevisions: vi.fn(),
    getActionHistory: vi.fn(),
    getTaskLocks: vi.fn(),
    setTaskLock: vi.fn(),
    createRevisionFromEdits: vi.fn(),
    updateTaskStatus: vi.fn(),
    cancelDraft: vi.fn(),
    submitTaskEdits: vi.fn(),
    validateTaskEdits: vi.fn(),
    deleteTaskEdit: vi.fn(),
    clearTaskEdits: vi.fn(),
    replanJob: vi.fn(),
    getPlanningJobStatus: vi.fn(),
    exportDraftAsJson: vi.fn(),
    exportDraftAsPdf: vi.fn(),
  },
}));

const api = apiClient as unknown as Record<string, ReturnType<typeof vi.fn>>;

const placements = [
  { taskId: 'P101-PREP', start: '2026-10-24T08:00:00+05:30', end: '2026-10-24T09:00:00+05:30', technicianIds: ['TECH-001'] },
  { taskId: 'P101-BEARING-REPLACEMENT', start: '2026-10-24T09:00:00+05:30', end: '2026-10-24T12:00:00+05:30', technicianIds: ['TECH-001', 'TECH-002'] },
];

const draft = (status = 'Approved', reservationStatus = 'Confirmed') => ({
  draftId: 'draft-1',
  planningJobId: 'job-1',
  status,
  createdAtUtc: '2026-10-24T02:00:00Z',
  approvedAtUtc: '2026-10-24T02:00:00Z',
  selectedCandidate: {
    rationale: 'Stable crew',
    requestedTaskIds: [],
    dependencyTaskIds: [],
    taskPlacements: placements,
    evidenceReferences: [],
    totalLabourCostAmount: 355.5,
    totalLabourCostCurrency: 'UNKNOWN',
    completionTimeMinutes: 360,
    deferralReasons: [],
    isLowestCostCandidate: true,
  },
  reservations: [{
    reservationId: 'res-1',
    sparePartId: 'BR-101',
    quantityReserved: 1,
    status: reservationStatus,
    createdAtUtc: '2026-10-24T02:00:00Z',
    confirmedAtUtc: '2026-10-24T02:00:00Z',
  }],
  interpretation: { maintenanceIntent: 'Replace bearing', scope: 'P-101', proposedAssetIds: ['P-101'], proposedTaskIds: [] },
});

const progress = (prepStatus = 'NotStarted') => ({
  draftId: 'draft-1',
  totalRequired: 1,
  completedQuantity: 0,
  remainingQuantity: 1,
  tasks: [{ sparePartId: 'BR-101', requiredQuantity: 1, completedQuantity: 0, remainingQuantity: 1 }],
  taskStatuses: [
    { taskId: 'P101-PREP', status: prepStatus },
    { taskId: 'P101-BEARING-REPLACEMENT', status: 'InProgress' },
  ],
});

const renderScreen = (onOpenJobAlternatives = vi.fn()) => {
  render(
    <DraftDetailScreen
      draftId="draft-1"
      onBack={vi.fn()}
      onProceedToProgress={vi.fn()}
      onOpenJobAlternatives={onOpenJobAlternatives}
    />
  );
  return onOpenJobAlternatives;
};

describe('DraftDetailScreen draft management', () => {
  beforeEach(() => {
    api.getDraftDetail.mockResolvedValue(draft());
    api.getPlanProgress.mockResolvedValue(progress());
    api.getTaskEdits.mockResolvedValue([]);
    api.getRevisions.mockResolvedValue([
      { id: 'job-1', status: 'Succeeded', createdAtUtc: '2026-10-24T01:00:00Z', originalJobId: null },
    ]);
    api.getActionHistory.mockResolvedValue([]);
    api.getTaskLocks.mockResolvedValue([{ taskId: 'P101-PREP', technicianId: 'TECH-001' }]);
  });

  afterEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it('shows each task lock and lets the user lock a task to one of its technicians or unlock it', async () => {
    api.setTaskLock.mockResolvedValue(undefined);
    renderScreen();

    const prepLock = await screen.findByRole('combobox', { name: 'Lock P101-PREP' }) as HTMLSelectElement;
    const bearingLock = screen.getByRole('combobox', { name: 'Lock P101-BEARING-REPLACEMENT' }) as HTMLSelectElement;
    expect(prepLock.value).toBe('TECH-001');
    expect(bearingLock.value).toBe('');

    api.getTaskLocks.mockResolvedValue([
      { taskId: 'P101-PREP', technicianId: 'TECH-001' },
      { taskId: 'P101-BEARING-REPLACEMENT', technicianId: 'TECH-002' },
    ]);
    fireEvent.change(bearingLock, { target: { value: 'TECH-002' } });

    expect(await screen.findByRole('status')).toHaveTextContent('Task P101-BEARING-REPLACEMENT is locked to TECH-002.');
    expect(api.setTaskLock).toHaveBeenCalledWith('draft-1', 'P101-BEARING-REPLACEMENT', 'TECH-002');
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Lock P101-BEARING-REPLACEMENT' }) as HTMLSelectElement).value).toBe('TECH-002'));

    fireEvent.change(screen.getByRole('combobox', { name: 'Lock P101-PREP' }), { target: { value: '' } });
    await waitFor(() => expect(api.setTaskLock).toHaveBeenLastCalledWith('draft-1', 'P101-PREP', null));
  });

  it('shows every task as unlocked and changeable when the locks load as an empty list', async () => {
    api.getTaskLocks.mockResolvedValue([]);
    renderScreen();

    const prepLock = await screen.findByRole('combobox', { name: 'Lock P101-PREP' }) as HTMLSelectElement;
    await waitFor(() => expect(prepLock).toBeEnabled());
    expect(prepLock.value).toBe('');
    expect(prepLock).toHaveDisplayValue('Unlocked');
    expect(screen.getByRole('combobox', { name: 'Lock P101-BEARING-REPLACEMENT' })).toHaveDisplayValue('Unlocked');
    expect(screen.queryByTestId('locks-error')).not.toBeInTheDocument();
  });

  it('shows the lock state as unknown and not changeable while the locks are loading', async () => {
    api.getTaskLocks.mockReturnValue(new Promise(() => {}));
    renderScreen();

    const prepLock = await screen.findByRole('combobox', { name: 'Lock P101-PREP' });
    expect(prepLock).toBeDisabled();
    expect(prepLock).toHaveDisplayValue('Loading lock…');
    expect(screen.queryByRole('option', { name: 'Unlocked' })).not.toBeInTheDocument();
  });

  it('reports a failed lock load beside the lock controls, disables them instead of showing "Unlocked", and recovers on retry', async () => {
    api.getTaskLocks.mockRejectedValue(new Error('Failed to get task locks: HTTP 500'));
    renderScreen();

    const error = await screen.findByTestId('locks-error');
    expect(error).toHaveTextContent('Task locks could not be loaded');
    expect(error).toHaveTextContent('Failed to get task locks: HTTP 500');
    for (const name of ['Lock P101-PREP', 'Lock P101-BEARING-REPLACEMENT']) {
      const control = screen.getByRole('combobox', { name });
      expect(control).toBeDisabled();
      expect(control).toHaveDisplayValue('Lock state unknown');
    }
    expect(screen.queryByRole('option', { name: 'Unlocked' })).not.toBeInTheDocument();
    expect(api.setTaskLock).not.toHaveBeenCalled();

    api.getTaskLocks.mockResolvedValue([{ taskId: 'P101-PREP', technicianId: 'TECH-001' }]);
    fireEvent.click(screen.getByRole('button', { name: 'Retry loading locks' }));

    await waitFor(() => expect(screen.queryByTestId('locks-error')).not.toBeInTheDocument());
    const prepLock = screen.getByRole('combobox', { name: 'Lock P101-PREP' }) as HTMLSelectElement;
    expect(prepLock).toBeEnabled();
    expect(prepLock.value).toBe('TECH-001');
  });

  it('reports a lock the API rejects instead of applying it', async () => {
    api.setTaskLock.mockRejectedValue(new Error('LOCKED_TECHNICIAN_UNAVAILABLE: Task P101-BEARING-REPLACEMENT cannot be locked to technician TECH-002, who is unavailable'));
    renderScreen();

    fireEvent.change(await screen.findByRole('combobox', { name: 'Lock P101-BEARING-REPLACEMENT' }), { target: { value: 'TECH-002' } });

    expect(await screen.findByRole('alert')).toHaveTextContent('LOCKED_TECHNICIAN_UNAVAILABLE');
    expect((screen.getByRole('combobox', { name: 'Lock P101-BEARING-REPLACEMENT' }) as HTMLSelectElement).value).toBe('');
  });

  it('creates a revision from the edits and opens it for review and approval', async () => {
    api.getTaskEdits.mockResolvedValue([{
      taskId: 'P101-PREP',
      originalStart: '2026-10-24T08:00:00+05:30', originalEnd: '2026-10-24T09:00:00+05:30', originalTechnicianIds: ['TECH-001'],
      newStart: '2026-10-24T08:30:00+05:30', newEnd: '2026-10-24T09:30:00+05:30', newTechnicianIds: ['TECH-001'],
      validationStatus: 'pending',
    }]);
    api.createRevisionFromEdits.mockResolvedValue({ newJobId: 'job-2', statusUrl: '/api/planning-jobs/job-2' });
    const onOpenJobAlternatives = renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: 'Create revision from edits' }));

    expect(await screen.findByTestId('edit-revision')).toHaveTextContent('job-2');
    expect(api.createRevisionFromEdits).toHaveBeenCalledWith('draft-1');
    fireEvent.click(screen.getByRole('button', { name: 'Review and approve revision' }));
    expect(onOpenJobAlternatives).toHaveBeenCalledWith('job-2');
  });

  it('lists every violation when the edited plan is rejected, and offers no revision', async () => {
    api.getTaskEdits.mockResolvedValue([{
      taskId: 'P101-PREP',
      originalStart: '2026-10-24T08:00:00+05:30', originalEnd: '2026-10-24T09:00:00+05:30', originalTechnicianIds: ['TECH-001'],
      newStart: '2026-10-24T08:00:00+05:30', newEnd: '2026-10-24T09:00:00+05:30', newTechnicianIds: ['TECH-002'],
      validationStatus: 'pending',
    }]);
    api.createRevisionFromEdits.mockRejectedValue(Object.assign(
      new Error('TASK_EDITS_INVALID: The edited plan violates current constraints; nothing was created.'),
      { violations: [{ taskId: 'P101-PREP', code: 'LOCKED_ASSIGNMENT_CHANGED', message: 'Task P101-PREP is locked to technician TECH-001' }] }
    ));
    renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: 'Create revision from edits' }));

    expect(await screen.findByTestId('revision-violations')).toHaveTextContent('P101-PREP: LOCKED_ASSIGNMENT_CHANGED');
    expect(screen.getByRole('alert')).toHaveTextContent('TASK_EDITS_INVALID');
    expect(screen.queryByTestId('edit-revision')).not.toBeInTheDocument();
  });

  it('shows who performed each recorded action and reloads the history after an action', async () => {
    api.getActionHistory.mockResolvedValue([
      { id: 'h-1', action: 'Approved', performedBy: 'reviewer', performedAtUtc: '2026-10-24T02:00:00Z', planningJobId: 'job-1', draftId: 'draft-1', taskId: null, details: 'Approved alternative 1 of 2.' },
    ]);
    api.updateTaskStatus.mockResolvedValue({ succeeded: true, taskId: 'P101-PREP', status: 'InProgress' });
    renderScreen();

    const table = await screen.findByTestId('action-history');
    expect(table).toHaveTextContent('Approved');
    expect(table).toHaveTextContent('reviewer');
    expect(table).toHaveTextContent('Approved alternative 1 of 2.');
    expect(table).toHaveTextContent('(this draft)');
    expect(api.getActionHistory).toHaveBeenCalledWith('job-1');

    api.getActionHistory.mockResolvedValue([
      { id: 'h-1', action: 'Approved', performedBy: 'reviewer', performedAtUtc: '2026-10-24T02:00:00Z', planningJobId: 'job-1', draftId: 'draft-1', taskId: null, details: null },
      { id: 'h-2', action: 'TaskProgressUpdated', performedBy: 'technician', performedAtUtc: '2026-10-24T03:00:00Z', planningJobId: 'job-1', draftId: 'draft-1', taskId: 'P101-PREP', details: 'NotStarted -> InProgress' },
    ]);
    fireEvent.click(await screen.findByRole('button', { name: /Start P101-PREP/ }));

    await waitFor(() => expect(screen.getByTestId('action-history')).toHaveTextContent('technician'));
    expect(screen.getByTestId('action-history')).toHaveTextContent('NotStarted -> InProgress');
  });

  it('shows the draft status, task placements with their progress status, and reservations', async () => {
    renderScreen();

    expect(await screen.findByTestId('draft-status')).toHaveTextContent('Approved');
    expect(screen.getByTestId('task-status-P101-PREP')).toHaveTextContent('Not started');
    expect(screen.getByTestId('task-status-P101-BEARING-REPLACEMENT')).toHaveTextContent('In progress');
    expect(screen.getByTestId('reservation-status-res-1')).toHaveTextContent('Confirmed');
    expect(screen.getByTestId('progress-summary')).toHaveTextContent('Parts remaining:1');
    expect(api.getRevisions).toHaveBeenCalledWith('job-1');
  });

  it('offers only the next forward transition and refreshes progress after it succeeds', async () => {
    api.updateTaskStatus.mockResolvedValue({ succeeded: true, taskId: 'P101-PREP', status: 'InProgress' });
    renderScreen();

    const start = await screen.findByRole('button', { name: /Start P101-PREP/ });
    expect(screen.getByRole('button', { name: /Complete P101-BEARING-REPLACEMENT/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Start P101-BEARING-REPLACEMENT/ })).not.toBeInTheDocument();

    api.getPlanProgress.mockResolvedValue(progress('InProgress'));
    fireEvent.click(start);

    expect(await screen.findByRole('status')).toHaveTextContent('Task P101-PREP is now in progress.');
    expect(api.updateTaskStatus).toHaveBeenCalledWith('draft-1', 'P101-PREP', 'InProgress');
    expect(screen.getByTestId('task-status-P101-PREP')).toHaveTextContent('In progress');
  });

  it('shows the API error when a task transition is rejected, without a success message', async () => {
    api.updateTaskStatus.mockRejectedValue(new Error('Task P101-PREP cannot move from NotStarted to InProgress'));
    renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: /Start P101-PREP/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('cannot move from NotStarted to InProgress');
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('asks for confirmation before cancelling and does nothing when declined', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: /Cancel draft/ }));

    expect(confirm).toHaveBeenCalled();
    expect(api.cancelDraft).not.toHaveBeenCalled();
  });

  it('cancels after confirmation, shows the result and the refreshed status and reservations', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    api.cancelDraft.mockResolvedValue(undefined);
    renderScreen();

    const cancel = await screen.findByRole('button', { name: /Cancel draft/ });
    api.getDraftDetail.mockResolvedValue(draft('Cancelled', 'Cancelled'));
    fireEvent.click(cancel);

    expect(await screen.findByRole('status')).toHaveTextContent('Draft cancelled');
    expect(api.cancelDraft).toHaveBeenCalledWith('draft-1');
    expect(screen.getByTestId('draft-status')).toHaveTextContent('Cancelled');
    expect(screen.getByTestId('reservation-status-res-1')).toHaveTextContent('Cancelled');
    // Actions that are no longer valid for a cancelled draft are gone
    expect(screen.queryByRole('button', { name: /Cancel draft/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Start P101-PREP/ })).not.toBeInTheDocument();
  });

  it('shows a cancellation conflict as an error and keeps the draft unchanged', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    api.cancelDraft.mockRejectedValue(new Error('INVALID_DRAFT_STATE: Draft is not in approved state'));
    renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: /Cancel draft/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('INVALID_DRAFT_STATE');
    expect(screen.getByTestId('draft-status')).toHaveTextContent('Approved');
  });

  it('disables actions while a request is in flight, so a double click submits once', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let finish: () => void = () => {};
    api.cancelDraft.mockReturnValue(new Promise<void>((resolve) => { finish = resolve; }));
    renderScreen();

    const cancel = await screen.findByRole('button', { name: /Cancel draft/ });
    fireEvent.click(cancel);
    await waitFor(() => expect(screen.getByRole('button', { name: /Cancelling/ })).toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Cancelling/ }));
    expect(screen.getByRole('button', { name: /Start P101-PREP/ })).toBeDisabled();

    finish();
    await screen.findByRole('status');
    expect(api.cancelDraft).toHaveBeenCalledTimes(1);
  });

  it('hides every mutating action for a superseded draft', async () => {
    api.getDraftDetail.mockResolvedValue(draft('Superseded'));
    renderScreen();

    expect(await screen.findByTestId('draft-status')).toHaveTextContent('Superseded');
    for (const name of [/Cancel draft/, /Start P101-PREP/, /Submit edit/, /Validate edits/,
      /Create replacement plan/, /Download approved revision/]) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
  });

  it('submits a task edit for the selected task and shows the refreshed edits', async () => {
    api.submitTaskEdits.mockResolvedValue({ draftId: 'draft-1', editedTasks: [] });
    renderScreen();

    fireEvent.change(await screen.findByLabelText('Task to edit'), { target: { value: 'P101-PREP' } });
    expect(screen.getByLabelText(/New start/)).toHaveValue('2026-10-24T08:00:00+05:30');
    fireEvent.change(screen.getByLabelText(/New start/), { target: { value: '2026-10-24T13:00:00+05:30' } });
    fireEvent.change(screen.getByLabelText(/New end/), { target: { value: '2026-10-24T14:00:00+05:30' } });
    api.getTaskEdits.mockResolvedValue([{
      taskId: 'P101-PREP',
      originalStart: placements[0].start,
      originalEnd: placements[0].end,
      originalTechnicianIds: ['TECH-001'],
      newStart: '2026-10-24T13:00:00+05:30',
      newEnd: '2026-10-24T14:00:00+05:30',
      newTechnicianIds: ['TECH-001'],
      validationStatus: 'Pending',
    }]);
    fireEvent.click(screen.getByRole('button', { name: 'Submit edit' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Edit for P101-PREP submitted.');
    expect(api.submitTaskEdits).toHaveBeenCalledWith('draft-1', [{
      taskId: 'P101-PREP',
      newStart: '2026-10-24T13:00:00+05:30',
      newEnd: '2026-10-24T14:00:00+05:30',
      newTechnicianIds: ['TECH-001'],
    }]);
    expect(screen.getByRole('button', { name: 'Delete edit for P101-PREP' })).toBeInTheDocument();
  });

  it('shows edit validation violations, and deletes an edit', async () => {
    api.getTaskEdits.mockResolvedValue([{
      taskId: 'P101-PREP',
      originalStart: placements[0].start,
      originalEnd: placements[0].end,
      originalTechnicianIds: ['TECH-001'],
      newStart: placements[0].start,
      newEnd: placements[0].end,
      newTechnicianIds: ['TECH-009'],
      validationStatus: 'Pending',
    }]);
    api.validateTaskEdits.mockResolvedValue({
      draftId: 'draft-1',
      isValid: false,
      violations: [{ taskId: 'P101-PREP', code: 'TECHNICIAN_NOT_FOUND', message: 'TECH-009 does not exist' }],
      validEditsCount: 0,
      invalidEditsCount: 1,
    });
    api.deleteTaskEdit.mockResolvedValue(undefined);
    renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: 'Validate edits' }));
    expect(await screen.findByTestId('edit-validation')).toHaveTextContent('TECHNICIAN_NOT_FOUND');

    api.getTaskEdits.mockResolvedValue([]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete edit for P101-PREP' }));

    await waitFor(() => expect(api.deleteTaskEdit).toHaveBeenCalledWith('draft-1', 'P101-PREP'));
    expect(await screen.findByText('No task edits for this draft.')).toBeInTheDocument();
  });

  it('creates a replacement plan, then shows its planning failures', async () => {
    api.replanJob.mockResolvedValue({ newJobId: 'job-2', statusUrl: '/api/planning-jobs/job-2' });
    api.getPlanningJobStatus.mockResolvedValue({
      jobId: 'job-2',
      status: 'Succeeded',
      resultPayload: JSON.stringify({
        outcome: 'Infeasible',
        result: { violations: [{ code: 'PART_UNAVAILABLE', message: 'Spare part BR-101 has 0 available' }] },
      }),
    });
    const onOpenJobAlternatives = renderScreen();

    fireEvent.change(await screen.findByLabelText('Revised maintenance request'), { target: { value: 'Revise P-101' } });
    expect(screen.getByLabelText(/Asset IDs/)).toHaveValue('P-101');
    fireEvent.click(screen.getByRole('button', { name: /Create replacement plan/ }));

    expect(await screen.findByTestId('replan-job')).toHaveTextContent('job-2');
    expect(api.replanJob).toHaveBeenCalledWith('job-1', { requestText: 'Revise P-101', assetIds: ['P-101'], windowId: undefined });

    fireEvent.click(screen.getByRole('button', { name: /Check replacement job status/ }));

    expect(await screen.findByTestId('replan-violations')).toHaveTextContent('PART_UNAVAILABLE');
    expect(screen.queryByRole('button', { name: /Review and approve V2/ })).not.toBeInTheDocument();
    expect(onOpenJobAlternatives).not.toHaveBeenCalled();
  });

  it('opens the replacement job candidates for approval once they are generated', async () => {
    api.replanJob.mockResolvedValue({ newJobId: 'job-2', statusUrl: '/api/planning-jobs/job-2' });
    api.getPlanningJobStatus.mockResolvedValue({
      jobId: 'job-2',
      status: 'Succeeded',
      resultPayload: JSON.stringify({ outcome: 'CandidatesGenerated', result: { candidates: [] } }),
    });
    const onOpenJobAlternatives = renderScreen();

    fireEvent.change(await screen.findByLabelText('Revised maintenance request'), { target: { value: 'Revise P-101' } });
    fireEvent.click(screen.getByRole('button', { name: /Create replacement plan/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Check replacement job status/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Review and approve V2 candidates/ }));

    expect(onOpenJobAlternatives).toHaveBeenCalledWith('job-2');
  });

  it('shows a replan conflict from the API as an error', async () => {
    api.replanJob.mockRejectedValue(new Error('Original job has no approved draft; cannot create replacement plan.'));
    renderScreen();

    fireEvent.change(await screen.findByLabelText('Revised maintenance request'), { target: { value: 'Revise P-101' } });
    fireEvent.click(screen.getByRole('button', { name: /Create replacement plan/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('no approved draft');
    expect(screen.queryByTestId('replan-job')).not.toBeInTheDocument();
  });

  it('downloads the server JSON export and reports an export failure', async () => {
    api.exportDraftAsJson.mockResolvedValue(undefined);
    api.exportDraftAsPdf.mockRejectedValue(new Error('Cannot export: Only approved drafts can be exported'));
    renderScreen();

    fireEvent.click(await screen.findByRole('button', { name: /Download approved revision \(JSON\)/ }));
    expect(await screen.findByRole('status')).toHaveTextContent('JSON export of the approved revision downloaded.');
    expect(api.exportDraftAsJson).toHaveBeenCalledWith('draft-1');

    fireEvent.click(screen.getByRole('button', { name: /Download approved revision \(PDF\)/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Only approved drafts can be exported');
  });

  it('opens a revision\'s candidates from the revision history', async () => {
    api.getRevisions.mockResolvedValue([
      { id: 'job-1', status: 'Succeeded', createdAtUtc: '2026-10-24T01:00:00Z', originalJobId: null },
      { id: 'job-2', status: 'Succeeded', createdAtUtc: '2026-10-24T03:00:00Z', originalJobId: 'job-1' },
    ]);
    const onOpenJobAlternatives = renderScreen();

    const buttons = await screen.findAllByRole('button', { name: 'View candidates' });
    fireEvent.click(buttons[1]);

    expect(onOpenJobAlternatives).toHaveBeenCalledWith('job-2');
    expect(screen.getByText(/\(this draft\)/)).toBeInTheDocument();
  });
});
