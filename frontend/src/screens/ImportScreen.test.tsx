/// <reference types="vitest" />
/// <reference types="@testing-library/jest-dom" />

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ImportScreen, MAXIMUM_DATASET_BYTES, MAXIMUM_MANUAL_BYTES } from './ImportScreen';
import { apiClient } from '../services/apiClient';
import type { ImportReport } from '../types/api';

vi.mock('../services/apiClient', () => ({
  apiClient: {
    importDataset: vi.fn(),
    importManual: vi.fn(),
  },
}));

const api = apiClient as unknown as Record<string, ReturnType<typeof vi.fn>>;

const rejected: ImportReport = {
  dataset: 'assets', format: 'csv', fileName: 'assets.csv', imported: false,
  totalRecords: 2, importedRecords: 0, invalidRecords: 1,
  issues: [
    { recordNumber: null, location: 'file', recordId: null, field: null, message: 'Unknown column \'colour\'.' },
    { recordNumber: 3, location: 'row 3', recordId: 'P-201', field: 'priority', message: 'priority must be between 1 and 99, but was 0.' },
  ],
};

const imported: ImportReport = {
  dataset: 'assets', format: 'csv', fileName: 'assets-fixed.csv', imported: true,
  totalRecords: 2, importedRecords: 2, invalidRecords: 0, issues: [],
};

const chooseFile = (file: File) => fireEvent.change(screen.getByLabelText(/^File/), { target: { files: [file] } });
const csv = (name = 'assets.csv') => new File(['id,plantId,name,model,priority\n'], name, { type: 'text/csv' });
const pdf = (name = 'manual.pdf') => new File(['%PDF-1.4'], name, { type: 'application/pdf' });
const queryConfirmation = () => screen.queryByRole('region', { name: /Confirm the import/ });
const importButton = () => screen.getByRole('button', { name: /Import/ });
const chooseManual = () => fireEvent.change(screen.getByLabelText(/Dataset/), { target: { value: 'manuals' } });
const fillManualFields = () => {
  fireEvent.change(screen.getByLabelText(/Document ID/), { target: { value: 'MAN-TEST-001' } });
  fireEvent.change(screen.getByLabelText(/Version/), { target: { value: '1.0' } });
};

