import { useCallback, useEffect, useState } from 'react'
import { AlternativesScreen } from './screens/AlternativesScreen'
import { ApprovalScreen } from './screens/ApprovalScreen'
import { DraftDetailScreen } from './screens/DraftDetailScreen'
import { ProgressScreen } from './screens/ProgressScreen'
import { apiClient } from './services/apiClient'
import type { CandidateResponse, InterpretationResponse, SavedPlanResponse } from './types/api'
import './App.css'

interface JobStatus {
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

type AppScreen = 'dashboard' | 'alternatives' | 'approval' | 'draft-detail' | 'progress';

function App() {
  const [role, setRole] = useState('planner')
  const [currentScreen, setCurrentScreen] = useState<AppScreen>('dashboard')
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null)
  const [selectedCandidate, setSelectedCandidate] = useState<CandidateResponse | null>(null)
  const [selectedInterpretation, setSelectedInterpretation] = useState<InterpretationResponse | undefined>()
  const [selectedCandidateIndex, setSelectedCandidateIndex] = useState<number>(0)
  const [draftId, setDraftId] = useState<string | null>(null)

  // The selected role is recorded by the API as the performer of every business action (FR09)
  useEffect(() => {
    apiClient.setActorRole(role)
  }, [role])

  // Saved plans, so a plan can be reopened without its draft ID (FR09)
  const [savedPlans, setSavedPlans] = useState<SavedPlanResponse[] | null>(null)
  const [savedPlansError, setSavedPlansError] = useState<string | null>(null)
  const loadSavedPlans = useCallback(async () => {
    try {
      setSavedPlans(await apiClient.getSavedPlans())
      setSavedPlansError(null)
    } catch (err) {
      setSavedPlansError(err instanceof Error ? err.message : 'Failed to load saved plans')
    }
  }, [])

  // Reloaded on every screen change, so the list is current whenever the dashboard is shown again
  useEffect(() => {
    loadSavedPlans()
  }, [currentScreen, loadSavedPlans])

  // Create Job States
  const [requestText, setRequestText] = useState('')
  const [assetIds, setAssetIds] = useState<string[]>([''])
  const [windowId, setWindowId] = useState('')
  const [createLoading, setCreateLoading] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [createdJobId, setCreatedJobId] = useState<string | null>(null)

  // Status Check States
  const [statusJobId, setStatusJobId] = useState('')
  const [jobStatus, setJobStatus] = useState<JobStatus | null>(null)
  const [statusLoading, setStatusLoading] = useState(false)
  const [statusError, setStatusError] = useState<string | null>(null)

  // Asset management
  const handleAssetIdChange = (index: number, value: string) => {
    const newAssetIds = [...assetIds]
    newAssetIds[index] = value
    setAssetIds(newAssetIds)
  }

  const addAssetId = () => {
    setAssetIds([...assetIds, ''])
  }

  const removeAssetId = (index: number) => {
    if (assetIds.length > 1) {
      setAssetIds(assetIds.filter((_, i) => i !== index))
    }
  }

  // Create Planning Job
  const handleCreateJob = async (e: React.FormEvent) => {
    e.preventDefault()
    setCreateError(null)

    const validAssetIds = assetIds.filter((id) => id.trim())
    if (!requestText.trim()) {
      setCreateError('Maintenance request is required')
      return
    }

    if (validAssetIds.length === 0) {
      setCreateError('At least one asset ID is required')
      return
    }

    setCreateLoading(true)
    try {
      const apiBase = import.meta.env.VITE_API_BASE || 'http://localhost:5083'
      const response = await fetch(`${apiBase}/api/planning-jobs`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Idempotency-Key': crypto.randomUUID(),
        },
        body: JSON.stringify({
          requestText: requestText.trim(),
          assetIds: validAssetIds,
          windowId: windowId.trim() || undefined,
        }),
      })

      if (!response.ok) {
        const error = await response.json()
        throw new Error(error.detail || `Failed: ${response.statusText}`)
      }

