/// <reference types="vitest" />
/// <reference types="@testing-library/jest-dom" />

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ApprovalScreen } from './ApprovalScreen';
import * as apiClient from '../services/apiClient';

vi.mock('../services/apiClient');

const mockApiClient = apiClient as any;

const mockCandidate = {
  rationale: 'Standard bearing replacement',
  requestedTaskIds: ['P101-BEARING-REPLACEMENT'],
  dependencyTaskIds: [],
  taskPlacements: [
    {
      taskId: 'P101-BEARING-REPLACEMENT',
      start: '2026-10-24T09:00:00Z',
      end: '2026-10-24T12:00:00Z',
      technicianIds: ['TECH-001'],
    },
  ],
  evidenceReferences: ['manual-section-2.1'],
  totalLabourCostAmount: 450.5,
  totalLabourCostCurrency: 'USD',
  completionTimeMinutes: 180,
  deferralReasons: [],
  isLowestCostCandidate: true,
  costComparabilityWarning: undefined,
};

const mockInterpretation = {
  maintenanceIntent: 'Replace bearing',
  scope: 'P-101',
  proposedAssetIds: ['P-101'],
  proposedTaskIds: ['P101-BEARING-REPLACEMENT'],
};

describe('ApprovalScreen', () => {
  const mockJobId = 'test-job-123';
  const mockOnBack = vi.fn();
  const mockOnApprovalSuccess = vi.fn();
  const mockOnViewDraftDetail = vi.fn();

  afterEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  describe('First-time approval flow', () => {
    it('should render approval button when no draft exists', () => {
      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      const approveButton = screen.getByRole('button', { name: /Approve Plan/i });
      expect(approveButton).toBeInTheDocument();
      expect(approveButton).not.toBeDisabled();
    });

    it('should approve draft and store draft ID in localStorage', async () => {
      const mockDraftId = 'draft-456';
      mockApiClient.apiClient.approveDraft.mockResolvedValue({ draftId: mockDraftId });

      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      const approveButton = screen.getByRole('button', { name: /Approve Plan/i });
      fireEvent.click(approveButton);

      await waitFor(() => {
        expect(mockApiClient.apiClient.approveDraft).toHaveBeenCalledWith(mockJobId);
        expect(mockOnApprovalSuccess).toHaveBeenCalledWith(mockDraftId);
      });

      expect(localStorage.getItem(`draft_${mockJobId}`)).toBe(mockDraftId);
    });
  });

  describe('Already approved state', () => {
    it('should show already-approved message when draft ID exists in localStorage', () => {
      const mockDraftId = 'draft-789';
      localStorage.setItem(`draft_${mockJobId}`, mockDraftId);

      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      expect(screen.getByText('Plan Already Approved')).toBeInTheDocument();
      expect(screen.getByText('Already approved — draft exists for this job.')).toBeInTheDocument();
      expect(screen.getByText(mockDraftId)).toBeInTheDocument();
    });

    it('should disable approve button when draft already exists', () => {
      const mockDraftId = 'draft-789';
      localStorage.setItem(`draft_${mockJobId}`, mockDraftId);

      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      const approveButton = screen.getByRole('button', { name: /Approve Plan/i });
      expect(approveButton).toBeDisabled();
    });

    it('should show View Draft Detail button when draft exists', () => {
      const mockDraftId = 'draft-789';
      localStorage.setItem(`draft_${mockJobId}`, mockDraftId);

      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      const viewDraftButton = screen.getByRole('button', { name: /View Draft Detail/i });
      expect(viewDraftButton).toBeInTheDocument();
      expect(viewDraftButton).not.toBeDisabled();

      fireEvent.click(viewDraftButton);
      expect(mockOnViewDraftDetail).toHaveBeenCalledWith(mockDraftId);
    });

    it('should show export buttons when draft exists', () => {
      const mockDraftId = 'draft-789';
      localStorage.setItem(`draft_${mockJobId}`, mockDraftId);

      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      expect(screen.getByRole('button', { name: /Export as JSON/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Export as PDF/i })).toBeInTheDocument();
    });

    it('should not make approval request when draft already exists', async () => {
      const mockDraftId = 'draft-789';
      localStorage.setItem(`draft_${mockJobId}`, mockDraftId);

      render(
        <ApprovalScreen
          jobId={mockJobId}
          candidate={mockCandidate}
          candidateIndex={0}
          interpretation={mockInterpretation}
          onBack={mockOnBack}
          onApprovalSuccess={mockOnApprovalSuccess}
          onViewDraftDetail={mockOnViewDraftDetail}
        />
      );

      await waitFor(() => {
        expect(mockApiClient.apiClient.approveDraft).not.toHaveBeenCalled();
      });
    });
  });
});
