/// <reference types="vitest" />
/// <reference types="@testing-library/jest-dom" />

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ImportScreen } from './ImportScreen';

// The screen with the real API client; only fetch is stubbed
describe('ImportScreen with the API client (FR02)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const stubFetch = (status: number, body: string, statusText: string) => {
    const fetchMock = vi.fn().mockResolvedValue({
      status,
      ok: status >= 200 && status < 300,
      statusText,
      text: async () => body,
    });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  };

  const importAssets = () => {
    fireEvent.change(screen.getByLabelText(/^File/), { target: { files: [new File(['id\n'], 'assets.csv')] } });
    fireEvent.click(screen.getByRole('button', { name: /Import/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm import/ }));
  };

  it('renders the issues of a 422 import report sent by the API', async () => {
    const fetchMock = stubFetch(422, JSON.stringify({
      dataset: 'assets', format: 'csv', fileName: 'assets.csv', imported: false,
      totalRecords: 1, importedRecords: 0, invalidRecords: 1,
      issues: [{ recordNumber: 2, location: 'row 2', recordId: 'P-201', field: 'plantId', message: 'plantId is required.' }],
    }), 'Unprocessable Entity');
    render(<ImportScreen onBack={vi.fn()} />);

    importAssets();

    expect(await screen.findByTestId('import-report')).toHaveTextContent('nothing was saved');
    expect(screen.getByTestId('import-issues')).toHaveTextContent('plantId is required.');
    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/imports\/assets$/);
  });

  it('shows a concise error instead of a server stack trace', async () => {
    stubFetch(500, 'System.Exception: boom\n   at MaintenancePlanner.Secret.Path()', 'Internal Server Error');
    render(<ImportScreen onBack={vi.fn()} />);

    importAssets();

    expect(await screen.findByText('Import failed: 500 Internal Server Error')).toBeInTheDocument();
    expect(screen.queryByText(/MaintenancePlanner\.Secret/)).not.toBeInTheDocument();
  });
});
