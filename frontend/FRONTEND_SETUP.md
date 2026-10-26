# Frontend Setup Guide - Phase 1

This is the frontend for the AI Maintenance Planner application.

## Prerequisites

- Node.js 20+ (tested with v24.14.1)
- npm 10+ (tested with v11.11.0)
- Backend API running on `http://localhost:5128` (or configured in `.env.development`)

## Installation

```bash
cd frontend
npm install
```

## Development

To start the development server:

```bash
npm run dev
```

The application will be available at `http://localhost:5173` by default.

The API base URL is configured in `.env.development` and defaults to `http://localhost:5000`.

## Building

To build for production:

```bash
npm run build
```

The compiled output will be in the `dist/` directory.

## Linting

To check code quality:

```bash
npm run lint
```

## Project Structure

- `src/screens/` - Page components
- `src/components/` - Reusable UI components
- `src/services/` - API client and business logic
- `src/types/` - TypeScript type definitions
- `src/styles/` - Component-scoped CSS

## Frontend Phase 1 Implementation

**Current Status**: Planning Request Screen

The frontend implements the first phase of the maintenance planner:

### PlanningRequestScreen Component

Features:
- Natural language request input
- Dynamic asset ID management (add/remove assets)
- Optional maintenance window ID input
- Form validation
- Error and success messages
- Idempotent request creation with automatic UUID generation

The component integrates with the backend via the `ApiClient` service which:
- Creates planning jobs with proper HTTP headers
- Generates idempotency keys automatically
- Handles error responses gracefully
- Uses fetch API with JSON serialization

### Backend Integration

The frontend expects the backend API to be running and providing:

1. **POST /api/planning-jobs** - Create a new planning job
   - Required headers: `Idempotency-Key` (UUID)
   - Request body: `{ requestText, assetIds, windowId? }`
   - Response: `{ jobId, statusUrl }`

## Testing the Planning Request Form

1. Start the backend API on port 5000
2. Run `npm run dev` in the frontend directory
3. Navigate to `http://localhost:5173`
4. Fill in the form:
   - **Maintenance Request**: "Replace bearing on pump P-101"
   - **Asset IDs**: ["P-101"]
   - **Maintenance Window**: (optional)
5. Click "Create Planning Job"
6. You should see a success message with the created Job ID

## Phase 2 Features (Planned)

- Job status polling and display
- Candidate review and comparison
- Approval workflow
- Revision history and replanning
- Export functionality (PDF/JSON)

## Environment Variables

### Development (.env.development)
```
VITE_API_BASE=http://localhost:5000
```

### Production (.env.production)
```
VITE_API_BASE=/api
```

## Architecture Notes

- **State Management**: React hooks (useState) for component-level state
- **API Communication**: Fetch API with JSON
- **Type Safety**: Full TypeScript implementation
- **Styling**: CSS modules and scoped CSS
- **Build Tool**: Vite for fast development and optimized production builds

## Known Limitations (Phase 1)

- No candidate viewing yet - Phase 2
- No job status polling - Phase 2
- No approval workflow - Phase 2
- No revision history - Phase 2
- No export functionality - Phase 2