      const data = await response.json()
      setCreatedJobId(data.jobId)
      setRequestText('')
      setAssetIds([''])
      setWindowId('')
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : 'Failed to create planning job')
    } finally {
      setCreateLoading(false)
    }
  }

  // Check Job Status
  const handleCheckStatus = async (e: React.FormEvent) => {
    e.preventDefault()

    if (!statusJobId.trim()) {
      setStatusError('Please enter a valid Job ID')
      return
    }

    setStatusError(null)
    setJobStatus(null)
    setStatusLoading(true)

    try {
      const apiBase = import.meta.env.VITE_API_BASE || 'http://localhost:5083'
      const response = await fetch(`${apiBase}/api/planning-jobs/${statusJobId}`, {
        method: 'GET',
      })

      if (response.status === 404) {
        setStatusError('Job not found. Please verify the Job ID.')
        setStatusLoading(false)
        return
      }

      if (!response.ok) {
        throw new Error(`Failed to fetch job status: ${response.statusText}`)
      }

      const data = await response.json()
      setJobStatus(data)
    } catch (err) {
      setStatusError(err instanceof Error ? err.message : 'Failed to fetch job status')
    } finally {
      setStatusLoading(false)
    }
  }

  const quickCheckStatus = (jobId: string) => {
    setStatusJobId(jobId)
    setTimeout(() => {
      const event = new Event('submit', { bubbles: true })
      const form = document.getElementById('status-form') as HTMLFormElement
      if (form) form.dispatchEvent(event)
    }, 0)
  }

  const parseClarificationDetails = (resultPayload: string | null) => {
    if (!resultPayload) {
      return { outcome: '', questions: [], interpretation: undefined }
    }
    try {
      let result: any = resultPayload
      if (typeof resultPayload === 'string') {
        result = JSON.parse(resultPayload)
      }
      return {
        outcome: result?.outcome || '',
        questions: result?.result?.questions || [],
        interpretation: result?.result?.interpretation,
      }
    } catch {
      return { outcome: '', questions: [], interpretation: undefined }
    }
  }

  // Open an existing draft by ID (draft management: details, progress, edits, exports, replan)
  const [openDraftId, setOpenDraftId] = useState('')

  const [clarWindowId, setClarWindowId] = useState('')
  const [clarLoading, setClarLoading] = useState(false)
  const [clarError, setClarError] = useState<string | null>(null)

  const handleViewAlternatives = (jobId: string) => {
    setSelectedJobId(jobId)
    setCurrentScreen('alternatives')
  }

  const handleBackToDashboard = () => {
    setCurrentScreen('dashboard')
    setSelectedJobId(null)
    setSelectedCandidate(null)
    setSelectedInterpretation(undefined)
  }

  const handleProceedToApproval = (candidate: CandidateResponse, interpretation?: InterpretationResponse, candidateIndex?: number) => {
    setSelectedCandidate(candidate)
    setSelectedInterpretation(interpretation)
    if (candidateIndex !== undefined) {
      setSelectedCandidateIndex(candidateIndex)
    }
    setCurrentScreen('approval')
  }

  const handleBackToAlternatives = () => {
    setCurrentScreen('alternatives')
    setSelectedCandidate(null)
    setSelectedInterpretation(undefined)
  }

  const handleBackFromDraftDetail = () => {
    setCurrentScreen('approval')
    setDraftId(null)
  }

  const handleOpenDraftDetail = (draftId: string) => {
    setDraftId(draftId)
    setCurrentScreen('draft-detail')
  }

  const handleProceedToProgress = (newDraftId: string) => {
    setDraftId(newDraftId)
    setCurrentScreen('progress')
  }

  const handleSubmitClarification = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!jobStatus || !clarWindowId.trim()) {
      setClarError('Window ID is required')
      return
    }

    setClarLoading(true)
    try {
      const apiBase = import.meta.env.VITE_API_BASE || 'http://localhost:5083'
      const response = await fetch(`${apiBase}/api/planning-jobs/${jobStatus.jobId}/clarify`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ windowId: clarWindowId.trim() }),
      })

      if (!response.ok) throw new Error('Failed to submit clarification')
      const data = await response.json()
      setJobStatus(data)
      setClarWindowId('')
    } catch (err) {
      setClarError(err instanceof Error ? err.message : 'Error')
    } finally {
      setClarLoading(false)
    }
  }

  if (currentScreen === 'alternatives' && selectedJobId) {
    return (
      <AlternativesScreen
        jobId={selectedJobId}
        onBack={handleBackToDashboard}
        onProceedToApproval={handleProceedToApproval}
        onOpenDraftDetail={handleOpenDraftDetail}
      />
    )
  }

  if (currentScreen === 'approval' && selectedJobId && selectedCandidate) {
    return (
      <ApprovalScreen
        jobId={selectedJobId}
        candidate={selectedCandidate}
        candidateIndex={selectedCandidateIndex}
        interpretation={selectedInterpretation}
        onBack={handleBackToAlternatives}
        onApprovalSuccess={(draftId) => {
          setDraftId(draftId)
        }}
        onViewDraftDetail={(draftId) => {
          setDraftId(draftId)
          setCurrentScreen('draft-detail')
        }}
      />
    )
  }

  if (currentScreen === 'draft-detail' && draftId) {
    return (
      <DraftDetailScreen
        draftId={draftId}
        onBack={handleBackFromDraftDetail}
        onProceedToProgress={handleProceedToProgress}
        onOpenJobAlternatives={handleViewAlternatives}
      />
    )
  }

  if (currentScreen === 'progress' && draftId) {
    return (
      <ProgressScreen
        draftId={draftId}
        onBack={() => {
          setCurrentScreen('draft-detail')
        }}
      />
    )
  }

  return (
    <div className="dashboard">
      {/* Header */}
      <div className="dashboard-header">
        <div className="header-content">
          <h1>AI Maintenance Planner</h1>
          <div className="role-selector">
            <label>Current Role:</label>
            <select value={role} onChange={(e) => setRole(e.target.value)} className="role-select">
              <option value="planner">👤 Planner</option>
              <option value="reviewer">👁 Reviewer</option>
              <option value="technician">🔧 Technician</option>
            </select>
          </div>
        </div>
      </div>

      {/* Main Grid */}
      <div className="dashboard-grid">

        {/* Tile 1: Create Planning Request */}
        <div className="tile">
          <div className="tile-header">
            <h2>Create Planning Request</h2>
            <span className="tile-icon">📝</span>
          </div>

          {createError && <div className="alert alert-error">{createError}</div>}

          <form onSubmit={handleCreateJob} className="tile-form">
            <div className="form-group">
              <label htmlFor="requestText">Maintenance Request *</label>
              <textarea
                id="requestText"
                value={requestText}
                onChange={(e) => setRequestText(e.target.value)}
                placeholder="What maintenance is needed?"
                disabled={createLoading}
                rows={3}
                className="textarea"
              />
            </div>

            <div className="form-group">
              <label>Asset IDs *</label>
              <div className="asset-list">
                {assetIds.map((assetId, index) => (
                  <div key={index} className="asset-row">
                    <input
                      type="text"
                      value={assetId}
                      onChange={(e) => handleAssetIdChange(index, e.target.value)}
                      placeholder={`Asset ${index + 1}`}
                      disabled={createLoading}
                      className="input input-sm"
                    />
                    {assetIds.length > 1 && (
                      <button
                        type="button"
                        onClick={() => removeAssetId(index)}
                        className="btn-icon"
                        disabled={createLoading}
                      >
                        ✕
                      </button>
                    )}
                  </div>
                ))}
              </div>
              <button
                type="button"
                onClick={addAssetId}
                className="btn btn-link"
                disabled={createLoading}
              >
                + Add Asset
              </button>
            </div>

            <div className="form-group">
              <label htmlFor="windowId">Maintenance Window (Optional)</label>
              <input
                id="windowId"
                type="text"
                value={windowId}
                onChange={(e) => setWindowId(e.target.value)}
                placeholder="e.g., window-1"
                disabled={createLoading}
                className="input"
              />
            </div>

            <button
              type="submit"
              className="btn btn-primary btn-block"
              disabled={createLoading}
            >
              {createLoading ? '⏳ Creating...' : '✓ Create Job'}
            </button>
          </form>
        </div>

        {/* Tile 2: Job Status */}
        <div className="tile">
          <div className="tile-header">
            <h2>Check Job Status</h2>
            <span className="tile-icon">🔍</span>
          </div>

          {statusError && <div className="alert alert-error">{statusError}</div>}

          <form onSubmit={handleCheckStatus} id="status-form" className="tile-form">
            <div className="form-group">
              <label htmlFor="statusJobId">Job ID *</label>
              <input
                id="statusJobId"
                type="text"
                value={statusJobId}
                onChange={(e) => {
                  setStatusJobId(e.target.value)
                  setStatusError(null)
                }}
                placeholder="Paste Job ID here"
                disabled={statusLoading}
                className="input"
              />
            </div>

            <button
              type="submit"
              className="btn btn-primary btn-block"
              disabled={statusLoading}
            >
              {statusLoading ? '⏳ Loading...' : '→ Get Status'}
            </button>
          </form>

          {jobStatus && (() => {
            const clarDetails = parseClarificationDetails(jobStatus.resultPayload)
            const isClarificationRequired = clarDetails.outcome === 'ClarificationRequired'
            const hasCandidates = clarDetails.outcome === 'CandidatesGenerated'

            return (
              <>
                {isClarificationRequired && (
                  <div style={{ background: '#fff3cd', border: '2px solid #ffc107', padding: '12px', marginBottom: '12px', borderRadius: '4px' }}>
                    <strong style={{ color: '#856404' }}>⚠️ CLARIFICATION REQUIRED</strong>
                  </div>
                )}
                {hasCandidates && (
                  <div style={{ background: '#d1fae5', border: '2px solid #10b981', padding: '12px', marginBottom: '12px', borderRadius: '4px' }}>
                    <strong style={{ color: '#065f46' }}>✓ CANDIDATES AVAILABLE</strong>
                  </div>
                )}
                <div className="status-compact">
                  <div className="status-row">
                    <span className="status-label">Status:</span>
                    <span className={`status-badge status-${jobStatus.status.toLowerCase()}`}>
                      {jobStatus.status}
                    </span>
                  </div>
                  <div className="status-row">
                    <span className="status-label">Step:</span>
                    <span className="status-value">{jobStatus.currentStep}</span>
                  </div>
                  <div className="status-row">
                    <span className="status-label">Outcome:</span>
                    <span className="status-value">{clarDetails.outcome || 'Pending'}</span>
                  </div>
                  <div className="status-row">
                    <span className="status-label">Attempts:</span>
                    <span className="status-value">{jobStatus.attemptCount}/{jobStatus.maxAttempts}</span>
                  </div>
                  <div className="status-row">
                    <span className="status-label">Created:</span>
                    <span className="status-value">{new Date(jobStatus.createdAtUtc).toLocaleString()}</span>
                  </div>
                  {jobStatus.errorMessage && (
                    <div className="status-row error">
                      <span className="status-label">Error:</span>
                      <span className="status-value">{jobStatus.errorMessage}</span>
                    </div>
                  )}
                </div>

                {isClarificationRequired && (
                  <div style={{ marginTop: '16px', padding: '16px', background: '#f8f9fa', borderRadius: '4px', border: '1px solid #dee2e6' }}>
                    <h3 style={{ marginTop: 0, color: '#495057' }}>Answer Clarification Questions</h3>
                    {clarDetails.questions.length > 0 && (
                      <div style={{ marginBottom: '12px' }}>
                        <strong>Questions:</strong>
                        <ul style={{ paddingLeft: '20px', margin: '8px 0' }}>
                          {clarDetails.questions.map((q: string, idx: number) => (
                            <li key={idx}>{q}</li>
                          ))}
                        </ul>
                      </div>
                    )}
                    <form onSubmit={handleSubmitClarification}>
                      <div className="form-group">
                        <label htmlFor="clarWindowId">Maintenance Window ID *</label>
                        <input
                          id="clarWindowId"
                          type="text"
                          value={clarWindowId}
                          onChange={(e) => setClarWindowId(e.target.value)}
                          placeholder="e.g., window-2024-Q1"
                          disabled={clarLoading}
                          className="input"
                          required
                        />
                      </div>
                      {clarError && <div style={{ color: '#dc3545', marginBottom: '8px' }}>{clarError}</div>}
                      <button type="submit" className="btn btn-primary btn-block" disabled={clarLoading}>
                        {clarLoading ? '⏳ Submitting...' : '✓ Submit Clarification'}
                      </button>
                    </form>
                  </div>
                )}

                {hasCandidates && (
                  <button
                    onClick={() => handleViewAlternatives(jobStatus.jobId)}
                    className="btn btn-success btn-block"
                    style={{ marginTop: '12px' }}
                  >
                    📊 View Alternatives & Schedule
                  </button>
                )}
              </>
            )
          })()}
        </div>

        {/* Tile: Open an existing draft */}
        <div className="tile">
          <div className="tile-header">
            <h2>Open Draft</h2>
            <span className="tile-icon">🗂</span>
          </div>

          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (openDraftId.trim()) {
                handleOpenDraftDetail(openDraftId.trim())
              }
            }}
            className="tile-form"
          >
            <div className="form-group">
              <label htmlFor="openDraftId">Draft ID *</label>
              <input
                id="openDraftId"
                type="text"
                value={openDraftId}
                onChange={(e) => setOpenDraftId(e.target.value)}
                placeholder="Paste Draft ID here"
                className="input"
              />
            </div>

            <button type="submit" className="btn btn-primary btn-block" disabled={!openDraftId.trim()}>
              → Open Draft
            </button>
          </form>
        </div>

        {/* Tile: Saved plans */}
        <div className="tile">
          <div className="tile-header">
            <h2>Saved Plans</h2>
            <span className="tile-icon">📚</span>
          </div>

          {savedPlansError && <div className="alert alert-error">{savedPlansError}</div>}
          {savedPlans && savedPlans.length === 0 && <p>No saved plans yet.</p>}
          {savedPlans && savedPlans.length > 0 && (
            <ul className="saved-plans" data-testid="saved-plans" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
              {savedPlans.map((plan) => (
                <li key={plan.draftId} style={{ padding: '8px 0', borderBottom: '1px solid #dee2e6' }}>
                  <div>
                    <span className={`status-badge status-${plan.status.toLowerCase()}`}>{plan.status}</span>{' '}
                    <strong>{plan.assetIds.join(', ') || 'No assets'}</strong>
                    {plan.windowId && <> · {plan.windowId}</>}
                    {plan.originalJobId && <> · revision</>}
                  </div>
                  <div style={{ fontSize: '0.85em', color: '#6c757d' }}>
                    Saved {new Date(plan.createdAtUtc).toLocaleString()} · draft <code>{plan.draftId}</code>
                  </div>
                  {plan.requestText && (
                    <div style={{ fontSize: '0.85em' }}>{plan.requestText}</div>
                  )}
                  <button
                    onClick={() => handleOpenDraftDetail(plan.draftId)}
                    className="btn btn-secondary"
                    style={{ marginTop: '4px' }}
                  >
                    → Open plan
                  </button>
                </li>
              ))}
            </ul>
          )}
          <button onClick={loadSavedPlans} className="btn btn-link">↻ Refresh</button>
        </div>

        {/* Tile 3: Recently Created Job */}
        {createdJobId && (
          <div className="tile tile-success">
            <div className="tile-header">
              <h2>✓ Job Created</h2>
              <span className="tile-icon">🎉</span>
            </div>

            <div className="job-created-content">
              <p className="job-created-text">Your planning job has been submitted for processing.</p>

              <div className="job-id-box">
                <span className="label">Job ID:</span>
                <code className="job-id">{createdJobId}</code>
              </div>

              <button
                onClick={() => quickCheckStatus(createdJobId)}
                className="btn btn-primary btn-block"
              >
                → Check Status
              </button>

              <button
                onClick={() => setCreatedJobId(null)}
                className="btn btn-secondary btn-block"
              >
                Create Another
              </button>
            </div>
          </div>
        )}

      </div>
    </div>
  )
}

export default App
