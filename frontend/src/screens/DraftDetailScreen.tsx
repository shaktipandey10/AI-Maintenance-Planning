import React, { useState, useEffect, useRef } from 'react';
import { apiClient } from '../services/apiClient';
import type { DraftDetailResponse } from '../types/api';
import html2pdf from 'html2pdf.js';
import '../styles/DraftDetailScreen.css';

interface DraftDetailScreenProps {
  draftId: string;
  onBack: () => void;
  onProceedToProgress: (draftId: string) => void;
}

type LoadingState = 'loading' | 'success' | 'error';

export const DraftDetailScreen: React.FC<DraftDetailScreenProps> = ({
  draftId,
  onBack,
  onProceedToProgress,
}) => {
  const [draft, setDraft] = useState<DraftDetailResponse | null>(null);
  const [loadingState, setLoadingState] = useState<LoadingState>('loading');
  const [error, setError] = useState<string | null>(null);
  const [pdfExportLoading, setPdfExportLoading] = useState(false);
  const [pdfExportError, setPdfExportError] = useState<string | null>(null);
  const contentRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const fetchDraftDetail = async () => {
      setLoadingState('loading');
      setError(null);
      try {
        const response = await apiClient.getDraftDetail(draftId);
        setDraft(response);
        setLoadingState('success');
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load draft details');
        setLoadingState('error');
      }
    };

    fetchDraftDetail();
  }, [draftId]);

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
        {/* Draft Header */}
        <section className="detail-section">
          <h2>Draft Information</h2>
          <div className="detail-grid">
            <div className="detail-item">
              <span className="label">Draft ID:</span>
              <code className="value">{draft.draftId}</code>
            </div>
            <div className="detail-item">
              <span className="label">Status:</span>
              <span className={`status-badge status-${draft.status.toLowerCase()}`}>
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

            {/* Task Placements */}
            {draft.selectedCandidate.taskPlacements.length > 0 && (
              <div className="tasks-subsection">
                <h3>Scheduled Tasks</h3>
                <div className="table-container">
                  <table className="tasks-table">
                    <thead>
                      <tr>
                        <th>Task</th>
                        <th>Start</th>
                        <th>End</th>
                        <th>Technicians</th>
                      </tr>
                    </thead>
                    <tbody>
                      {draft.selectedCandidate.taskPlacements.map((task, idx) => {
                        const start = new Date(task.start);
                        const end = new Date(task.end);
                        return (
                          <tr key={idx}>
                            <td>{task.taskId}</td>
                            <td>{start.toLocaleString()}</td>
                            <td>{end.toLocaleString()}</td>
                            <td>{task.technicianIds.join(', ') || '—'}</td>
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
                    <span className={`reservation-status status-${reservation.status.toLowerCase()}`}>
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

        {/* PDF Export Error */}
        {pdfExportError && (
          <div className="alert alert-error" style={{ marginBottom: '12px' }}>
            {pdfExportError}
          </div>
        )}

        {/* Navigation */}
        <section className="detail-section action-section">
          <button
            onClick={handleExportPdf}
            disabled={pdfExportLoading}
            className="btn btn-secondary btn-block"
          >
            {pdfExportLoading ? '⏳ Generating PDF...' : '📄 Export as PDF'}
          </button>
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
