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

describe('apiClient imports (FR02)', () => {
  const report = {
    dataset: 'assets', format: 'csv', fileName: 'assets.csv', imported: false,
    totalRecords: 2, importedRecords: 0, invalidRecords: 1,
    issues: [{ recordNumber: 3, location: 'row 3', recordId: 'P-201', field: 'priority', message: 'priority is required.' }],
  };

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  const stubFetch = (status: number, body: string, statusText = '') => {
    const fetchMock = vi.fn().mockResolvedValue({
      status,
      ok: status >= 200 && status < 300,
      statusText,
      text: async () => body,
    });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  };

  it('posts the dataset file as multipart form data without setting a Content-Type', async () => {
    const fetchMock = stubFetch(200, JSON.stringify({ ...report, imported: true, importedRecords: 2, invalidRecords: 0, issues: [] }));
    const file = new File(['id,plantId,name,model,priority\n'], 'assets.csv', { type: 'text/csv' });

    const result = await apiClient.importDataset('assets', file);

    expect(result.imported).toBe(true);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/imports\/assets$/);
    expect(init.method).toBe('POST');
    expect(init.headers).toBeUndefined();
    expect(init.body).toBeInstanceOf(FormData);
    expect((init.body as FormData).get('file')).toBeInstanceOf(File);
    expect(((init.body as FormData).get('file') as File).name).toBe('assets.csv');
    expect((init.body as FormData).has('documentId')).toBe(false);
  });

  it('posts a manual with its file, document ID and version', async () => {
    const fetchMock = stubFetch(200, JSON.stringify({ ...report, dataset: 'manuals', format: 'pdf', imported: true, issues: [] }));
    const file = new File(['%PDF-1.4'], 'manual.pdf', { type: 'application/pdf' });

    await apiClient.importManual(file, 'MAN-TEST-001', '1.0');

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/imports\/manuals$/);
    const form = init.body as FormData;
    expect((form.get('file') as File).name).toBe('manual.pdf');
    expect(form.get('documentId')).toBe('MAN-TEST-001');
    expect(form.get('version')).toBe('1.0');
  });

  it.each([200, 400, 422])('returns the import report sent with status %i', async (status) => {
    stubFetch(status, JSON.stringify(report));

    const result = await apiClient.importDataset('assets', new File(['x'], 'assets.csv'));

    expect(result).toEqual(report);
  });

  it('throws the problem details of a server error', async () => {
    stubFetch(500, JSON.stringify({ title: 'An error occurred while processing your request.' }), 'Internal Server Error');

    await expect(apiClient.importDataset('tasks', new File(['x'], 'tasks.json')))
      .rejects.toThrow('An error occurred while processing your request.');
  });

  it('throws when a 400 response is not an import report', async () => {
    stubFetch(400, '', 'Bad Request');

    await expect(apiClient.importDataset('tasks', new File(['x'], 'tasks.json')))
      .rejects.toThrow('Import failed: 400 Bad Request');
  });

  it.each([
    ['a stack trace', 'System.InvalidOperationException: boom\r\n   at MaintenancePlanner.Api.Endpoints.ImportEndpoints.<>c.<MapImportEndpoints>b__0_1()'],
    ['an HTML page', '<!DOCTYPE html><html><body><h1>Internal Server Error</h1></body></html>'],
    ['an overlong body', 'x'.repeat(201)],
  ])('shows a concise fallback, not %s, for a non-JSON server error', async (_kind, body) => {
    stubFetch(500, body, 'Internal Server Error');

    const error = await apiClient.importDataset('assets', new File(['x'], 'assets.csv')).catch((err: Error) => err);

    expect(error).toBeInstanceOf(Error);
    expect((error as Error).message).toBe('Import failed: 500 Internal Server Error');
  });

  it('keeps a short plain-text error message', async () => {
    stubFetch(503, 'Service Unavailable', 'Service Unavailable');

    await expect(apiClient.importDataset('assets', new File(['x'], 'assets.csv')))
      .rejects.toThrow('Service Unavailable');
  });

  it.each([
    [200, '{}', 'OK'],
    [200, '<html>proxy page</html>', 'OK'],
    [422, '{"title":"Unprocessable"}', 'Unprocessable Entity'],
    [422, 'not json', 'Unprocessable Entity'],
  ])('reports a %i response without an import report as a safe error', async (status, body, statusText) => {
    stubFetch(status, body, statusText);

    await expect(apiClient.importDataset('assets', new File(['x'], 'assets.csv')))
      .rejects.toThrow(`Import failed: ${status} ${statusText}. The response did not contain an import report.`);
  });

  it('does not show an HTML error page from other endpoints either', async () => {
    stubFetch(502, '<html><body>Bad Gateway</body></html>', 'Bad Gateway');

    await expect(apiClient.getSavedPlans()).rejects.toThrow(/^Failed to get saved plans: Bad Gateway$/);
  });

  it('reports a network failure as an unreachable API', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    await expect(apiClient.importDataset('assets', new File(['x'], 'assets.csv')))
      .rejects.toThrow('Could not reach the API: Failed to fetch');
  });
});
