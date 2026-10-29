import React, { useEffect, useRef, useState } from 'react';
import { apiClient } from '../services/apiClient';
import type { ImportDatasetSlug, ImportReport } from '../types/api';
import '../styles/ImportScreen.css';

interface ImportScreenProps {
  onBack: () => void;
}

type ImportKind = ImportDatasetSlug | 'manuals';

// Upload limits enforced by the import endpoints (ReferenceDataImportService.MaximumFileBytes,
// ManualImportService.MaximumPdfBytes)
export const MAXIMUM_DATASET_BYTES = 10 * 1024 * 1024;
export const MAXIMUM_MANUAL_BYTES = 20 * 1024 * 1024;

// Listed in the order their references require: a record can only refer to records that are already imported
const IMPORT_KINDS: { value: ImportKind; label: string }[] = [
  { value: 'assets', label: 'Assets' },
  { value: 'spare-parts', label: 'Spare parts (refer to assets)' },
  { value: 'technicians', label: 'Technicians' },
  { value: 'technician-availability', label: 'Technician availability (refers to technicians)' },
  { value: 'manuals', label: 'PDF manual' },
  { value: 'tasks', label: 'Tasks (refer to assets, spare parts and manual sections)' },
  { value: 'maintenance-history', label: 'Maintenance history (refers to assets)' },
];

const extensionsOf = (kind: ImportKind) => (kind === 'manuals' ? ['.pdf'] : ['.csv', '.json']);
const maximumBytesOf = (kind: ImportKind) => (kind === 'manuals' ? MAXIMUM_MANUAL_BYTES : MAXIMUM_DATASET_BYTES);