describe('ImportScreen (FR02)', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('requires a file with a supported extension and size before asking for confirmation', () => {
    render(<ImportScreen onBack={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    expect(screen.getByText('Choose a file to import.')).toBeInTheDocument();

    chooseFile(new File(['x'], 'assets.txt'));
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    expect(screen.getByText('Structured data must be uploaded as a .csv or .json file.')).toBeInTheDocument();

    const large = csv();
    Object.defineProperty(large, 'size', { value: MAXIMUM_DATASET_BYTES + 1 });
    chooseFile(large);
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    expect(screen.getByText('The file exceeds 10 MB.')).toBeInTheDocument();

    expect(queryConfirmation()).not.toBeInTheDocument();
    expect(api.importDataset).not.toHaveBeenCalled();
  });

  it('asks for confirmation, which is not a preview, before uploading; cancelling uploads nothing', async () => {
    api.importDataset.mockResolvedValue(imported);
    render(<ImportScreen onBack={vi.fn()} />);
    const file = csv();
    chooseFile(file);

    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    const dialog = screen.getByRole('region', { name: /Confirm the import/ });
    expect(dialog).toHaveTextContent('not a preview');
    expect(dialog).toHaveTextContent('insert-only');
    expect(dialog).toHaveTextContent('all-or-nothing');
    expect(dialog).toHaveTextContent('cannot be undone');
    expect(api.importDataset).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(queryConfirmation()).not.toBeInTheDocument();
    expect(api.importDataset).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));

    await waitFor(() => expect(api.importDataset).toHaveBeenCalledWith('assets', file));
    const report = await screen.findByTestId('import-report');
    expect(report).toHaveTextContent('Imported 2 of 2 records.');
    expect(report).toHaveTextContent('assets-fixed.csv');
  });

  it('shows the manual fields only for PDF manuals and requires them', async () => {
    api.importManual.mockResolvedValue({ ...imported, dataset: 'manuals', format: 'pdf', fileName: 'manual.pdf' });
    render(<ImportScreen onBack={vi.fn()} />);
    expect(screen.queryByLabelText(/Document ID/)).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Dataset/), { target: { value: 'manuals' } });
    expect(screen.getByLabelText(/^File/)).toHaveAttribute('accept', '.pdf');
    const pdf = new File(['%PDF-1.4'], 'manual.pdf', { type: 'application/pdf' });
    chooseFile(pdf);

    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    expect(screen.getByText('A document ID is required.')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Document ID/), { target: { value: ' MAN-TEST-001 ' } });
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    expect(screen.getByText('A version is required.')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Version/), { target: { value: '1.0' } });

    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));

    await waitFor(() => expect(api.importManual).toHaveBeenCalledWith(pdf, 'MAN-TEST-001', '1.0'));
    expect(api.importDataset).not.toHaveBeenCalled();
    expect(await screen.findByText('For manuals, each record is a page.')).toBeInTheDocument();
  });

  it('lists every issue of a rejected import, says nothing was saved, and imports a corrected file', async () => {
    api.importDataset.mockResolvedValueOnce(rejected).mockResolvedValueOnce(imported);
    render(<ImportScreen onBack={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/Dataset/), { target: { value: 'tasks' } });
    chooseFile(new File(['[]'], 'tasks.json'));
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));

    const report = await screen.findByTestId('import-report');
    expect(report).toHaveTextContent('nothing was saved');
    expect(report).toHaveTextContent('Rejected');
    const rows = screen.getByTestId('import-issues').querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('file');
    expect(rows[0]).toHaveTextContent('Unknown column');
    expect(rows[1]).toHaveTextContent('row 3');
    expect(rows[1]).toHaveTextContent('P-201');
    expect(rows[1]).toHaveTextContent('priority');
    expect(rows[1]).toHaveTextContent('must be between 1 and 99');

    // The dataset is kept; the file must be chosen again
    expect(screen.getByLabelText(/Dataset/)).toHaveValue('tasks');
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    expect(screen.getByText('Choose a file to import.')).toBeInTheDocument();

    const corrected = new File(['[]'], 'tasks-fixed.json');
    chooseFile(corrected);
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));

    await waitFor(() => expect(api.importDataset).toHaveBeenLastCalledWith('tasks', corrected));
    expect(await screen.findByText('Imported 2 of 2 records.')).toBeInTheDocument();
    expect(screen.queryByTestId('import-issues')).not.toBeInTheDocument();
  });

  it('shows a failed request and allows another attempt', async () => {
    api.importDataset.mockRejectedValueOnce(new Error('Could not reach the API: Failed to fetch'));
    render(<ImportScreen onBack={vi.fn()} />);
    chooseFile(csv());
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));

    expect(await screen.findByText('Could not reach the API: Failed to fetch')).toBeInTheDocument();
    expect(screen.queryByTestId('import-report')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Import/ })).toBeEnabled();
  });

  it('shows a loading state and sends one request however often confirm is clicked', async () => {
    let resolve: (value: ImportReport) => void = () => {};
    api.importDataset.mockReturnValue(new Promise<ImportReport>((r) => { resolve = r; }));
    render(<ImportScreen onBack={vi.fn()} />);
    chooseFile(csv());
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));

    const confirm = screen.getByRole('button', { name: /Confirm import/ });
    fireEvent.click(confirm);
    fireEvent.click(confirm);

    const loading = await screen.findByRole('button', { name: 'Importing…' });
    expect(loading).toBeDisabled();
    expect(screen.getByLabelText(/Dataset/)).toBeDisabled();
    expect(screen.getByRole('button', { name: /Back to dashboard/ })).toBeDisabled();
    expect(api.importDataset).toHaveBeenCalledTimes(1);

    resolve(imported);
    expect(await screen.findByTestId('import-report')).toBeInTheDocument();
  });

  it('moves focus into the labelled confirmation, describes it on Confirm, and cancels with Escape', async () => {
    api.importDataset.mockResolvedValue(imported);
    render(<ImportScreen onBack={vi.fn()} />);
    chooseFile(csv());
    fireEvent.click(importButton());

    const region = screen.getByRole('region', { name: 'Confirm the import of assets.csv.' });
    const confirm = screen.getByRole('button', { name: /Confirm import/ });
    expect(region).toContainElement(confirm);
    expect(confirm).toHaveFocus();
    expect(confirm).toHaveAccessibleDescription(expect.stringContaining('not a preview'));
    expect(confirm).toHaveAccessibleDescription(expect.stringContaining('all-or-nothing'));

    fireEvent.keyDown(confirm, { key: 'Escape' });
    expect(queryConfirmation()).not.toBeInTheDocument();
    expect(importButton()).toHaveFocus();
    expect(api.importDataset).not.toHaveBeenCalled();

    // Keyboard users proceed from the focused Confirm button; focus returns to Import when the import ends
    fireEvent.click(importButton());
    fireEvent.click(document.activeElement as HTMLElement);
    await waitFor(() => expect(api.importDataset).toHaveBeenCalledTimes(1));
    await screen.findByTestId('import-report');
    expect(importButton()).toHaveFocus();
  });

  it('clears the file, confirmation, report and error when the dataset changes', async () => {
    api.importDataset.mockResolvedValueOnce(rejected).mockRejectedValueOnce(new Error('Import failed: 500 Internal Server Error'));
    render(<ImportScreen onBack={vi.fn()} />);
    chooseFile(csv());
    fireEvent.click(importButton());
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));
    await screen.findByTestId('import-report');

    chooseFile(csv('assets-fixed.csv'));
    fireEvent.click(importButton());
    expect(queryConfirmation()).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Dataset/), { target: { value: 'technicians' } });
    expect(queryConfirmation()).not.toBeInTheDocument();
    expect(screen.queryByTestId('import-report')).not.toBeInTheDocument();
    fireEvent.click(importButton());
    expect(screen.getByText('Choose a file to import.')).toBeInTheDocument();

    chooseFile(csv('technicians.csv'));
    fireEvent.click(importButton());
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));
    expect(await screen.findByText('Import failed: 500 Internal Server Error')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Dataset/), { target: { value: 'tasks' } });
    expect(screen.queryByText('Import failed: 500 Internal Server Error')).not.toBeInTheDocument();
    expect(api.importDataset).toHaveBeenCalledTimes(2);
  });

  it('rejects a manual that is not a PDF or exceeds 20 MB', () => {
    render(<ImportScreen onBack={vi.fn()} />);
    chooseManual();
    fillManualFields();

    chooseFile(csv('manual.csv'));
    fireEvent.click(importButton());
    expect(screen.getByText('Manuals must be uploaded as .pdf files.')).toBeInTheDocument();

    const large = pdf();
    Object.defineProperty(large, 'size', { value: MAXIMUM_MANUAL_BYTES + 1 });
    chooseFile(large);
    fireEvent.click(importButton());
    expect(screen.getByText('The file exceeds 20 MB.')).toBeInTheDocument();

    expect(queryConfirmation()).not.toBeInTheDocument();
    expect(api.importManual).not.toHaveBeenCalled();
  });

  it('closes the confirmation without uploading when a manual field is edited', () => {
    render(<ImportScreen onBack={vi.fn()} />);
    chooseManual();
    chooseFile(pdf());
    fillManualFields();

    fireEvent.click(importButton());
    expect(queryConfirmation()).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Document ID/), { target: { value: 'MAN-TEST-002' } });
    expect(queryConfirmation()).not.toBeInTheDocument();

    fireEvent.click(importButton());
    expect(queryConfirmation()).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Version/), { target: { value: '2.0' } });
    expect(queryConfirmation()).not.toBeInTheDocument();

    expect(api.importManual).not.toHaveBeenCalled();
  });

  it('returns to the dashboard', () => {
    const onBack = vi.fn();
    render(<ImportScreen onBack={onBack} />);

    fireEvent.click(screen.getByRole('button', { name: /Back to dashboard/ }));

    expect(onBack).toHaveBeenCalled();
  });
});
