import React, { useState, useEffect, useRef, useCallback } from 'react';
import { apiClient } from '../services/apiClient';
import type {
  BusinessActionResponse,
  DraftDetailResponse,
  EditedTaskPlacementResponse,
  EditValidationResult,
  EditValidationViolation,
  PlanningJobRevisionResponse,
  PlanningJobStatusResponse,
  PlanProgressResponse,
  TaskProgressStatus,
} from '../types/api';
import html2pdf from 'html2pdf.js';
import '../styles/DraftDetailScreen.css';

interface DraftDetailScreenProps {
  draftId: string;
  onBack: () => void;
  onProceedToProgress: (draftId: string) => void;
  // Opens a planning job's alternatives (e.g. a V2 replacement job) to review and approve a candidate
  onOpenJobAlternatives?: (jobId: string) => void;
}

type LoadingState = 'loading' | 'success' | 'error';

interface ActionMessage {
  kind: 'success' | 'error';
  text: string;
}

const STATUS_LABELS: Record<TaskProgressStatus, string> = {
  NotStarted: 'Not started',
  InProgress: 'In progress',
  Completed: 'Completed',
};

// Tasks move forward only: NotStarted → InProgress → Completed (the API rejects any other move)
const NEXT_STATUS: Record<TaskProgressStatus, TaskProgressStatus | null> = {
  NotStarted: 'InProgress',
  InProgress: 'Completed',
  Completed: null,
};

// Outcome and violations of a planning job's result payload, if it can be read
const readJobOutcome = (resultPayload: string | null) => {
  if (!resultPayload) {
    return { outcome: '', violations: [] as { code: string; message: string }[] };
  }
  try {
    const result = JSON.parse(resultPayload);
    return {
      outcome: (result?.outcome as string) || '',
      violations: ((result?.result?.violations as { code: string; message: string }[]) || []),
    };
  } catch {
    return { outcome: '', violations: [] as { code: string; message: string }[] };
  }
};

