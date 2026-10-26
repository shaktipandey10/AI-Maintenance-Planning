import React, { useState, useEffect } from 'react';
import { apiClient } from '../services/apiClient';
import type { CandidateResponse, ApprovalErrorResponse, InterpretationResponse } from '../types/api';
import '../styles/ApprovalScreen.css';

interface ApprovalScreenProps {
  jobId: string;
  candidate: CandidateResponse;
  candidateIndex: number;
  interpretation?: InterpretationResponse;
  onBack: () => void;
  onApprovalSuccess?: (draftId: string) => void;
  onViewDraftDetail?: (draftId: string) => void;
}

type ApprovalState = 'idle' | 'approving' | 'success' | 'error' | 'already_approved';

export const ApprovalScreen: React.FC<ApprovalScreenProps> = ({
  jobId,
  candidate,
  candidateIndex,
  interpretation,
  onBack,
  onApprovalSuccess,
  onViewDraftDetail,
}) => {
  const [approvalState, setApprovalState] = useState<ApprovalState>('idle');
  const [error, setError] = useState<{ message: string; details?: ApprovalErrorResponse } | null>(null);
  const [draftId, setDraftId] = useState<string | null>(null);
  const [exportLoading, setExportLoading] = useState<'json' | 'pdf' | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);

  useEffect(() => {
    const existingDraftId = localStorage.getItem(`draft_${jobId}`);
    if (existingDraftId) {
      setDraftId(existingDraftId);
      setApprovalState('already_approved');
    }
  }, [jobId]);

  const handleApprove = async () => {
    setApprovalState('approving');
    setError(null);
    setExportError(null);

    try {
      const result = await apiClient.approveDraft(jobId);
      console.log('[Approval] Approval succeeded, draftId:', result.draftId);
      setDraftId(result.draftId);
      setApprovalState('success');

      // Store draft ID and candidate index in localStorage for AlternativesScreen
      try {
        localStorage.setItem(`draft_${jobId}`, result.draftId);
        localStorage.setItem(`approved_candidate_${jobId}`, candidateIndex.toString());
      } catch (e) {
        // Silently fail if localStorage unavailable
      }

      // Call the callback if provided (to navigate to draft detail)
      if (onApprovalSuccess) {
        setTimeout(() => {
          onApprovalSuccess(result.draftId);
        }, 500);
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Unknown error';
      const conflictData = (err as any).conflictData as ApprovalErrorResponse | undefined;
      setError({
        message: errorMessage,
        details: conflictData,
      });
      setApprovalState('error');
    }
  };

  const handleExportJson = async () => {
    console.log('[Export JSON] Button clicked, draftId:', draftId);
    if (!draftId) {
      console.log('[Export JSON] draftId is null/undefined, returning early');
      return;
    }
    setExportError(null);
    setExportLoading('json');
    try {
      console.log('[Export JSON] Starting export for draftId:', draftId);
      await apiClient.exportDraftAsJson(draftId);
      console.log('[Export JSON] Export successful');
    } catch (err) {
      console.error('[Export JSON] Export failed:', err);
      setExportError(err instanceof Error ? err.message : 'Failed to export JSON');
    } finally {
      setExportLoading(null);
    }
  };

  const handleExportPdf = async () => {
    console.log('[Export PDF] Button clicked, draftId:', draftId);
    if (!draftId) {
      console.log('[Export PDF] draftId is null/undefined, returning early');
      return;
    }
    setExportError(null);
    setExportLoading('pdf');
    try {
      console.log('[Export PDF] Starting export for draftId:', draftId);
      await apiClient.exportDraftAsPdf(draftId);
      console.log('[Export PDF] Export successful');
    } catch (err) {
      console.error('[Export PDF] Export failed:', err);
      setExportError(err instanceof Error ? err.message : 'Failed to export PDF');
    } finally {
      setExportLoading(null);
    }
  };

  const durationHours = Math.floor(candidate.completionTimeMinutes / 60);
  const durationMinutes = candidate.completionTimeMinutes % 60;
  const durationText = durationHours > 0
    ? `${durationHours}h ${durationMinutes}m`
    : `${durationMinutes}m`;

  return (
    <div className="approval-screen">
      <div className="screen-header">
        <button onClick={onBack} className="btn-back">
          ← Back
        </button>
        <h1>Review & Approve Plan</h1>
        <div className="header-spacer"></div>
      </div>

      <div className="screen-content">
        {/* Candidate Summary */}
        <section className="summary-section">
          <h2>Plan Summary</h2>
          <div className="summary-grid">
            <div className="summary-item">
              <span className="label">Duration:</span>
              <span className="value">{durationText}</span>
            </div>
            <div className="summary-item">
              <span className="label">Labour Cost:</span>
              <span className="value">
                {candidate.totalLabourCostAmount}
                {candidate.totalLabourCostCurrency === 'UNKNOWN'
                  ? ' (Currency unknown)'
                  : ` ${candidate.totalLabourCostCurrency}`}
              </span>
            </div>
            <div className="summary-item">
              <span className="label">Rationale:</span>
              <span className="value">{candidate.rationale}</span>
            </div>
          </div>
        </section>

        {/* Schedule Preview */}
        <section className="schedule-section">
          <h2>Task Schedule</h2>
          {candidate.taskPlacements.length > 0 ? (
            <div className="table-container">
              <table className="schedule-table">
                <thead>
                  <tr>
                    <th>Task ID</th>
                    <th>Start Time</th>
                    <th>End Time</th>
                    <th>Duration</th>
                    <th>Technicians</th>
                  </tr>
                </thead>
                <tbody>
                  {candidate.taskPlacements.map((placement, idx) => {
                    const start = new Date(placement.start);
                    const end = new Date(placement.end);
                    const durationMinutes = Math.round(
                      (end.getTime() - start.getTime()) / 60000
                    );

                    return (
                      <tr key={idx}>
                        <td className="task-id">{placement.taskId}</td>
                        <td className="time">{start.toLocaleString()}</td>
                        <td className="time">{end.toLocaleString()}</td>
                        <td className="duration">{durationMinutes} min</td>
                        <td className="technicians">
                          {placement.technicianIds.length > 0
                            ? placement.technicianIds.join(', ')
                            : '—'}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="empty-message">No task placements for this plan.</p>
          )}
        </section>

        {/* Interpretation */}
        {interpretation && (
          <section className="interpretation-section">
            <h2>Planning Interpretation</h2>
            <div className="interpretation-grid">
              <div className="interpretation-item">
                <span className="label">Maintenance Intent:</span>
                <span className="value">{interpretation.maintenanceIntent}</span>
              </div>
              <div className="interpretation-item">
                <span className="label">Scope:</span>
                <span className="value">{interpretation.scope}</span>
              </div>
            </div>
          </section>
        )}

        {/* Approval Status */}
        <section className="approval-section">
          <h2>Approval & Export</h2>

          {approvalState === 'idle' && (
            <div className="approval-form">
              <p className="instruction">
                Click below to approve this plan and reserve the required spare parts.
              </p>
              <button
                onClick={handleApprove}
                className="btn btn-primary btn-block"
              >
                ✓ Approve Plan
              </button>
            </div>
          )}

          {approvalState === 'already_approved' && draftId && (
            <div className="approval-success">
              <div className="status-banner already-approved">
                <span className="status-icon">ℹ️</span>
                <div className="status-text">
                  <h3>Plan Already Approved</h3>
                  <p>Already approved — draft exists for this job.</p>
                  <p>Draft ID: <code>{draftId}</code></p>
                </div>
              </div>

              <button
                disabled
                className="btn btn-primary btn-block"
                style={{ opacity: 0.5, cursor: 'not-allowed' }}
              >
                ✓ Approve Plan
              </button>

              <div className="export-buttons">
                <p className="export-label">Export your approved plan:</p>
                <button
                  onClick={handleExportJson}
                  disabled={exportLoading !== null}
                  className="btn btn-secondary btn-block"
                >
                  {exportLoading === 'json' ? '⏳ Exporting...' : '📥 Export as JSON'}
                </button>
                <button
                  onClick={handleExportPdf}
                  disabled={exportLoading !== null}
                  className="btn btn-secondary btn-block"
                >
                  {exportLoading === 'pdf' ? '⏳ Exporting...' : '📥 Export as PDF'}
                </button>
              </div>

              {exportError && (
                <div className="alert alert-error">{exportError}</div>
              )}

              <button
                onClick={() => onViewDraftDetail?.(draftId)}
                className="btn btn-primary btn-block"
                style={{ marginTop: '16px' }}
              >
                → View Draft Detail
              </button>

              <button
                onClick={onBack}
                className="btn btn-primary btn-block"
                style={{ marginTop: '12px' }}
              >
                ← Back to Alternatives
              </button>
            </div>
          )}

          {approvalState === 'approving' && (
            <div className="status-message approving">
              <div className="spinner-small"></div>
              <p>Submitting approval and reserving spare parts...</p>
            </div>
          )}

          {approvalState === 'success' && draftId && (
            <div className="approval-success">
              <div className="success-banner">
                <span className="success-icon">✓</span>
                <div className="success-text">
                  <h3>Plan Approved Successfully</h3>
                  <p>Draft ID: <code>{draftId}</code></p>
                </div>
              </div>

              <div className="export-buttons">
                <p className="export-label">Export your approved plan:</p>
                <button
                  onClick={handleExportJson}
                  disabled={exportLoading !== null}
                  className="btn btn-secondary btn-block"
                >
                  {exportLoading === 'json' ? '⏳ Exporting...' : '📥 Export as JSON'}
                </button>
                <button
                  onClick={handleExportPdf}
                  disabled={exportLoading !== null}
                  className="btn btn-secondary btn-block"
                >
                  {exportLoading === 'pdf' ? '⏳ Exporting...' : '📥 Export as PDF'}
                </button>
              </div>

              {exportError && (
                <div className="alert alert-error">{exportError}</div>
              )}

              {draftId && (
                <button
                  onClick={() => onViewDraftDetail?.(draftId)}
                  className="btn btn-primary btn-block"
                  style={{ marginTop: '16px' }}
                >
                  → View Draft Detail
                </button>
              )}

              <button
                onClick={onBack}
                className="btn btn-primary btn-block"
                style={{ marginTop: '12px' }}
              >
                ← Back to Alternatives
              </button>
            </div>
          )}

          {approvalState === 'error' && error && (
            <div className="approval-error">
              <div className="error-banner">
                <span className="error-icon">⚠️</span>
                <div className="error-text">
                  <h3>Approval Failed</h3>
                  <p>{error.message}</p>
                </div>
              </div>

              {error.details && (
                <div className="error-details">
                  <div className="detail-row">
                    <span className="detail-label">Resource Type:</span>
                    <span className="detail-value">{error.details.resourceType}</span>
                  </div>
                  <div className="detail-row">
                    <span className="detail-label">Reason:</span>
                    <span className="detail-value">{error.details.reason}</span>
                  </div>
                  {error.details.resourceId && (
                    <div className="detail-row">
                      <span className="detail-label">Resource ID:</span>
                      <span className="detail-value">{error.details.resourceId}</span>
                    </div>
                  )}
                  {error.details.message && (
                    <div className="detail-row">
                      <span className="detail-label">Message:</span>
                      <span className="detail-value">{error.details.message}</span>
                    </div>
                  )}
                </div>
              )}

              <div className="error-actions">
                <button
                  onClick={() => {
                    setApprovalState('idle');
                    setError(null);
                  }}
                  className="btn btn-primary btn-block"
                >
                  ↻ Try Again
                </button>
                <button
                  onClick={onBack}
                  className="btn btn-secondary btn-block"
                >
                  ← Back to Alternatives
                </button>
              </div>
            </div>
          )}
        </section>
      </div>
    </div>
  );
};
