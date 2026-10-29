import React, { useState, useEffect, useRef } from 'react';
import { apiClient } from '../services/apiClient';
import html2pdf from 'html2pdf.js';
import type { PlanProgressResponse, TaskProgressStatus } from '../types/api';
import '../styles/ProgressScreen.css';

interface ProgressScreenProps {
  draftId: string;
  onBack: () => void;
}

type LoadingState = 'loading' | 'success' | 'error';

export const ProgressScreen: React.FC<ProgressScreenProps> = ({
  draftId,
  onBack,
}) => {
  const [progress, setProgress] = useState<PlanProgressResponse | null>(null);
  const [loadingState, setLoadingState] = useState<LoadingState>('loading');
  const [error, setError] = useState<string | null>(null);
  const [completingTask, setCompletingTask] = useState<string | null>(null);
  const [completionError, setCompletionError] = useState<string | null>(null);
  const [updatingTaskId, setUpdatingTaskId] = useState<string | null>(null);
  const [taskStatusError, setTaskStatusError] = useState<string | null>(null);
  const [pdfExportLoading, setPdfExportLoading] = useState(false);
  const [pdfExportError, setPdfExportError] = useState<string | null>(null);
  const contentRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const fetchProgress = async () => {
      setLoadingState('loading');
      setError(null);
      try {
        const response = await apiClient.getPlanProgress(draftId);
        setProgress(response);
        setLoadingState('success');
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load progress');
        setLoadingState('error');
      }
    };

    fetchProgress();
  }, [draftId]);

  const handleMarkTaskComplete = async (sparePartId: string) => {
    setCompletingTask(sparePartId);
    setCompletionError(null);

    try {
      const result = await apiClient.markTaskComplete(draftId, sparePartId);

      if (!result.succeeded) {
        setCompletionError(result.failureReason || 'Failed to complete task');
        setCompletingTask(null);
        return;
      }

      // Refresh progress after successful completion
      const updatedProgress = await apiClient.getPlanProgress(draftId);
      setProgress(updatedProgress);
      setCompletingTask(null);
    } catch (err) {
      setCompletionError(err instanceof Error ? err.message : 'Failed to complete task');
      setCompletingTask(null);
    }
  };

  const handleUpdateTaskStatus = async (taskId: string, status: TaskProgressStatus) => {
    setUpdatingTaskId(taskId);
    setTaskStatusError(null);

    try {
      await apiClient.updateTaskStatus(draftId, taskId, status);
      const updatedProgress = await apiClient.getPlanProgress(draftId);
      setProgress(updatedProgress);
    } catch (err) {
      setTaskStatusError(err instanceof Error ? err.message : 'Failed to update task status');
    } finally {
      setUpdatingTaskId(null);
    }
  };

  const handleExportPdf = async () => {
    if (!contentRef.current || !progress) {
      setPdfExportError('Unable to export PDF: content not available');
      return;
    }

    setPdfExportLoading(true);
    setPdfExportError(null);

    try {
      const element = contentRef.current;
      console.log('[PDF Export] Starting PDF export for progress:', progress.draftId);
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
        filename: `Progress-${progress.draftId}.pdf`,
        image: { type: 'jpeg', quality: 0.98 },
        html2canvas: { scale: 2, logging: false, useCORS: true },
        jsPDF: { orientation: 'portrait', unit: 'mm', format: 'a4' },
        pagebreak: { mode: ['avoid-all', 'css', 'legacy'] },
      };

      console.log('[PDF Export] Starting html2pdf capture');
      await html2pdf().set(options as any).from(wrapper).save();
      console.log('[PDF Export] Progress PDF generated successfully');

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

  const progressPercent = progress && progress.totalRequired > 0
    ? Math.round((progress.completedQuantity / progress.totalRequired) * 100)
    : 0;

  if (loadingState === 'loading') {
    return (
      <div className="progress-screen">
        <div className="screen-header">
          <button onClick={onBack} className="btn-back">
            ← Back
          </button>
          <h1>Progress Tracking</h1>
          <div className="header-spacer"></div>
        </div>
        <div className="screen-content">
          <div className="loading-container">
            <div className="spinner"></div>
            <p>Loading progress...</p>
          </div>
        </div>
      </div>
    );
  }

  if (loadingState === 'error' || !progress) {
    return (
      <div className="progress-screen">
        <div className="screen-header">
          <button onClick={onBack} className="btn-back">
            ← Back
          </button>
          <h1>Progress Tracking</h1>
          <div className="header-spacer"></div>
        </div>
        <div className="screen-content">
          <div className="error-container">
            <div className="alert alert-error">
              {error || 'Failed to load progress'}
            </div>
            <button onClick={onBack} className="btn btn-primary">
              ← Back
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="progress-screen">
      <div className="screen-header">
        <button onClick={onBack} className="btn-back">
          ← Back
        </button>
        <h1>Progress Tracking</h1>
        <div className="header-spacer"></div>
      </div>

      <div className="screen-content" ref={contentRef}>
        {/* Overall Progress */}
        <section className="detail-section">
          <h2>Overall Progress</h2>
          <div className="progress-summary">
            <div className="metric">
              <span className="label">Total Required</span>
              <span className="value">{progress.totalRequired}</span>
            </div>
            <div className="metric">
              <span className="label">Completed</span>
              <span className="value completed">{progress.completedQuantity}</span>
            </div>
            <div className="metric">
              <span className="label">Remaining</span>
              <span className="value remaining">{progress.remainingQuantity}</span>
            </div>
          </div>

          <div className="progress-bar-container">
            <div className="progress-bar">
              <div
                className="progress-fill"
                style={{ width: `${progressPercent}%` }}
              ></div>
            </div>
            <span className="progress-percent">{progressPercent}% Complete</span>
          </div>
        </section>

        {/* Task statuses: each task moves forward only, NotStarted → InProgress → Completed */}
        {progress.taskStatuses && progress.taskStatuses.length > 0 && (
          <section className="detail-section">
            <h2>Task Status</h2>
            {taskStatusError && (
              <div className="alert alert-error">{taskStatusError}</div>
            )}
            <div className="tasks-list">
              {progress.taskStatuses.map((task) => {
                const nextStatus: TaskProgressStatus | null =
                  task.status === 'NotStarted' ? 'InProgress'
                    : task.status === 'InProgress' ? 'Completed'
                      : null;
                const statusLabel =
                  task.status === 'NotStarted' ? 'Not started'
                    : task.status === 'InProgress' ? 'In progress'
                      : '✓ Completed';

                return (
                  <div
                    key={task.taskId}
                    className={`task-card ${task.status === 'Completed' ? 'complete' : ''}`}
                  >
                    <div className="task-header">
                      <span className="part-id">{task.taskId}</span>
                      <span className={`status-badge ${task.status === 'Completed' ? 'status-complete' : 'status-pending'}`}>
                        {statusLabel}
                      </span>
                    </div>
                    {nextStatus && (
                      <button
                        onClick={() => handleUpdateTaskStatus(task.taskId, nextStatus)}
                        disabled={updatingTaskId === task.taskId}
                        className="btn btn-secondary btn-mark-complete"
                      >
                        {updatingTaskId === task.taskId
                          ? '⏳ Updating...'
                          : nextStatus === 'InProgress' ? '▶ Start' : '✓ Complete'}
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
          </section>
        )}

        {/* Tasks */}
        <section className="detail-section">
          <h2>Task Progress by Spare Part</h2>
          {progress.tasks.length > 0 ? (
            <div className="tasks-list">
              {progress.tasks.map((task) => {
                const taskProgressPercent = task.requiredQuantity > 0
                  ? Math.round((task.completedQuantity / task.requiredQuantity) * 100)
                  : 0;
                const isComplete = task.remainingQuantity === 0;

                return (
                  <div
                    key={task.sparePartId}
                    className={`task-card ${isComplete ? 'complete' : ''}`}
                  >
                    <div className="task-header">
                      <span className="part-id">{task.sparePartId}</span>
                      <span className={`status-badge ${isComplete ? 'status-complete' : 'status-pending'}`}>
                        {isComplete ? '✓ Complete' : 'In Progress'}
                      </span>
                    </div>

                    <div className="task-metrics">
                      <div className="metric-row">
                        <span className="label">Required:</span>
                        <span className="value">{task.requiredQuantity}</span>
                      </div>
                      <div className="metric-row">
                        <span className="label">Completed:</span>
                        <span className="value">{task.completedQuantity}</span>
                      </div>
                      <div className="metric-row">
                        <span className="label">Remaining:</span>
                        <span className="value">{task.remainingQuantity}</span>
                      </div>
                    </div>

                    <div className="task-progress-bar">
                      <div className="progress-bar">
                        <div
                          className="progress-fill"
                          style={{ width: `${taskProgressPercent}%` }}
                        ></div>
                      </div>
                      <span className="progress-text">{taskProgressPercent}%</span>
                    </div>

                    {!isComplete && (
                      <button
                        onClick={() => handleMarkTaskComplete(task.sparePartId)}
                        disabled={completingTask === task.sparePartId}
                        className="btn btn-secondary btn-mark-complete"
                      >
                        {completingTask === task.sparePartId
                          ? '⏳ Marking Complete...'
                          : '✓ Mark Complete'}
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
          ) : (
            <p className="empty-message">No tasks in this plan.</p>
          )}

          {completionError && (
            <div className="alert alert-error" style={{ marginTop: '16px' }}>
              {completionError}
            </div>
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
            {pdfExportLoading ? '⏳ Generating PDF...' : '📄 Export Progress as PDF'}
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