export const DraftDetailScreen: React.FC<DraftDetailScreenProps> = ({
  draftId,
  onBack,
  onProceedToProgress,
  onOpenJobAlternatives,
}) => {
  const [draft, setDraft] = useState<DraftDetailResponse | null>(null);
  const [loadingState, setLoadingState] = useState<LoadingState>('loading');
  const [error, setError] = useState<string | null>(null);
  const [pdfExportLoading, setPdfExportLoading] = useState(false);
  const [pdfExportError, setPdfExportError] = useState<string | null>(null);
  const contentRef = useRef<HTMLDivElement>(null);

  // Draft management: one action at a time, with its actual result
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<ActionMessage | null>(null);
  const [progress, setProgress] = useState<PlanProgressResponse | null>(null);
  const [progressError, setProgressError] = useState<string | null>(null);
  const [edits, setEdits] = useState<EditedTaskPlacementResponse[]>([]);
  const [editsError, setEditsError] = useState<string | null>(null);
  const [editValidation, setEditValidation] = useState<EditValidationResult | null>(null);
  const [editTaskId, setEditTaskId] = useState('');
  const [editStart, setEditStart] = useState('');
  const [editEnd, setEditEnd] = useState('');
  const [editTechnicians, setEditTechnicians] = useState('');
  const [revisions, setRevisions] = useState<PlanningJobRevisionResponse[] | null>(null);
  const [revisionsError, setRevisionsError] = useState<string | null>(null);
  const [history, setHistory] = useState<BusinessActionResponse[] | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  // FR05: technician locks by task (null until they are loaded successfully: the lock state is then unknown, not
  // "unlocked"), and the revision built from the task edits
  const [locks, setLocks] = useState<Record<string, string> | null>(null);
  const [locksError, setLocksError] = useState<string | null>(null);
  const [revisionJobId, setRevisionJobId] = useState<string | null>(null);
  const [revisionViolations, setRevisionViolations] = useState<EditValidationViolation[]>([]);
  const [replanRequestText, setReplanRequestText] = useState('');
  const [replanAssetIds, setReplanAssetIds] = useState('');
  const [replanWindowId, setReplanWindowId] = useState('');
  const [replanJobId, setReplanJobId] = useState<string | null>(null);
  const [replanJobStatus, setReplanJobStatus] = useState<PlanningJobStatusResponse | null>(null);

  const loadDraft = useCallback(async () => {
    const response = await apiClient.getDraftDetail(draftId);
    setDraft(response);
    return response;
  }, [draftId]);

  const loadProgress = useCallback(async () => {
    try {
      setProgress(await apiClient.getPlanProgress(draftId));
      setProgressError(null);
    } catch (err) {
      setProgressError(err instanceof Error ? err.message : 'Failed to load progress');
    }
  }, [draftId]);

  const loadEdits = useCallback(async () => {
    try {
      setEdits(await apiClient.getTaskEdits(draftId));
      setEditsError(null);
    } catch (err) {
      setEditsError(err instanceof Error ? err.message : 'Failed to load task edits');
    }
  }, [draftId]);

  const loadRevisions = useCallback(async (planningJobId: string) => {
    try {
      setRevisions(await apiClient.getRevisions(planningJobId));
      setRevisionsError(null);
    } catch (err) {
      setRevisionsError(err instanceof Error ? err.message : 'Failed to load revision history');
    }
  }, []);

  const loadLocks = useCallback(async () => {
    try {
      const response = await apiClient.getTaskLocks(draftId);
      setLocks(Object.fromEntries(response.map((lock) => [lock.taskId, lock.technicianId])));
      setLocksError(null);
    } catch (err) {
      setLocks(null);
      setLocksError(err instanceof Error ? err.message : 'Failed to load task locks');
    }
  }, [draftId]);

  // Who did what and when, across every revision of this plan
  const loadHistory = useCallback(async (planningJobId: string) => {
    try {
      setHistory(await apiClient.getActionHistory(planningJobId));
      setHistoryError(null);
    } catch (err) {
      setHistoryError(err instanceof Error ? err.message : 'Failed to load action history');
    }
  }, []);

  // Reloads the draft (status, reservations), its progress, edits, revision and action history
  const refreshAll = useCallback(async () => {
    try {
      const response = await loadDraft();
      await Promise.all([
        loadProgress(),
        loadEdits(),
        loadLocks(),
        loadRevisions(response.planningJobId),
        loadHistory(response.planningJobId),
      ]);
    } catch (err) {
      setActionMessage({
        kind: 'error',
        text: err instanceof Error ? err.message : 'Failed to reload draft details',
      });
    }
  }, [loadDraft, loadProgress, loadEdits, loadLocks, loadRevisions, loadHistory]);

  useEffect(() => {
    const fetchDraftDetail = async () => {
      setLoadingState('loading');
      setError(null);
      try {
        const response = await loadDraft();
        setLoadingState('success');
        setReplanAssetIds((response.interpretation?.proposedAssetIds ?? []).join(', '));
        await Promise.all([
          loadProgress(),
          loadEdits(),
          loadLocks(),
          loadRevisions(response.planningJobId),
          loadHistory(response.planningJobId),
        ]);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load draft details');
        setLoadingState('error');
      }
    };

    fetchDraftDetail();
  }, [loadDraft, loadProgress, loadEdits, loadLocks, loadRevisions, loadHistory]);

  // Runs one mutating action; further actions are disabled until it finishes. Success is shown only when the API
  // call succeeded, followed by a refresh of the affected data.
  const runAction = async (
    name: string,
    action: () => Promise<string>,
    refresh?: () => Promise<void>
  ) => {
    if (busyAction) {
      return;
    }

    setBusyAction(name);
    setActionMessage(null);
    try {
      const text = await action();
      if (refresh) {
        await refresh();
      }
      // Every successful action may have been recorded in the plan's history
      if (draft) {
        await loadHistory(draft.planningJobId);
      }
      setActionMessage({ kind: 'success', text });
    } catch (err) {
      setActionMessage({ kind: 'error', text: err instanceof Error ? err.message : 'Request failed' });
    } finally {
      setBusyAction(null);
    }
  };

  if (loadingState === 'loading') {
    return (
      <div className="draft-detail-screen">
        <div className="screen-header">
          <button onClick={onBack} className="btn-back">
            ← Back
          </button>
          <h1>Draft Details</h1>
          <div className="header-spacer"></div>
        </div>
        <div className="screen-content">
          <div className="loading-container">
            <div className="spinner"></div>
            <p>Loading draft details...</p>
          </div>
        </div>
      </div>
    );
  }

  if (loadingState === 'error' || !draft) {
    return (
      <div className="draft-detail-screen">
        <div className="screen-header">
          <button onClick={onBack} className="btn-back">
            ← Back
          </button>
          <h1>Draft Details</h1>
          <div className="header-spacer"></div>
        </div>
        <div className="screen-content">
          <div className="error-container">
            <div className="alert alert-error">
              {error || 'Failed to load draft details'}
            </div>
            <button onClick={onBack} className="btn btn-primary">
              ← Back
            </button>
          </div>
        </div>
      </div>
    );
  }

  const formatDate = (dateString: string) => new Date(dateString).toLocaleString();
  // Only an approved draft can be cancelled, progressed, edited, exported or replanned (backend rules)
  const isApproved = draft.status === 'Approved';
  const statusByTask = new Map(
    (progress?.taskStatuses ?? []).map((task) => [task.taskId, task.status] as const)
  );
  const placements = draft.selectedCandidate?.taskPlacements ?? [];

  const handleCancelDraft = () => {
    if (!window.confirm(
      `Cancel draft ${draft.draftId}? Unused spare-part reservations will be released. This cannot be undone.`
    )) {
      return;
    }

    runAction(
      'cancel',
      async () => {
        await apiClient.cancelDraft(draft.draftId);
        return 'Draft cancelled. Unused reservations were released.';
      },
      refreshAll
    );
  };

  const handleTaskStatus = (taskId: string, status: TaskProgressStatus) =>
    runAction(
      `task-${taskId}`,
      async () => {
        await apiClient.updateTaskStatus(draft.draftId, taskId, status);
        return `Task ${taskId} is now ${STATUS_LABELS[status].toLowerCase()}.`;
      },
      async () => {
        await Promise.all([loadProgress(), loadDraft().then(() => undefined)]);
      }
    );

  const handleSelectEditTask = (taskId: string) => {
    setEditTaskId(taskId);
    const placement = placements.find((p) => p.taskId === taskId);
    setEditStart(placement?.start ?? '');
    setEditEnd(placement?.end ?? '');
    setEditTechnicians(placement?.technicianIds.join(', ') ?? '');
  };

  const handleSubmitEdit = (e: React.FormEvent) => {
    e.preventDefault();
    const technicianIds = editTechnicians.split(',').map((id) => id.trim()).filter(Boolean);
    if (!editTaskId || !editStart.trim() || !editEnd.trim() || technicianIds.length === 0) {
      setActionMessage({ kind: 'error', text: 'Task, new start, new end and at least one technician are required' });
      return;
    }

    runAction(
      'submit-edit',
      async () => {
        await apiClient.submitTaskEdits(draft.draftId, [
          { taskId: editTaskId, newStart: editStart.trim(), newEnd: editEnd.trim(), newTechnicianIds: technicianIds },
        ]);
        setEditValidation(null);
        return `Edit for ${editTaskId} submitted.`;
      },
      loadEdits
    );
  };

  const handleValidateEdits = () =>
    runAction(
      'validate-edits',
      async () => {
        const result = await apiClient.validateTaskEdits(draft.draftId);
        setEditValidation(result);
        return result.isValid
          ? `All ${result.validEditsCount} edit(s) are valid.`
          : `Validation finished: ${result.invalidEditsCount} invalid edit(s).`;
      },
      loadEdits
    );

  const handleDeleteEdit = (taskId: string) =>
    runAction(
      `delete-edit-${taskId}`,
      async () => {
        await apiClient.deleteTaskEdit(draft.draftId, taskId);
        setEditValidation(null);
        return `Edit for ${taskId} deleted.`;
      },
      loadEdits
    );

  const handleClearEdits = () => {
    if (!window.confirm('Delete all task edits for this draft?')) {
      return;
    }

    runAction(
      'clear-edits',
      async () => {
        await apiClient.clearTaskEdits(draft.draftId);
        setEditValidation(null);
        return 'All task edits deleted.';
      },
      loadEdits
    );
  };

  // Locks a task to one of its technicians, or unlocks it; an impossible lock is reported, not applied
  const handleLockChange = (taskId: string, technicianId: string | null) =>
    locks !== null && runAction(
      `lock-${taskId}`,
      async () => {
        await apiClient.setTaskLock(draft.draftId, taskId, technicianId);
        return technicianId ? `Task ${taskId} is locked to ${technicianId}.` : `Task ${taskId} is unlocked.`;
      },
      loadLocks
    );

  const handleCreateRevision = () =>
    runAction(
      'create-revision',
      async () => {
        setRevisionViolations([]);
        try {
          const accepted = await apiClient.createRevisionFromEdits(draft.draftId);
          setRevisionJobId(accepted.newJobId);
          return `Revision ${accepted.newJobId} created from the edits. Review and approve it to replace this plan.`;
        } catch (err) {
          if (err && typeof err === 'object' && 'violations' in err && Array.isArray(err.violations)) {
            setRevisionViolations(err.violations as EditValidationViolation[]);
          }
          throw err;
        }
      },
      async () => {
        await Promise.all([loadEdits(), loadRevisions(draft.planningJobId)]);
      }
    );

  const handleReplan = (e: React.FormEvent) => {
    e.preventDefault();
    const assetIds = replanAssetIds.split(',').map((id) => id.trim()).filter(Boolean);
    if (!replanRequestText.trim() || assetIds.length === 0) {
      setActionMessage({ kind: 'error', text: 'A revised request and at least one asset ID are required to replan' });
      return;
    }

    runAction(
      'replan',
      async () => {
        const accepted = await apiClient.replanJob(draft.planningJobId, {
          requestText: replanRequestText.trim(),
          assetIds,
          windowId: replanWindowId.trim() || undefined,
        });
        setReplanJobId(accepted.newJobId);
        setReplanJobStatus(null);
        return `Replacement planning job ${accepted.newJobId} created. Check its status, then review and approve a candidate.`;
      },
      () => loadRevisions(draft.planningJobId)
    );
  };

  const handleCheckReplanStatus = () => {
    if (!replanJobId) {
      return;
    }

    runAction('replan-status', async () => {
      const status = await apiClient.getPlanningJobStatus(replanJobId);
      setReplanJobStatus(status);
      return `Replacement job status: ${status.status}.`;
    });
  };

  const handleServerExport = (format: 'json' | 'pdf') =>
    runAction(`export-${format}`, async () => {
      if (format === 'json') {
        await apiClient.exportDraftAsJson(draft.draftId);
      } else {
        await apiClient.exportDraftAsPdf(draft.draftId);
      }
      return `${format.toUpperCase()} export of the approved revision downloaded.`;
    });

  const handleExportPdf = async () => {
    if (!contentRef.current || !draft) {
      setPdfExportError('Unable to export PDF: content not available');
      return;
    }

    setPdfExportLoading(true);
    setPdfExportError(null);

    try {
      const element = contentRef.current;
      console.log('[PDF Export] Starting PDF export for draft:', draft.draftId);
      console.log('[PDF Export] Element content length:', element.innerHTML.length);

      // Temporarily hide buttons and alerts in the actual DOM
      const actionSections = element.querySelectorAll('.action-section, .alert');
      const originalDisplay: string[] = [];

      actionSections.forEach((section) => {
        originalDisplay.push((section as HTMLElement).style.display);
        (section as HTMLElement).style.display = 'none';
      });

      // Create wrapper div for proper A4 sizing
      const wrapper = document.createElement('div');
      wrapper.style.width = '210mm';
      wrapper.style.padding = '10mm';
      wrapper.style.background = 'white';

      // Clone and append to wrapper for capturing with styles
      const clone = element.cloneNode(true) as HTMLElement;
      wrapper.appendChild(clone);

      // Append wrapper to body so styles are computed
      document.body.appendChild(wrapper);

      // Wait for rendering
      await new Promise(resolve => setTimeout(resolve, 100));

      const options = {
        margin: [10, 10, 10, 10],
        filename: `Draft-${draft.draftId}.pdf`,
        image: { type: 'jpeg', quality: 0.98 },
        html2canvas: { scale: 2, logging: false, useCORS: true },
        jsPDF: { orientation: 'portrait', unit: 'mm', format: 'a4' },
        pagebreak: { mode: ['avoid-all', 'css', 'legacy'] },
      };

      console.log('[PDF Export] Starting html2pdf capture');
      await html2pdf().set(options as any).from(wrapper).save();
      console.log('[PDF Export] PDF generated successfully');

      // Clean up
      document.body.removeChild(wrapper);

      // Restore visibility of action sections
      actionSections.forEach((section, index) => {
        (section as HTMLElement).style.display = originalDisplay[index];
      });
    } catch (err) {
      console.error('[PDF Export] Failed to generate PDF:', err);
      setPdfExportError(err instanceof Error ? err.message : 'Failed to generate PDF');
    } finally {
      setPdfExportLoading(false);
    }
  };

  const replanOutcome = readJobOutcome(replanJobStatus?.resultPayload ?? null);

  return (
    <div className="draft-detail-screen">
      <div className="screen-header">
        <button onClick={onBack} className="btn-back">
          ← Back
        </button>
        <h1>Draft Details</h1>
        <div className="header-spacer"></div>
      </div>

      <div className="screen-content" ref={contentRef}>
        {/* Result of the last draft action */}
        {actionMessage && (
          <div
            role={actionMessage.kind === 'error' ? 'alert' : 'status'}
            className={`alert ${actionMessage.kind === 'error' ? 'alert-error' : 'alert-success'}`}
          >
            {actionMessage.text}
          </div>
        )}

        {/* Draft Header */}
        <section className="detail-section">
          <h2>Draft Information</h2>
          <div className="detail-grid">
            <div className="detail-item">
              <span className="label">Draft ID:</span>
              <code className="value">{draft.draftId}</code>
            </div>
            <div className="detail-item">
              <span className="label">Planning Job ID:</span>
              <code className="value">{draft.planningJobId}</code>
            </div>
            <div className="detail-item">
              <span className="label">Status:</span>
              <span className={`status-badge status-${draft.status.toLowerCase()}`} data-testid="draft-status">
                {draft.status}
              </span>
            </div>
            <div className="detail-item">
              <span className="label">Created:</span>
              <span className="value">{formatDate(draft.createdAtUtc)}</span>
            </div>
            {draft.approvedAtUtc && (
              <div className="detail-item">
                <span className="label">Approved:</span>
                <span className="value">{formatDate(draft.approvedAtUtc)}</span>
              </div>
            )}
          </div>
          {!isApproved && (
            <p className="empty-message">
              This draft is {draft.status}; it is kept as history and can no longer be cancelled, progressed, edited,
              exported or replanned.
            </p>
          )}
        </section>

        {/* Selected Candidate */}
        {draft.selectedCandidate && (
          <section className="detail-section">
            <h2>Selected Maintenance Plan</h2>
            <div className="plan-summary">
              <div className="summary-item">
                <span className="label">Rationale:</span>
                <span className="value">{draft.selectedCandidate.rationale}</span>
              </div>
              <div className="summary-item">
                <span className="label">Duration:</span>
                <span className="value">
                  {Math.floor(draft.selectedCandidate.completionTimeMinutes / 60)}h{' '}
                  {draft.selectedCandidate.completionTimeMinutes % 60}m
                </span>
              </div>
              <div className="summary-item">
                <span className="label">Labour Cost:</span>
                <span className="value">
                  {draft.selectedCandidate.totalLabourCostAmount}
                  {draft.selectedCandidate.totalLabourCostCurrency === 'UNKNOWN'
                    ? ' (Currency unknown)'
                    : ` ${draft.selectedCandidate.totalLabourCostCurrency}`}
                </span>
              </div>
            </div>

            {/* Task Placements with their progress status */}
            {placements.length > 0 && (
              <div className="tasks-subsection">
                <h3>Scheduled Tasks</h3>
                {locksError && (
                  <div className="alert alert-error" data-testid="locks-error">
                    Task locks could not be loaded, so their state is unknown and they cannot be changed: {locksError}{' '}
                    <button onClick={() => loadLocks()} disabled={busyAction !== null} className="btn btn-secondary">
                      Retry loading locks
                    </button>
                  </div>
                )}
                <div className="table-container">
                  <table className="tasks-table">
                    <thead>
                      <tr>
                        <th>Task</th>
                        <th>Start</th>
                        <th>End</th>
                        <th>Technicians</th>
                        <th>Status</th>
                        <th>Lock</th>
                        {isApproved && <th>Progress</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {placements.map((task, idx) => {
                        const start = new Date(task.start);
                        const end = new Date(task.end);
                        const status = statusByTask.get(task.taskId);
                        const next = status ? NEXT_STATUS[status] : null;
                        return (
                          <tr key={idx}>
                            <td>{task.taskId}</td>
                            <td>{start.toLocaleString()}</td>
                            <td>{end.toLocaleString()}</td>
                            <td>{task.technicianIds.join(', ') || '—'}</td>
                            <td data-testid={`task-status-${task.taskId}`}>
                              {status ? STATUS_LABELS[status] : '—'}
                            </td>
                            <td>
                              {locks === null ? (
                                // Unknown until loaded: shown as such and not changeable, never as "Unlocked"
                                <select aria-label={`Lock ${task.taskId}`} value="unknown" disabled className="input">
                                  <option value="unknown">{locksError ? 'Lock state unknown' : 'Loading lock…'}</option>
                                </select>
                              ) : isApproved ? (
                                <select
                                  aria-label={`Lock ${task.taskId}`}
                                  value={locks[task.taskId] ?? ''}
                                  onChange={(e) => handleLockChange(task.taskId, e.target.value || null)}
                                  disabled={busyAction !== null}
                                  className="input"
                                >
                                  <option value="">Unlocked</option>
                                  {[...new Set([...task.technicianIds, ...(locks[task.taskId] ? [locks[task.taskId]] : [])])].map((technicianId) => (
                                    <option key={technicianId} value={technicianId}>Locked to {technicianId}</option>
                                  ))}
                                </select>
                              ) : (
                                locks[task.taskId] ?? 'Unlocked'
                              )}
                            </td>
                            {isApproved && (
                              <td>
                                {next && (
                                  <button
                                    onClick={() => handleTaskStatus(task.taskId, next)}
                                    disabled={busyAction !== null}
                                    className="btn btn-secondary"
                                  >
                                    {busyAction === `task-${task.taskId}`
                                      ? '⏳ Updating...'
                                      : next === 'InProgress' ? `▶ Start ${task.taskId}` : `✓ Complete ${task.taskId}`}
                                  </button>
                                )}
                              </td>
                            )}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </section>
        )}

        {/* Interpretation */}
        {draft.interpretation && (
          <section className="detail-section">
            <h2>Maintenance Interpretation</h2>
            <div className="interpretation-grid">
              <div className="interpretation-item">
                <span className="label">Intent:</span>
                <span className="value">{draft.interpretation.maintenanceIntent}</span>
              </div>
              <div className="interpretation-item">
                <span className="label">Scope:</span>
                <span className="value">{draft.interpretation.scope}</span>
              </div>
              {draft.interpretation.proposedAssetIds.length > 0 && (
                <div className="interpretation-item">
                  <span className="label">Assets:</span>
                  <span className="value">{draft.interpretation.proposedAssetIds.join(', ')}</span>
                </div>
              )}
            </div>
          </section>
        )}

        {/* Reservations */}
        <section className="detail-section">
          <h2>Spare Parts Reservations</h2>
          {draft.reservations.length > 0 ? (
            <div className="reservations-container">
              {draft.reservations.map((reservation) => (
                <div key={reservation.reservationId} className="reservation-card">
                  <div className="reservation-header">
                    <span className="part-id">{reservation.sparePartId}</span>
                    <span
                      className={`reservation-status status-${reservation.status.toLowerCase()}`}
                      data-testid={`reservation-status-${reservation.reservationId}`}
                    >
                      {reservation.status}
                    </span>
                  </div>
                  <div className="reservation-details">
                    <div className="detail">
                      <span className="label">Quantity Reserved:</span>
                      <span className="value">{reservation.quantityReserved}</span>
                    </div>
                    <div className="detail">
                      <span className="label">Created:</span>
                      <span className="value">{formatDate(reservation.createdAtUtc)}</span>
                    </div>
                    {reservation.confirmedAtUtc && (
                      <div className="detail">
                        <span className="label">Confirmed:</span>
                        <span className="value">{formatDate(reservation.confirmedAtUtc)}</span>
                      </div>
                    )}
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <p className="empty-message">No spare parts reserved for this draft.</p>
          )}
        </section>

        {/* Progress and remaining work */}
        <section className="detail-section">
          <h2>Progress and Remaining Work</h2>
          {progressError && <div className="alert alert-error">{progressError}</div>}
          {progress && (
            <div className="detail-grid" data-testid="progress-summary">
              <div className="detail-item">
                <span className="label">Parts required:</span>
                <span className="value">{progress.totalRequired}</span>
              </div>
              <div className="detail-item">
                <span className="label">Parts consumed:</span>
                <span className="value">{progress.completedQuantity}</span>
              </div>
              <div className="detail-item">
                <span className="label">Parts remaining:</span>
                <span className="value">{progress.remainingQuantity}</span>
              </div>
            </div>
          )}
          <div className="action-section">
            <button
              onClick={() => runAction('refresh-progress', async () => {
                await loadProgress();
                return 'Progress refreshed.';
              })}
              disabled={busyAction !== null}
              className="btn btn-secondary"
            >
              ↻ Refresh progress
            </button>
          </div>
        </section>

        {/* Task edits */}
        <section className="detail-section">
          <h2>Task Edits</h2>
          {editsError && <div className="alert alert-error">{editsError}</div>}
          {edits.length > 0 ? (
            <div className="table-container">
              <table className="tasks-table">
                <thead>
                  <tr>
                    <th>Task</th>
                    <th>Original</th>
                    <th>Proposed</th>
                    <th>Validation</th>
                    {isApproved && <th></th>}
                  </tr>
                </thead>
                <tbody>
                  {edits.map((edit) => (
                    <tr key={edit.taskId}>
                      <td>{edit.taskId}</td>
                      <td>
                        {formatDate(edit.originalStart)} – {formatDate(edit.originalEnd)} ({edit.originalTechnicianIds.join(', ')})
                      </td>
                      <td>
                        {formatDate(edit.newStart)} – {formatDate(edit.newEnd)} ({edit.newTechnicianIds.join(', ')})
                      </td>
                      <td>{edit.validationStatus}</td>
                      {isApproved && (
                        <td>
                          <button
                            onClick={() => handleDeleteEdit(edit.taskId)}
                            disabled={busyAction !== null}
                            className="btn btn-secondary"
                          >
                            Delete edit for {edit.taskId}
                          </button>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="empty-message">No task edits for this draft.</p>
          )}

          {editValidation && (
            <div
              className={`alert ${editValidation.isValid ? 'alert-success' : 'alert-error'}`}
              data-testid="edit-validation"
            >
              {editValidation.isValid
                ? `Edits are valid (${editValidation.validEditsCount} valid).`
                : `${editValidation.invalidEditsCount} invalid edit(s):`}
              {editValidation.violations.length > 0 && (
                <ul>
                  {editValidation.violations.map((violation, idx) => (
                    <li key={idx}>{violation.taskId}: {violation.code} – {violation.message}</li>
                  ))}
                </ul>
              )}
            </div>
          )}

          {isApproved && (
            <>
              <form onSubmit={handleSubmitEdit} className="tile-form">
                <div className="form-group">
                  <label htmlFor="editTaskId">Task to edit</label>
                  <select
                    id="editTaskId"
                    value={editTaskId}
                    onChange={(e) => handleSelectEditTask(e.target.value)}
                    disabled={busyAction !== null}
                    className="input"
                  >
                    <option value="">Select a task</option>
                    {placements.map((p) => (
                      <option key={p.taskId} value={p.taskId}>{p.taskId}</option>
                    ))}
                  </select>
                </div>
                <div className="form-group">
                  <label htmlFor="editStart">New start (ISO 8601 with offset)</label>
                  <input id="editStart" className="input" value={editStart}
                    onChange={(e) => setEditStart(e.target.value)} disabled={busyAction !== null} />
                </div>
                <div className="form-group">
                  <label htmlFor="editEnd">New end (ISO 8601 with offset)</label>
                  <input id="editEnd" className="input" value={editEnd}
                    onChange={(e) => setEditEnd(e.target.value)} disabled={busyAction !== null} />
                </div>
                <div className="form-group">
                  <label htmlFor="editTechnicians">Technician IDs (comma-separated)</label>
                  <input id="editTechnicians" className="input" value={editTechnicians}
                    onChange={(e) => setEditTechnicians(e.target.value)} disabled={busyAction !== null} />
                </div>
                <button type="submit" disabled={busyAction !== null} className="btn btn-primary">
                  {busyAction === 'submit-edit' ? '⏳ Submitting...' : 'Submit edit'}
                </button>
              </form>
              <div className="action-section">
                <button onClick={handleValidateEdits} disabled={busyAction !== null} className="btn btn-secondary">
                  {busyAction === 'validate-edits' ? '⏳ Validating...' : 'Validate edits'}
                </button>
                {edits.length > 0 && (
                  <button onClick={handleClearEdits} disabled={busyAction !== null} className="btn btn-secondary">
                    Delete all edits
                  </button>
                )}
                {edits.length > 0 && (
                  <button onClick={handleCreateRevision} disabled={busyAction !== null} className="btn btn-primary">
                    {busyAction === 'create-revision' ? '⏳ Creating revision...' : 'Create revision from edits'}
                  </button>
                )}
              </div>
            </>
          )}

          {revisionViolations.length > 0 && (
            <div className="alert alert-error" data-testid="revision-violations">
              The edited plan cannot be used; nothing was created:
              <ul>
                {revisionViolations.map((violation, idx) => (
                  <li key={idx}>{violation.taskId ? `${violation.taskId}: ` : ''}{violation.code} – {violation.message}</li>
                ))}
              </ul>
            </div>
          )}

          {revisionJobId && (
            <div className="action-section" data-testid="edit-revision">
              <p>Revision job: <code>{revisionJobId}</code></p>
              {onOpenJobAlternatives && (
                <button onClick={() => onOpenJobAlternatives(revisionJobId)} className="btn btn-primary">
                  Review and approve revision
                </button>
              )}
            </div>
          )}
        </section>

        {/* Revision history */}
        <section className="detail-section">
          <h2>Revision History</h2>
          {revisionsError && <div className="alert alert-error">{revisionsError}</div>}
          {revisions && revisions.length > 0 ? (
            <div className="table-container">
              <table className="tasks-table">
                <thead>
                  <tr>
                    <th>Planning job</th>
                    <th>Status</th>
                    <th>Created</th>
                    <th>Replaces</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {revisions.map((revision) => (
                    <tr key={revision.id}>
                      <td>
                        <code>{revision.id}</code>
                        {revision.id === draft.planningJobId && ' (this draft)'}
                      </td>
                      <td>{revision.status}</td>
                      <td>{formatDate(revision.createdAtUtc)}</td>
                      <td>{revision.originalJobId ? <code>{revision.originalJobId}</code> : '—'}</td>
                      <td>
                        {onOpenJobAlternatives && revision.status === 'Succeeded' && (
                          <button onClick={() => onOpenJobAlternatives(revision.id)} className="btn btn-secondary">
                            View candidates
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            !revisionsError && <p className="empty-message">No revision history available.</p>
          )}
        </section>

        {/* Business-action history (FR09) */}
        <section className="detail-section">
          <h2>Action History</h2>
          {historyError && <div className="alert alert-error">{historyError}</div>}
          {history && history.length > 0 ? (
            <div className="table-container">
              <table className="tasks-table" data-testid="action-history">
                <thead>
                  <tr>
                    <th>When</th>
                    <th>Action</th>
                    <th>Performed by</th>
                    <th>Draft</th>
                    <th>Task</th>
                    <th>Details</th>
                  </tr>
                </thead>
                <tbody>
                  {history.map((entry) => (
                    <tr key={entry.id}>
                      <td>{formatDate(entry.performedAtUtc)}</td>
                      <td>{entry.action}</td>
                      <td>{entry.performedBy}</td>
                      <td>
                        {entry.draftId ? <code>{entry.draftId}</code> : '—'}
                        {entry.draftId === draft.draftId && ' (this draft)'}
                      </td>
                      <td>{entry.taskId ?? '—'}</td>
                      <td>{entry.details ?? ''}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            !historyError && <p className="empty-message">No recorded actions for this plan.</p>
          )}
        </section>

        {/* Replacement (V1 → V2) */}
        {isApproved && (
          <section className="detail-section">
            <h2>Replan This Approved Draft</h2>
            <form onSubmit={handleReplan} className="tile-form">
              <div className="form-group">
                <label htmlFor="replanRequestText">Revised maintenance request</label>
                <textarea id="replanRequestText" className="textarea" rows={3} value={replanRequestText}
                  onChange={(e) => setReplanRequestText(e.target.value)} disabled={busyAction !== null} />
              </div>
              <div className="form-group">
                <label htmlFor="replanAssetIds">Asset IDs (comma-separated)</label>
                <input id="replanAssetIds" className="input" value={replanAssetIds}
                  onChange={(e) => setReplanAssetIds(e.target.value)} disabled={busyAction !== null} />
              </div>
              <div className="form-group">
                <label htmlFor="replanWindowId">Maintenance window ID (optional)</label>
                <input id="replanWindowId" className="input" value={replanWindowId}
                  onChange={(e) => setReplanWindowId(e.target.value)} disabled={busyAction !== null} />
              </div>
              <button type="submit" disabled={busyAction !== null} className="btn btn-primary">
                {busyAction === 'replan' ? '⏳ Creating replacement...' : 'Create replacement plan (V2)'}
              </button>
            </form>
          </section>
        )}

        {replanJobId && (
          <section className="detail-section" data-testid="replan-job">
            <h2>Replacement Job</h2>
            <p>Job ID: <code>{replanJobId}</code></p>
            <div className="action-section">
              <button onClick={handleCheckReplanStatus} disabled={busyAction !== null} className="btn btn-secondary">
                {busyAction === 'replan-status' ? '⏳ Checking...' : 'Check replacement job status'}
              </button>
              {replanJobStatus?.status === 'Succeeded' && replanOutcome.outcome === 'CandidatesGenerated' && onOpenJobAlternatives && (
                <button onClick={() => onOpenJobAlternatives(replanJobId)} className="btn btn-primary">
                  Review and approve V2 candidates
                </button>
              )}
            </div>
            {replanJobStatus && (
              <div className="detail-grid">
                <div className="detail-item">
                  <span className="label">Status:</span>
                  <span className="value">{replanJobStatus.status}</span>
                </div>
                <div className="detail-item">
                  <span className="label">Outcome:</span>
                  <span className="value">{replanOutcome.outcome || 'Pending'}</span>
                </div>
                {(replanJobStatus.failureCode || replanJobStatus.errorMessage) && (
                  <div className="detail-item">
                    <span className="label">Failure:</span>
                    <span className="value">
                      {[replanJobStatus.failureCode, replanJobStatus.errorMessage].filter(Boolean).join(': ')}
                    </span>
                  </div>
                )}
              </div>
            )}
            {replanOutcome.violations.length > 0 && (
              <div className="alert alert-error" data-testid="replan-violations">
                Planning did not produce a valid replacement:
                <ul>
                  {replanOutcome.violations.map((violation, idx) => (
                    <li key={idx}>{violation.code}: {violation.message}</li>
                  ))}
                </ul>
              </div>
            )}
          </section>
        )}

        {/* PDF Export Error */}
        {pdfExportError && (
          <div className="alert alert-error" style={{ marginBottom: '12px' }}>
            {pdfExportError}
          </div>
        )}

        {/* Actions */}
        <section className="detail-section action-section">
          {isApproved && (
            <>
              <button
                onClick={() => handleServerExport('json')}
                disabled={busyAction !== null}
                className="btn btn-secondary btn-block"
              >
                {busyAction === 'export-json' ? '⏳ Exporting...' : '⬇ Download approved revision (JSON)'}
              </button>
              <button
                onClick={() => handleServerExport('pdf')}
                disabled={busyAction !== null}
                className="btn btn-secondary btn-block"
              >
                {busyAction === 'export-pdf' ? '⏳ Exporting...' : '⬇ Download approved revision (PDF)'}
              </button>
            </>
          )}
          <button
            onClick={handleExportPdf}
            disabled={pdfExportLoading}
            className="btn btn-secondary btn-block"
          >
            {pdfExportLoading ? '⏳ Generating PDF...' : '📄 Save this page as PDF'}
          </button>
          {isApproved && (
            <button
              onClick={handleCancelDraft}
              disabled={busyAction !== null}
              className="btn btn-secondary btn-block"
            >
              {busyAction === 'cancel' ? '⏳ Cancelling...' : '✕ Cancel draft'}
            </button>
          )}
          <button
            onClick={() => onProceedToProgress(draftId)}
            className="btn btn-primary btn-block"
          >
            → View Progress
          </button>
          <button
            onClick={onBack}
            className="btn btn-secondary btn-block"
          >
            ← Back
          </button>
        </section>
      </div>
    </div>
  );
};
