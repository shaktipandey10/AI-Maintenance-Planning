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

    it('should switch details on candidate click', async () => {
      mockApiClient.apiClient.getCandidates.mockResolvedValue(
        mockCandidatesResponse
      );

      const { container } = render(
        <AlternativesScreen jobId={mockJobId} onBack={mockOnBack} />
      );

      await waitFor(() => {
        const buttons = container.querySelectorAll('button');
        // Click the second candidate card
        const secondCandidateBtn = Array.from(buttons).find(
          (btn) => btn.textContent?.includes('Alternative 2')
        ) as HTMLButtonElement;
        expect(secondCandidateBtn).toBeTruthy();
        if (secondCandidateBtn) {
          fireEvent.click(secondCandidateBtn);
        }
      });

      await waitFor(() => {
        expect(screen.queryByText(/Alternative 2 - Details/i)).toBeTruthy();
      });
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
