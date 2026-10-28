import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiClient } from './apiClient';

describe('apiClient.approveDraft', () => {
  const jobId = 'job-123';
  const draftId = 'd3b07384-d9a0-4c3b-9b7a-1a2b3c4d5e6f';

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  const stubAcceptedFetch = () => {
    const fetchMock = vi.fn().mockResolvedValue({
      status: 202,
      ok: true,
      statusText: 'Accepted',
      headers: { get: (name: string) => (name.toLowerCase() === 'location' ? `/api/drafts/${draftId}` : null) },
      json: async () => ({}),
    });
    vi.stubGlobal('fetch', fetchMock);
    vi.spyOn(console, 'log').mockImplementation(() => {});
    return fetchMock;
  };

  it.each([0, 1])('posts candidateIndex %i in the JSON body of the approve request', async (candidateIndex) => {
    const fetchMock = stubAcceptedFetch();

    const result = await apiClient.approveDraft(jobId, candidateIndex);

    expect(result).toEqual({ draftId });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(new RegExp(`/api/drafts/${jobId}/approve$`));
    expect(init.method).toBe('POST');
    expect(init.headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(init.body)).toEqual({ candidateIndex });
  });
});
