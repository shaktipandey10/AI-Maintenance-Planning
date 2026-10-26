import React, { useState } from 'react';
import { apiClient } from '../services/apiClient';
import type { CreatePlanningJobRequest, PlanningJobStatusResponse } from '../types/api';
import '../styles/PlanningRequestScreen.css';

interface PlanningRequestScreenProps {
  onJobCreated: (jobId: string) => void;
}

export const PlanningRequestScreen: React.FC<PlanningRequestScreenProps> = ({
  onJobCreated,
}) => {
  // Create Job section
  const [requestText, setRequestText] = useState('');
  const [assetIds, setAssetIds] = useState<string[]>(['']);
  const [windowId, setWindowId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [createdJobId, setCreatedJobId] = useState<string | null>(null);

  // Get Status section
  const [statusJobId, setStatusJobId] = useState('');
  const [statusLoading, setStatusLoading] = useState(false);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [jobStatus, setJobStatus] = useState<PlanningJobStatusResponse | null>(null);

  // Clarification section
  const [clarificationWindowId, setClarificationWindowId] = useState('');
  const [clarificationAdditionalContext, setClarificationAdditionalContext] = useState('');
  const [clarificationLoading, setClarificationLoading] = useState(false);
  const [clarificationError, setClarificationError] = useState<string | null>(null);
  const [clarificationSuccess, setClarificationSuccess] = useState<string | null>(null);

  const handleAssetIdChange = (index: number, value: string) => {
    const newAssetIds = [...assetIds];
    newAssetIds[index] = value;
    setAssetIds(newAssetIds);
  };

  const addAssetId = () => {
    setAssetIds([...assetIds, '']);
  };

  const removeAssetId = (index: number) => {
    if (assetIds.length > 1) {
      setAssetIds(assetIds.filter((_, i) => i !== index));
    }
  };

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setError(null);
    setSuccessMessage(null);

    const validAssetIds = assetIds.filter((id) => id.trim());
    if (!requestText.trim()) {
      setError('Request text is required');
      return;
    }

    if (validAssetIds.length === 0) {
      setError('At least one asset ID is required');
      return;
    }

    setLoading(true);
    try {
      const request: CreatePlanningJobRequest = {
        requestText: requestText.trim(),
        assetIds: validAssetIds,
        windowId: windowId.trim() || undefined,
      };

      const response = await apiClient.createPlanningJob(request);
      setSuccessMessage(`Planning job created successfully! Job ID: ${response.jobId}`);
      setCreatedJobId(response.jobId);
      setStatusJobId(response.jobId);
      setRequestText('');
      setAssetIds(['']);
      setWindowId('');
      onJobCreated(response.jobId);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create planning job');
    } finally {
      setLoading(false);
    }
  };

  const handleGetStatus = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setStatusError(null);
    setJobStatus(null);

    const jobIdTrim = statusJobId.trim();
    if (!jobIdTrim) {
      setStatusError('Job ID is required');
      return;
    }

    setStatusLoading(true);
    try {
      const status = await apiClient.getPlanningJobStatus(jobIdTrim);
      setJobStatus(status);
      setClarificationError(null);
      setClarificationSuccess(null);
      setClarificationWindowId('');
      setClarificationAdditionalContext('');
    } catch (err) {
      setStatusError(err instanceof Error ? err.message : 'Failed to get job status');
      setJobStatus(null);
    } finally {
      setStatusLoading(false);
    }
  };

  const parseClarificationDetails = (resultPayload: any) => {
    try {
      if (!resultPayload) {
        console.log('[CLARIFICATION] Empty payload');
        return { outcome: '', questions: [], assumptions: [], interpretation: undefined };
      }

      let result = resultPayload;

      if (typeof resultPayload === 'string') {
        console.log('[CLARIFICATION] Parsing JSON string');
        result = JSON.parse(resultPayload);
      }

      const outcome = result?.outcome;
      console.log('[CLARIFICATION] Parsed outcome:', outcome);
      console.log('[CLARIFICATION] Will render section?', outcome === 'ClarificationRequired');

      return {
        outcome: outcome || '',
        questions: result?.result?.questions || [],
        assumptions: result?.result?.assumptions || [],
        interpretation: result?.result?.interpretation,
      };
    } catch (error) {
      console.error('[CLARIFICATION] Parse error:', error);
      return { outcome: '', questions: [], assumptions: [], interpretation: undefined };
    }
  };

  const handleSubmitClarification = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setClarificationError(null);
    setClarificationSuccess(null);

    if (!jobStatus) {
      setClarificationError('No job selected');
      return;
    }

    if (!clarificationWindowId.trim()) {
      setClarificationError('Maintenance Window ID is required');
      return;
    }

    setClarificationLoading(true);
    try {
      const updated = await apiClient.submitClarification(
        jobStatus.jobId,
        clarificationWindowId.trim(),
        clarificationAdditionalContext.trim() || undefined
      );
      setClarificationSuccess('Clarification submitted successfully. Job has been re-queued for planning.');
      setJobStatus(updated);
      setClarificationWindowId('');
      setClarificationAdditionalContext('');
    } catch (err) {
      setClarificationError(err instanceof Error ? err.message : 'Failed to submit clarification');
    } finally {
      setClarificationLoading(false);
    }
  };

  return (
    <div className="planning-request-screen">
      <div className="container">
        {/* Create Planning Request Section */}
        <section className="form-section">
          <h1>Create Planning Request</h1>
          <p className="subtitle">
            Submit a maintenance request with asset details and optional window information
          </p>

          {error && <div className="error-message">{error}</div>}
          {successMessage && <div className="success-message">{successMessage}</div>}

          <form onSubmit={handleSubmit} className="planning-form">
          <div className="form-group">
            <label htmlFor="requestText">Maintenance Request *</label>
            <textarea
              id="requestText"
              value={requestText}
              onChange={(e) => setRequestText(e.target.value)}
              placeholder="Describe the maintenance work needed..."
              disabled={loading}
              rows={6}
            />
            <p className="field-help">
              Provide a clear description of the maintenance work required
            </p>
          </div>

          <div className="form-group">
            <label>Asset IDs *</label>
            <div className="asset-list">
              {assetIds.map((assetId, index) => (
                <div key={index} className="asset-input-group">
                  <input
                    type="text"
                    value={assetId}
                    onChange={(e) => handleAssetIdChange(index, e.target.value)}
                    placeholder={`Asset ID ${index + 1}`}
                    disabled={loading}
                  />
                  {assetIds.length > 1 && (
                    <button
                      type="button"
                      onClick={() => removeAssetId(index)}
                      className="btn-remove"
                      disabled={loading}
                    >
                      Remove
                    </button>
                  )}
                </div>
              ))}
            </div>
            <button
              type="button"
              onClick={addAssetId}
              className="btn-add-asset"
              disabled={loading}
            >
              + Add Another Asset
            </button>
            <p className="field-help">
              Specify at least one asset ID. You can add multiple assets.
            </p>
          </div>

          <div className="form-group">
            <label htmlFor="windowId">Maintenance Window (Optional)</label>
            <input
              id="windowId"
              type="text"
              value={windowId}
              onChange={(e) => setWindowId(e.target.value)}
              placeholder="e.g., maintenance-window-1"
              disabled={loading}
            />
            <p className="field-help">
              Optionally specify a maintenance window ID for scheduling constraints
            </p>
          </div>

          <div className="form-actions">
            <button type="submit" className="btn-primary" disabled={loading}>
              {loading ? 'Creating...' : 'Create Planning Job'}
            </button>
            {createdJobId && (
              <button
                type="button"
                onClick={() => {
                  setStatusJobId(createdJobId);
                  handleGetStatus({ preventDefault: () => {} } as React.FormEvent<HTMLFormElement>);
                }}
                className="btn-secondary"
              >
                Check Status
              </button>
            )}
          </div>
        </form>
        </section>

        {/* Get Planning Job Status Section */}
        <section className="form-section">
          <h2>Get Planning Job Status</h2>
          <p className="subtitle">
            Check the status of an existing planning job using its ID
          </p>

          {statusError && <div className="error-message">{statusError}</div>}

          <form onSubmit={handleGetStatus} className="planning-form">
            <div className="form-group">
              <label htmlFor="statusJobId">Planning Job ID *</label>
              <input
                id="statusJobId"
                type="text"
                value={statusJobId}
                onChange={(e) => setStatusJobId(e.target.value)}
                placeholder="e.g., 9457ad13-5aae-45f5-84fb-ea75a6e8c9cc"
                disabled={statusLoading}
              />
              <p className="field-help">
                Enter a valid GUID of a planning job to check its status
              </p>
            </div>

            <div className="form-actions">
              <button type="submit" className="btn-primary" disabled={statusLoading}>
                {statusLoading ? 'Loading...' : 'Get Status'}
              </button>
            </div>
          </form>

          {jobStatus && (() => {
            const hasResultPayload = !!jobStatus.resultPayload;
            let outcomeValue = 'N/A';
            let conditionIsTrue = false;

            if (hasResultPayload) {
              try {
                let parsed: any = jobStatus.resultPayload;
                if (typeof jobStatus.resultPayload === 'string') {
                  parsed = JSON.parse(jobStatus.resultPayload);
                }
                outcomeValue = parsed?.outcome || 'N/A';
                conditionIsTrue = outcomeValue === 'ClarificationRequired';
              } catch (e) {
                outcomeValue = 'ERROR';
              }
            }

            return (
              <>
                <div style={{
                  background: '#fff3cd',
                  border: '2px solid #ffc107',
                  padding: '12px',
                  marginBottom: '16px',
                  borderRadius: '4px',
                  fontFamily: 'monospace',
                  fontSize: '12px'
                }}>
                  <strong>🔍 DIAGNOSTIC BANNER</strong>
                  <div>✓ Component mounted</div>
                  <div>jobStatus: {jobStatus ? 'YES' : 'NO'}</div>
                  <div>resultPayload: {hasResultPayload ? 'YES' : 'NO'}</div>
                  <div>Parsed outcome: <strong>{outcomeValue}</strong></div>
                  <div>Clarification condition true?: <strong style={{color: conditionIsTrue ? 'green' : 'red'}}>{String(conditionIsTrue)}</strong></div>
                </div>
                <div className="status-display">
                  <h3>Job Status Details</h3>
              <div className="status-grid">
                <div className="status-item">
                  <span className="label">Job ID:</span>
                  <span className="value">{jobStatus.jobId}</span>
                </div>
                <div className="status-item">
                  <span className="label">Worker Status:</span>
                  <span className={`value status-${jobStatus.status.toLowerCase()}`}>
                    {jobStatus.status}
                  </span>
                </div>
                <div className="status-item">
                  <span className="label">Current Step:</span>
                  <span className="value">{jobStatus.currentStep}</span>
                </div>
                {jobStatus.resultPayload && (() => {
                  const outcome = (() => {
                    try {
                      const parsed = typeof jobStatus.resultPayload === 'string'
                        ? JSON.parse(jobStatus.resultPayload)
                        : jobStatus.resultPayload;
                      return parsed?.outcome || 'Unknown';
                    } catch {
                      return 'Unknown';
                    }
                  })();
                  return (
                    <div className="status-item">
                      <span className="label">Planning Outcome:</span>
                      <span className={`value status-${outcome.toLowerCase()}`}>
                        {outcome}
                      </span>
                    </div>
                  );
                })()}
                <div className="status-item">
                  <span className="label">Attempt Count:</span>
                  <span className="value">
                    {jobStatus.attemptCount} / {jobStatus.maxAttempts}
                  </span>
                </div>
                <div className="status-item">
                  <span className="label">Created At:</span>
                  <span className="value">
                    {new Date(jobStatus.createdAtUtc).toLocaleString()}
                  </span>
                </div>
                {jobStatus.startedAtUtc && (
                  <div className="status-item">
                    <span className="label">Started At:</span>
                    <span className="value">
                      {new Date(jobStatus.startedAtUtc).toLocaleString()}
                    </span>
                  </div>
                )}
                {jobStatus.completedAtUtc && (
                  <div className="status-item">
                    <span className="label">Completed At:</span>
                    <span className="value">
                      {new Date(jobStatus.completedAtUtc).toLocaleString()}
                    </span>
                  </div>
                )}
                {jobStatus.failureCode && (
                  <div className="status-item">
                    <span className="label">Failure Code:</span>
                    <span className="value">{jobStatus.failureCode}</span>
                  </div>
                )}
                {jobStatus.errorMessage && (
                  <div className="status-item full-width">
                    <span className="label">Error Message:</span>
                    <span className="value error-text">{jobStatus.errorMessage}</span>
                  </div>
                )}
                {jobStatus.clarificationPayload && (
                  <div className="status-item full-width">
                    <span className="label">Previous Clarification Required:</span>
                    <span className="value code-block">
                      {typeof jobStatus.clarificationPayload === 'string'
                        ? jobStatus.clarificationPayload
                        : JSON.stringify(jobStatus.clarificationPayload, null, 2)}
                    </span>
                  </div>
                )}
                {jobStatus.resultPayload && (
                  <div className="status-item full-width">
                    <span className="label">Current Result Payload:</span>
                    <span className="value code-block">
                      {typeof jobStatus.resultPayload === 'string'
                        ? jobStatus.resultPayload
                        : JSON.stringify(jobStatus.resultPayload, null, 2)}
                    </span>
                  </div>
                )}
              </div>
            </div>
              </>
            );
          })()}

          {jobStatus && jobStatus.resultPayload && (() => {
            const clarDetails = parseClarificationDetails(jobStatus.resultPayload);
            return clarDetails.outcome === 'ClarificationRequired' ? (
              <div className="clarification-section">
                <h3>Clarification Required</h3>
                <p className="clarification-subtitle">
                  The planning process requires additional information to proceed.
                </p>

                {clarDetails.interpretation && (
                  <div className="interpretation-box">
                    <h4>Planning Summary</h4>
                    <div className="interpretation-grid">
                      <div className="interpretation-item">
                        <span className="label">Maintenance Intent:</span>
                        <span className="value">
                          {clarDetails.interpretation.maintenanceIntent}
                        </span>
                      </div>
                      <div className="interpretation-item">
                        <span className="label">Scope:</span>
                        <span className="value">{clarDetails.interpretation.scope}</span>
                      </div>
                      <div className="interpretation-item">
                        <span className="label">Proposed Assets:</span>
                        <span className="value">
                          {clarDetails.interpretation.proposedAssetIds.join(', ')}
                        </span>
                      </div>
                      <div className="interpretation-item">
                        <span className="label">Proposed Tasks:</span>
                        <span className="value">
                          {clarDetails.interpretation.proposedTaskIds.join(', ')}
                        </span>
                      </div>
                    </div>
                  </div>
                )}

                {clarDetails.questions.length > 0 && (
                  <div className="questions-box">
                    <h4>Questions to Answer</h4>
                    <ul className="questions-list">
                      {clarDetails.questions.map((question: string, idx: number) => (
                        <li key={idx} className="question-item">{question}</li>
                      ))}
                    </ul>
                  </div>
                )}

                {clarificationError && (
                  <div className="error-message">{clarificationError}</div>
                )}
                {clarificationSuccess && (
                  <div className="success-message">{clarificationSuccess}</div>
                )}

                <form onSubmit={handleSubmitClarification} className="clarification-form">
                  <div className="form-group">
                    <label htmlFor="clarWindowId">Maintenance Window ID *</label>
                    <input
                      id="clarWindowId"
                      type="text"
                      value={clarificationWindowId}
                      onChange={(e) => setClarificationWindowId(e.target.value)}
                      placeholder="e.g., window-2024-Q1"
                      disabled={clarificationLoading}
                      required
                    />
                    <p className="field-help">
                      Specify a maintenance window ID to resolve scheduling constraints
                    </p>
                  </div>

                  <div className="form-group">
                    <label htmlFor="clarContext">Additional Context (Optional)</label>
                    <textarea
                      id="clarContext"
                      value={clarificationAdditionalContext}
                      onChange={(e) => setClarificationAdditionalContext(e.target.value)}
                      placeholder="Any additional information to help with planning..."
                      disabled={clarificationLoading}
                      rows={4}
                    />
                    <p className="field-help">
                      Provide any additional details that may help resolve the clarification
                    </p>
                  </div>

                  <div className="form-actions">
                    <button type="submit" className="btn-primary" disabled={clarificationLoading}>
                      {clarificationLoading ? 'Submitting...' : 'Submit Clarification'}
                    </button>
                  </div>
                </form>
              </div>
            ) : null
          })()}
        </section>
      </div>
    </div>
  );
};
