import React, { useState, useEffect } from 'react';
import { apiClient } from '../services/apiClient';
import type { PlanningJobCandidatesResponse, CandidateResponse, InterpretationResponse } from '../types/api';
import '../styles/AlternativesScreen.css';

interface AlternativesScreenProps {
  jobId: string;
  onBack: () => void;
  onProceedToApproval?: (candidate: CandidateResponse, interpretation?: InterpretationResponse, candidateIndex?: number) => void;
  onOpenDraftDetail?: (draftId: string) => void;
}

export const AlternativesScreen: React.FC<AlternativesScreenProps> = ({
  jobId,
  onBack,
  onProceedToApproval,
  onOpenDraftDetail,
}) => {
  const [candidatesData, setCandidatesData] = useState<PlanningJobCandidatesResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedCandidateIndex, setSelectedCandidateIndex] = useState<number>(0);
  const [existingDraftId, setExistingDraftId] = useState<string | null>(null);
  const [manualDraftId, setManualDraftId] = useState<string>('');

  useEffect(() => {
    const fetchCandidates = async () => {
      setLoading(true);
      setError(null);
      try {
        const data = await apiClient.getCandidates(jobId);
        setCandidatesData(data);
        setSelectedCandidateIndex(0);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to fetch candidates');
      } finally {
        setLoading(false);
      }
    };

    fetchCandidates();
  }, [jobId]);

  useEffect(() => {
    // Check if there's an existing approved draft for this job
    try {
      const draftId = localStorage.getItem(`draft_${jobId}`);
      setExistingDraftId(draftId);
    } catch (e) {
      // Silently fail if localStorage unavailable
    }
  }, [jobId]);

  if (loading) {
    return (
      <div className="alternatives-screen">
        <div className="loading-container">
          <div className="spinner"></div>
          <p>Loading candidates...</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="alternatives-screen">
        <div className="error-container">
          <h2>Error Loading Candidates</h2>
          <p className="error-message">{error}</p>
          <button onClick={onBack} className="btn-back">
            ← Back to Job Status
          </button>
        </div>
      </div>
    );
  }

  if (!candidatesData) {
    return (
      <div className="alternatives-screen">
        <div className="empty-container">
          <p>No data available</p>
          <button onClick={onBack} className="btn-back">
            ← Back to Job Status
          </button>
        </div>
      </div>
    );
  }

  // Handle non-CandidatesGenerated outcomes
  if (candidatesData.outcome !== 'CandidatesGenerated') {
    return (
      <div className="alternatives-screen">
        <div className="outcome-message">
          <h2>Planning Outcome: {candidatesData.outcome}</h2>
          <p>
            No candidates are available. The planning process resulted in{' '}
            <strong>{candidatesData.outcome}</strong>.
          </p>
          <button onClick={onBack} className="btn-back">
            ← Back to Job Status
          </button>
        </div>
      </div>
    );
  }

  const candidates = candidatesData.candidates;
  if (candidates.length === 0) {
    return (
      <div className="alternatives-screen">
        <div className="empty-container">
          <p>No candidates available for this planning job.</p>
          <button onClick={onBack} className="btn-back">
            ← Back to Job Status
          </button>
        </div>
      </div>
    );
  }

  const selectedCandidate = candidates[selectedCandidateIndex];

  return (
    <div className="alternatives-screen">
      <div className="screen-header">
        <button onClick={onBack} className="btn-back">
          ← Back
        </button>
        <h1>Candidate Alternatives</h1>
        <div className="header-spacer"></div>
      </div>

      <div className="screen-content">
        {/* View Existing Draft Section */}
        <section className="existing-draft-section">
          <h2>View Existing Approved Draft</h2>
          <div className="draft-input-group">
            <input
              type="text"
              placeholder="Paste existing draft ID (UUID format)"
              value={manualDraftId}
              onChange={(e) => setManualDraftId(e.target.value)}
              className="draft-id-input"
            />
            <button
              onClick={() => {
                if (manualDraftId.trim() && onOpenDraftDetail) {
                  onOpenDraftDetail(manualDraftId.trim());
                }
              }}
              disabled={!manualDraftId.trim()}
              className="btn-view-existing-draft"
            >
              → View Draft
            </button>
          </div>
          <p className="draft-input-help">
            If you have an existing approved draft from a previous session, paste its UUID here to view it.
            No approval or inventory changes will be made.
          </p>
        </section>

        {/* Candidates List */}
        <section className="candidates-list-section">
          <h2>Available Alternatives</h2>
          <div className="candidates-list">
            {candidates.map((candidate, index) => {
              const draftId = localStorage.getItem(`draft_${jobId}`);
              const approvedIndex = localStorage.getItem(`approved_candidate_${jobId}`);
              const isApproved = approvedIndex !== null && Number(approvedIndex) === index;
              return (
              // The card holds block content and the nested "View Draft" button, so it cannot be a
              // <button>; it gets button semantics, focusability and Enter/Space activation instead.
              <div
                key={index}
                className={`candidate-card ${
                  index === selectedCandidateIndex ? 'selected' : ''
                }`}
                role="button"
                tabIndex={0}
                aria-pressed={index === selectedCandidateIndex}
                onClick={() => setSelectedCandidateIndex(index)}
                onKeyDown={(e) => {
                  // Keys pressed on the nested "View Draft" button must not also select the card
                  if (e.target !== e.currentTarget) return;
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    setSelectedCandidateIndex(index);
                  }
                }}
              >
                <div className="candidate-header">
                  <span className="candidate-number">Alternative {index + 1}</span>
                  {candidate.isLowestCostCandidate && (
                    <span className="badge-lowest-cost">💰 Lowest Cost</span>
                  )}
                </div>
                <p className="candidate-rationale">{candidate.rationale}</p>
                <div className="candidate-metrics">
                  <div className="metric">
                    <span className="metric-label">Duration:</span>
                    <span className="metric-value">
                      {candidate.completionTimeMinutes} min
                    </span>
                  </div>
                  <div className="metric">
                    <span className="metric-label">Labour Cost:</span>
                    <span className="metric-value">
                      {candidate.totalLabourCostAmount}
                      {candidate.totalLabourCostCurrency === 'UNKNOWN'
                        ? ' (Currency unknown)'
                        : ` ${candidate.totalLabourCostCurrency}`}
                    </span>
                  </div>
                </div>
                {candidate.costComparabilityWarning && (
                  <div className="warning-box">
                    ⚠️ {candidate.costComparabilityWarning}
                  </div>
                )}
                {candidate.deferralReasons.length > 0 && (
                  <div className="deferral-box">
                    <strong>Deferred Tasks:</strong>
                    <ul>
                      {candidate.deferralReasons.map((reason, idx) => (
                        <li key={idx}>
                          {reason.taskId}: {reason.reason}
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    if (isApproved && draftId && onOpenDraftDetail) {
                      onOpenDraftDetail(draftId);
                    }
                  }}
                  disabled={!draftId || !isApproved}
                  className="btn-view-draft"
                  title={draftId && isApproved ? 'View this approved draft' : 'This candidate has not been approved yet'}
                >
                  📋 View Draft
                </button>
              </div>
            );
            })}
          </div>
        </section>

        {/* Selected Candidate Details */}
        <section className="candidate-details-section">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
            <h2 style={{ margin: 0 }}>Alternative {selectedCandidateIndex + 1} - Details</h2>
            <div style={{ display: 'flex', gap: '8px' }}>
              {existingDraftId && onOpenDraftDetail && (
                <button
                  onClick={() => onOpenDraftDetail(existingDraftId)}
                  className="btn-draft-detail"
                  title="View existing approved draft for this job"
                >
                  📋 Draft Detail
                </button>
              )}
              {onProceedToApproval && (
                <button
                  onClick={() => onProceedToApproval(selectedCandidate, candidatesData.interpretation, selectedCandidateIndex)}
                  className="btn-proceed-approval"
                >
                  → Proceed to Approval
                </button>
              )}
            </div>
          </div>

          {/* Schedule */}
          <div className="details-subsection">
            <h3>Schedule</h3>
            {selectedCandidate.taskPlacements.length > 0 ? (
              <div className="table-container">
                <table className="schedule-table">
                  <thead>
                    <tr>
                      <th>Task ID</th>
                      <th>Start Time</th>
                      <th>End Time</th>
                      <th>Duration</th>
                      <th>Assigned Technicians</th>
                    </tr>
                  </thead>
                  <tbody>
                    {selectedCandidate.taskPlacements.map((placement, idx) => {
                      const start = new Date(placement.start);
                      const end = new Date(placement.end);
                      const durationMinutes = Math.round(
                        (end.getTime() - start.getTime()) / 60000
                      );

                      return (
                        <tr key={idx}>
                          <td className="task-id">{placement.taskId}</td>
                          <td className="time">
                            {start.toLocaleString()}
                          </td>
                          <td className="time">
                            {end.toLocaleString()}
                          </td>
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
              <p className="empty-message">No task placements for this candidate.</p>
            )}
          </div>

          {/* Evidence */}
          {selectedCandidate.evidenceReferences.length > 0 && (
            <div className="details-subsection">
              <h3>Evidence References</h3>
              <div className="evidence-list">
                {selectedCandidate.evidenceReferences.map((ref, idx) => (
                  <div key={idx} className="evidence-item">
                    <div className="evidence-header">
                      <span className="evidence-source">{ref}</span>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Interpretation */}
          {candidatesData.interpretation && (
            <div className="details-subsection">
              <h3>Planning Interpretation</h3>
              <div className="interpretation-grid">
                <div className="interpretation-item">
                  <span className="label">Maintenance Intent:</span>
                  <span className="value">
                    {candidatesData.interpretation.maintenanceIntent}
                  </span>
                </div>
                <div className="interpretation-item">
                  <span className="label">Scope:</span>
                  <span className="value">
                    {candidatesData.interpretation.scope}
                  </span>
                </div>
                <div className="interpretation-item">
                  <span className="label">Proposed Assets:</span>
                  <span className="value">
                    {candidatesData.interpretation.proposedAssetIds.join(', ')}
                  </span>
                </div>
                <div className="interpretation-item">
                  <span className="label">Proposed Tasks:</span>
                  <span className="value">
                    {candidatesData.interpretation.proposedTaskIds.join(', ')}
                  </span>
                </div>
              </div>
            </div>
          )}

          {/* Additional Info */}
          <div className="details-subsection">
            <h3>Additional Information</h3>
            <div className="info-grid">
              <div className="info-item">
                <span className="label">Requested Tasks:</span>
                <span className="value">
                  {selectedCandidate.requestedTaskIds.length > 0
                    ? selectedCandidate.requestedTaskIds.join(', ')
                    : '—'}
                </span>
              </div>
              <div className="info-item">
                <span className="label">Dependency Tasks:</span>
                <span className="value">
                  {selectedCandidate.dependencyTaskIds.length > 0
                    ? selectedCandidate.dependencyTaskIds.join(', ')
                    : '—'}
                </span>
              </div>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
};
