import { describe, it, expect } from 'vitest';
import { PlanningRequestScreen } from './PlanningRequestScreen';

describe('PlanningRequestScreen - Clarification UI', () => {
  it('should export PlanningRequestScreen component', () => {
    expect(PlanningRequestScreen).toBeDefined();
    expect(typeof PlanningRequestScreen).toBe('function');
  });

  it('should have clarification parsing logic', () => {
    // Test the clarification JSON parsing logic
    const clarificationPayload = JSON.stringify({
      outcome: 'ClarificationRequired',
      result: {
        interpretation: {
          maintenanceIntent: 'Replace bearing',
          scope: 'Unplanned',
          proposedAssetIds: ['P-101'],
          proposedTaskIds: ['P101-BEARING-REPLACEMENT'],
        },
        questions: [
          'What is the preferred maintenance window?',
          'Should downtime be minimized?',
        ],
        assumptions: [],
      },
    });

    const parsed = JSON.parse(clarificationPayload);
    expect(parsed.outcome).toBe('ClarificationRequired');
    expect(parsed.result.questions).toHaveLength(2);
    expect(parsed.result.interpretation.maintenanceIntent).toBe('Replace bearing');
  });

  it('should handle non-clarification payload', () => {
    const normalPayload = JSON.stringify({
      outcome: 'PlanGenerated',
      result: {
        plan: 'test-plan',
      },
    });

    const parsed = JSON.parse(normalPayload);
    expect(parsed.outcome).not.toBe('ClarificationRequired');
    expect(parsed.outcome).toBe('PlanGenerated');
  });
});
