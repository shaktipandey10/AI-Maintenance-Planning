/// <reference types="vitest" />
/// <reference types="@testing-library/jest-dom" />

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import App from './App';
import { apiClient } from './services/apiClient';

vi.mock('./services/apiClient', () => ({
  apiClient: {
    setActorRole: vi.fn(),
    getSavedPlans: vi.fn(),
  },
}));
vi.mock('./screens/DraftDetailScreen', () => ({
  DraftDetailScreen: ({ draftId }: { draftId: string }) => <div data-testid="draft-detail">Draft {draftId}</div>,
}));

const api = apiClient as unknown as Record<string, ReturnType<typeof vi.fn>>;

describe('App saved plans and actor role', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('lists saved plans on the dashboard and opens one without typing its draft ID', async () => {
    api.getSavedPlans.mockResolvedValue([
      {
        draftId: 'draft-v2', planningJobId: 'job-v2', originalJobId: 'job-v1', status: 'Approved',
        createdAtUtc: '2026-10-28T05:00:00Z', approvedAtUtc: '2026-10-28T05:00:00Z',
        assetIds: ['P-101'], windowId: 'MW-P101-BASELINE-20261024', requestText: 'Replan the bearing replacement',
      },
      {
        draftId: 'draft-v1', planningJobId: 'job-v1', originalJobId: null, status: 'Superseded',
        createdAtUtc: '2026-10-27T19:00:00Z', approvedAtUtc: '2026-10-27T19:00:00Z',
        assetIds: ['P-101'], windowId: 'MW-P101-BASELINE-20261024', requestText: 'Plan the bearing replacement',
      },
    ]);
    render(<App />);

    const list = await screen.findByTestId('saved-plans');
    expect(list).toHaveTextContent('Approved');
    expect(list).toHaveTextContent('Superseded');
    expect(list).toHaveTextContent('revision');
    expect(list).toHaveTextContent('Replan the bearing replacement');

    fireEvent.click(screen.getAllByRole('button', { name: /Open plan/ })[1]);

    expect(await screen.findByTestId('draft-detail')).toHaveTextContent('Draft draft-v1');
  });

  it('shows when there are no saved plans and reports a failed load', async () => {
    api.getSavedPlans.mockResolvedValueOnce([]);
    render(<App />);
    expect(await screen.findByText('No saved plans yet.')).toBeInTheDocument();

    api.getSavedPlans.mockRejectedValueOnce(new Error('Failed to get saved plans: HTTP 500'));
    fireEvent.click(screen.getByRole('button', { name: /Refresh/ }));
    expect(await screen.findByText('Failed to get saved plans: HTTP 500')).toBeInTheDocument();
  });

  it('passes the selected role to the API client as the actor of recorded actions', async () => {
    api.getSavedPlans.mockResolvedValue([]);
    render(<App />);
    await waitFor(() => expect(api.setActorRole).toHaveBeenCalledWith('planner'));

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'reviewer' } });

    await waitFor(() => expect(api.setActorRole).toHaveBeenLastCalledWith('reviewer'));
  });
});
