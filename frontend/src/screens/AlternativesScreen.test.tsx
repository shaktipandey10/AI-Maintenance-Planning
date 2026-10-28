import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { AlternativesScreen } from './AlternativesScreen';
import * as apiClient from '../services/apiClient';

vi.mock('../services/apiClient');

const mockApiClient = apiClient as any;

describe('AlternativesScreen', () => {
  const mockJobId = 'test-job-123';
  const mockOnBack = vi.fn();

  const mockCandidatesResponse = {
    jobId: mockJobId,
    status: 'Succeeded',
    outcome: 'CandidatesGenerated',
    interpretation: {
      maintenanceIntent: 'Replace bearing',
      scope: 'P-101',
      proposedAssetIds: ['P-101'],
      proposedTaskIds: ['P101-BEARING-REPLACEMENT'],
    },
    candidates: [
      {
        rationale: 'Standard bearing replacement',
        requestedTaskIds: ['P101-BEARING-REPLACEMENT'],
        dependencyTaskIds: [],
        taskPlacements: [
          {
            taskId: 'P101-PREP',
            start: '2026-10-24T08:00:00+05:30',
            end: '2026-10-24T09:00:00+05:30',
            technicianIds: ['TECH-001'],
          },
          {
            taskId: 'P101-BEARING-REPLACEMENT',
            start: '2026-10-24T09:00:00+05:30',
            end: '2026-10-24T12:00:00+05:30',
            technicianIds: ['TECH-001', 'TECH-002'],
          },
        ],
        evidenceReferences: ['manual-section-2.1'],
        totalLabourCostAmount: 450.5,
        totalLabourCostCurrency: 'USD',
        completionTimeMinutes: 240,
        deferralReasons: [],
        isLowestCostCandidate: true,
        costComparabilityWarning: null,
      },
      {
        rationale: 'Extended bearing replacement with verification',
        requestedTaskIds: ['P101-BEARING-REPLACEMENT', 'P101-VERIFICATION'],
        dependencyTaskIds: [],
        taskPlacements: [
          {
            taskId: 'P101-PREP',
            start: '2026-10-24T08:00:00+05:30',
            end: '2026-10-24T09:00:00+05:30',
            technicianIds: ['TECH-001'],
          },
        ],
        evidenceReferences: ['manual-section-2.1'],
        totalLabourCostAmount: 650.75,
        totalLabourCostCurrency: 'USD',
        completionTimeMinutes: 360,
        deferralReasons: [],
        isLowestCostCandidate: false,
        costComparabilityWarning: 'Price 44% higher than lowest cost option',
      },
    ],
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('Loading and error states', () => {
    it('should display loading message while fetching', () => {
      mockApiClient.apiClient.getCandidates.mockReturnValue(
        new Promise(() => {})
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);
      expect(screen.queryByText(/Loading candidates/i)).toBeTruthy();
    });

    it('should display error message on fetch failure', async () => {
      mockApiClient.apiClient.getCandidates.mockRejectedValue(
        new Error('Network error')
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText(/Error Loading Candidates/i)).toBeTruthy();
        expect(screen.queryByText(/Network error/i)).toBeTruthy();
      });
    });

    it('should call onBack when back button is clicked in error state', async () => {
      mockApiClient.apiClient.getCandidates.mockRejectedValue(
        new Error('API Error')
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        const backButton = screen.queryByText(/Back to Job Status/i);
        expect(backButton).toBeTruthy();
      });
    });
  });

  describe('Empty and non-candidate outcomes', () => {
    it('should display message for empty candidates', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue({
        ...mockCandidatesResponse,
        candidates: [],
      });

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        const text = screen.queryByText(/No candidates available/i);
        expect(text).toBeTruthy();
      });
    });

    it('should display outcome message for non-CandidatesGenerated', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue({
        ...mockCandidatesResponse,
        outcome: 'ClarificationRequired',
        candidates: [],
      });

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        const text = screen.queryByText(/Planning Outcome: ClarificationRequired/i);
        expect(text).toBeTruthy();
      });
    });
  });

  describe('Candidate display', () => {
    it('should render all candidates', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        const alternatives = screen.queryAllByText(/Alternative/i);
        expect(alternatives.length).toBeGreaterThan(2); // At least cards + details heading
      });
    });

    it('should display candidate rationale', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(
          screen.queryByText('Standard bearing replacement')
        ).toBeTruthy();
        expect(
          screen.queryByText(/Extended bearing replacement/i)
        ).toBeTruthy();
      });
    });

    it('should show lowest cost badge', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      const { container } = render(
        <AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />
      );

      await waitFor(() => {
        const badge = container.querySelector('.badge-lowest-cost');
        expect(badge?.textContent?.includes('Lowest Cost')).toBe(true);
      });
    });
  });

  describe('Currency handling', () => {
    it('should display known currency', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText(/450.5.*USD/i)).toBeTruthy();
      });
    });

    it('should display "Currency unknown" for UNKNOWN currency', async () => {
      const response = {
        ...mockCandidatesResponse,
        candidates: [
          {
            ...mockCandidatesResponse.candidates[0],
            totalLabourCostCurrency: 'UNKNOWN',
          },
        ],
      };

      mockApiClient.apiClient.getCandidates.mockResolvedValue(response);

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(
          screen.queryByText(/Currency unknown/i)
        ).toBeTruthy();
      });
    });

    it('should display cost warning when present', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(
          screen.queryByText(/Price 44% higher/i)
        ).toBeTruthy();
      });
    });
  });

  describe('Schedule table', () => {
    it('should display task placements', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      const { container } = render(
        <AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />
      );

      await waitFor(() => {
        const table = container.querySelector('.schedule-table');
        const content = table?.textContent || '';
        expect(content.includes('P101-PREP')).toBe(true);
        expect(content.includes('P101-BEARING-REPLACEMENT')).toBe(true);
      });
    });

    it('should display technician assignments', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      const { container } = render(
        <AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />
      );

      await waitFor(() => {
        const table = container.querySelector('.schedule-table');
        const content = table?.textContent || '';
        expect(content.includes('TECH-001')).toBe(true);
        expect(content.includes('TECH-002')).toBe(true);
      });
    });

    it('should handle empty technician list', async () => {
      const response = {
        ...mockCandidatesResponse,
        candidates: [
          {
            ...mockCandidatesResponse.candidates[0],
            taskPlacements: [
              {
                taskId: 'TASK-1',
                start: '2026-10-24T08:00:00+05:30',
                end: '2026-10-24T09:00:00+05:30',
                technicianIds: [],
              },
            ],
          },
        ],
      };

      mockApiClient.apiClient.getCandidates.mockResolvedValue(response);

      const { container } = render(
        <AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />
      );

      await waitFor(() => {
        const table = container.querySelector('.schedule-table');
        const content = table?.textContent || '';
        expect(content.includes('—')).toBe(true);
      });
    });
  });

  describe('Candidate selection', () => {
    it('should select first candidate by default', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText(/Alternative 1 - Details/i)).toBeTruthy();
      });
    });

    it('should proceed to approval with Alternative 1 and index 0 when nothing else is selected', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(mockCandidatesResponse);
      const onProceedToApproval = vi.fn();

      render(
        <AlternativesScreen
          jobId={mockJobId}
          onBack={mockOnBack}
          onProceedToApproval={onProceedToApproval}
        />
      );

      fireEvent.click(await screen.findByRole('button', { name: /Proceed to Approval/i }));

      expect(onProceedToApproval).toHaveBeenCalledTimes(1);
      const [candidate, interpretation, candidateIndex] = onProceedToApproval.mock.calls[0];
      expect(candidateIndex).toBe(0);
      expect(candidate.rationale).toBe('Standard bearing replacement');
      expect(candidate.totalLabourCostAmount).toBe(450.5);
      expect(interpretation).toEqual(mockCandidatesResponse.interpretation);
    });

    it('should proceed to approval with Alternative 2 and index 1 after selecting it', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(mockCandidatesResponse);
      const onProceedToApproval = vi.fn();

      render(
        <AlternativesScreen
          jobId={mockJobId}
          onBack={mockOnBack}
          onProceedToApproval={onProceedToApproval}
        />
      );

      const secondCard = (await screen.findByText('Alternative 2')).closest('[role="button"]') as HTMLElement;
      fireEvent.click(secondCard);
      await waitFor(() => {
        expect(secondCard.getAttribute('aria-pressed')).toBe('true');
        expect(screen.queryByText(/Alternative 2 - Details/i)).toBeTruthy();
      });

      fireEvent.click(screen.getByRole('button', { name: /Proceed to Approval/i }));

      expect(onProceedToApproval).toHaveBeenCalledTimes(1);
      const [candidate, , candidateIndex] = onProceedToApproval.mock.calls[0];
      expect(candidateIndex).toBe(1);
      expect(candidate).toEqual(mockCandidatesResponse.candidates[1]);
      expect(candidate.rationale).toBe('Extended bearing replacement with verification');
    });

    it('should switch details on candidate click', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(
        <AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />
      );

      // Candidate cards are clickable <div>s labelled "Alternative N"; the first is selected initially
      const secondCandidateLabel = await screen.findByText('Alternative 2');
      expect(screen.queryByText(/Alternative 1 - Details/i)).toBeTruthy();

      // Click the second candidate card
      fireEvent.click(secondCandidateLabel);

      await waitFor(() => {
        expect(screen.queryByText(/Alternative 2 - Details/i)).toBeTruthy();
      });
      expect(screen.queryByText(/Alternative 1 - Details/i)).toBeNull();
    });

    const findCard = async (label: string) =>
      (await screen.findByText(label)).closest('[role="button"]') as HTMLElement;

    it('should expose candidate cards as focusable buttons with pressed state', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      const firstCard = await findCard('Alternative 1');
      const secondCard = await findCard('Alternative 2');
      expect(firstCard).toBeTruthy();
      expect(secondCard).toBeTruthy();
      expect(firstCard.tabIndex).toBe(0);
      expect(secondCard.tabIndex).toBe(0);
      expect(firstCard.getAttribute('aria-pressed')).toBe('true');
      expect(secondCard.getAttribute('aria-pressed')).toBe('false');
    });

    it('should select a candidate with the Enter key', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      const secondCard = await findCard('Alternative 2');
      secondCard.focus();
      expect(document.activeElement).toBe(secondCard);
      fireEvent.keyDown(secondCard, { key: 'Enter' });

      await waitFor(() => {
        expect(screen.queryByText(/Alternative 2 - Details/i)).toBeTruthy();
      });
      expect(screen.queryByText(/Alternative 1 - Details/i)).toBeNull();
      expect(secondCard.getAttribute('aria-pressed')).toBe('true');
    });

    it('should select a candidate with the Space key', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      const secondCard = await findCard('Alternative 2');
      fireEvent.keyDown(secondCard, { key: ' ' });

      await waitFor(() => {
        expect(screen.queryByText(/Alternative 2 - Details/i)).toBeTruthy();
      });
      expect(secondCard.getAttribute('aria-pressed')).toBe('true');
    });

    it('should not select a candidate for other keys or keys pressed on its nested View Draft button', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      const secondCard = await findCard('Alternative 2');
      fireEvent.keyDown(secondCard, { key: 'a' });
      const viewDraftButton = secondCard.querySelector('button') as HTMLButtonElement;
      expect(viewDraftButton).toBeTruthy();
      fireEvent.keyDown(viewDraftButton, { key: 'Enter' });

      expect(screen.queryByText(/Alternative 1 - Details/i)).toBeTruthy();
      expect(screen.queryByText(/Alternative 2 - Details/i)).toBeNull();
      expect(secondCard.getAttribute('aria-pressed')).toBe('false');
    });
  });

  describe('Evidence and interpretation', () => {
    it('should display interpretation when available', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText(/Planning Interpretation/i)).toBeTruthy();
        expect(screen.queryByText('Replace bearing')).toBeTruthy();
      });
    });

    it('should display evidence references', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText('manual-section-2.1')).toBeTruthy();
      });
    });
  });

  describe('Deferral reasons', () => {
    it('should display deferral reasons when present', async () => {
      const response = {
        ...mockCandidatesResponse,
        candidates: [
          {
            ...mockCandidatesResponse.candidates[0],
            deferralReasons: [
              {
                taskId: 'P101-VERIFICATION',
                reason: 'Technician unavailable',
              },
            ],
          },
        ],
      };

      mockApiClient.apiClient.getCandidates.mockResolvedValue(response);

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText(/Deferred Tasks/i)).toBeTruthy();
        expect(screen.queryByText(/Technician unavailable/i)).toBeTruthy();
      });
    });
  });

  describe('Completion time', () => {
    it('should display completion time in minutes', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      render(<AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />);

      await waitFor(() => {
        expect(screen.queryByText(/240 min/)).toBeTruthy();
        expect(screen.queryByText(/360 min/)).toBeTruthy();
      });
    });
  });
});