export const ImportScreen: React.FC<ImportScreenProps> = ({ onBack }) => {
  const [kind, setKind] = useState<ImportKind>('assets');
  const [file, setFile] = useState<File | null>(null);
  // Changing the key clears the file input, so the same (corrected) file can be chosen again
  const [fileInputKey, setFileInputKey] = useState(0);
  const [documentId, setDocumentId] = useState('');
  const [version, setVersion] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [loading, setLoading] = useState(false);
  const [report, setReport] = useState<ImportReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const importButton = useRef<HTMLButtonElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  // Set when the confirmation closes through its own buttons, so focus goes back to the Import button
  const returnFocus = useRef(false);

  const isManual = kind === 'manuals';

  // Opening the confirmation moves focus to its Confirm button, whose description is the explanation, so keyboard and
  // screen-reader users are told that confirmation is needed
  useEffect(() => {
    if (confirming) {
      confirmButton.current?.focus();
    } else if (returnFocus.current) {
      returnFocus.current = false;
      importButton.current?.focus();
    }
  }, [confirming]);

  const cancelConfirmation = () => {
    returnFocus.current = true;
    setConfirming(false);
  };

  const resetFile = () => {
    setFile(null);
    setFileInputKey((key) => key + 1);
  };

  const handleKindChange = (value: ImportKind) => {
    setKind(value);
    resetFile();
    setValidationError(null);
    setConfirming(false);
    setReport(null);
    setError(null);
  };

  // Client-side checks only; the API validates every record and remains the authority
  const validate = (): string | null => {
    if (!file) {
      return 'Choose a file to import.';
    }

    const extensions = extensionsOf(kind);
    const name = file.name.toLowerCase();
    if (!extensions.some((extension) => name.endsWith(extension))) {
      return isManual
        ? 'Manuals must be uploaded as .pdf files.'
        : 'Structured data must be uploaded as a .csv or .json file.';
    }

    const maximumBytes = maximumBytesOf(kind);
    if (file.size > maximumBytes) {
      return `The file exceeds ${maximumBytes / (1024 * 1024)} MB.`;
    }

    if (isManual && !documentId.trim()) {
      return 'A document ID is required.';
    }

    if (isManual && !version.trim()) {
      return 'A version is required.';
    }

    return null;
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const problem = validate();
    setValidationError(problem);
    setConfirming(problem === null);
  };

  const handleConfirm = async () => {
    if (inFlight.current || !file) {
      return;
    }

    inFlight.current = true;
    setLoading(true);
    setReport(null);
    setError(null);
    try {
      const result = kind === 'manuals'
        ? await apiClient.importManual(file, documentId.trim(), version.trim())
        : await apiClient.importDataset(kind, file);
      setReport(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'The import failed');
    } finally {
      inFlight.current = false;
      returnFocus.current = true;
      setLoading(false);
      setConfirming(false);
      resetFile();
    }
  };

  return (
    <div className="import-screen">
      <div className="screen-header">
        <button onClick={onBack} className="btn-back" disabled={loading}>
          ← Back to dashboard
        </button>
        <h1>Import Data</h1>
        <div className="header-spacer"></div>
      </div>

      <div className="screen-content">
        <section className="detail-section">
          <h2>Choose what to import</h2>
          <form onSubmit={handleSubmit} className="tile-form">
            <div className="form-group">
              <label htmlFor="importKind">Dataset *</label>
              <select
                id="importKind"
                value={kind}
                onChange={(e) => handleKindChange(e.target.value as ImportKind)}
                disabled={loading}
                className="input"
              >
                {IMPORT_KINDS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </div>

            <div className="form-group">
              <label htmlFor="importFile">File * ({extensionsOf(kind).join(', ')}, up to {maximumBytesOf(kind) / (1024 * 1024)} MB)</label>
              <input
                key={fileInputKey}
                id="importFile"
                type="file"
                accept={extensionsOf(kind).join(',')}
                onChange={(e) => {
                  setFile(e.target.files?.[0] ?? null);
                  setValidationError(null);
                  setConfirming(false);
                }}
                disabled={loading}
                className="input"
              />
            </div>

            {isManual && (
              <>
                <div className="form-group">
                  <label htmlFor="importDocumentId">Document ID *</label>
                  <input
                    id="importDocumentId"
                    type="text"
                    value={documentId}
                    onChange={(e) => {
                      setDocumentId(e.target.value);
                      setConfirming(false);
                    }}
                    placeholder="e.g. MAN-PLANT01-SERVICE-001"
                    disabled={loading}
                    className="input"
                  />
                </div>
                <div className="form-group">
                  <label htmlFor="importVersion">Version *</label>
                  <input
                    id="importVersion"
                    type="text"
                    value={version}
                    onChange={(e) => {
                      setVersion(e.target.value);
                      setConfirming(false);
                    }}
                    placeholder="e.g. 1.0"
                    disabled={loading}
                    className="input"
                  />
                </div>
              </>
            )}

            {validationError && <div className="alert alert-error">{validationError}</div>}

            {!confirming && (
              <button ref={importButton} type="submit" className="btn btn-primary btn-block" disabled={loading}>
                📥 Import
              </button>
            )}
          </form>

          {confirming && file && (
            <div
              className="import-confirmation"
              role="region"
              aria-labelledby="import-confirmation-title"
              onKeyDown={(e) => {
                if (e.key === 'Escape' && !loading) {
                  cancelConfirmation();
                }
              }}
            >
              <p id="import-confirmation-title"><strong>Confirm the import of {file.name}.</strong></p>
              <p id="import-confirmation-text">
                This is a confirmation, not a preview: the file is validated and saved in one step. Imports are
                insert-only, so records whose ID already exists are rejected and saved records are never changed.
                They are all-or-nothing, so if any record is invalid nothing is saved. A completed import cannot be
                undone from the application.
              </p>
              <div className="import-confirmation-actions">
                <button
                  ref={confirmButton}
                  onClick={handleConfirm}
                  className="btn btn-primary"
                  disabled={loading}
                  aria-describedby="import-confirmation-title import-confirmation-text"
                >
                  {loading ? 'Importing…' : '✓ Confirm import'}
                </button>
                <button onClick={cancelConfirmation} className="btn btn-secondary" disabled={loading}>
                  Cancel
                </button>
              </div>
            </div>
          )}
        </section>

        {error && (
          <section className="detail-section">
            <div className="alert alert-error">{error}</div>
          </section>
        )}

        {report && (
          <section className="detail-section" data-testid="import-report">
            <h2>Import result</h2>
            {report.imported ? (
              <div className="alert alert-success">
                Imported {report.importedRecords} of {report.totalRecords} records.
              </div>
            ) : (
              <div className="alert alert-error">
                Rejected: nothing was saved. Imports are all-or-nothing; correct every issue below and import the
                corrected file.
              </div>
            )}

            <dl className="import-summary">
              <div><dt>Dataset</dt><dd>{report.dataset}</dd></div>
              <div><dt>Format</dt><dd>{report.format}</dd></div>
              <div><dt>File</dt><dd>{report.fileName || '—'}</dd></div>
              <div><dt>Outcome</dt><dd>{report.imported ? 'Imported' : 'Rejected'}</dd></div>
              <div><dt>Total records</dt><dd>{report.totalRecords}</dd></div>
              <div><dt>Imported records</dt><dd>{report.importedRecords}</dd></div>
              <div><dt>Invalid records</dt><dd>{report.invalidRecords}</dd></div>
            </dl>
            {report.format === 'pdf' && <p className="import-note">For manuals, each record is a page.</p>}

            {report.issues.length > 0 && (
              <table className="import-issues" data-testid="import-issues">
                <thead>
                  <tr>
                    <th>Location</th>
                    <th>Record ID</th>
                    <th>Field</th>
                    <th>Message</th>
                  </tr>
                </thead>
                <tbody>
                  {report.issues.map((issue, index) => (
                    <tr key={index}>
                      <td>{issue.location}</td>
                      <td>{issue.recordId || '—'}</td>
                      <td>{issue.field || '—'}</td>
                      <td>{issue.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        )}
      </div>
    </div>
  );
};
