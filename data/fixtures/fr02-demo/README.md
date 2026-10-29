# FR02 import demo fixtures

Small CSV files for demonstrating the FR02 Import UI (dashboard → **Import Data** → **→ Open importer**). All three
use the **Assets** dataset (`POST /api/imports/assets`), which has no references to other records, so the files need
no prerequisites beyond a running API.

These files have not been imported anywhere. The expected outcomes below are derived from the validator code
(`src/MaintenancePlanner.Application/Imports/ImportRecordReader.cs`,
`src/MaintenancePlanner.Infrastructure/Imports/ReferenceDataImportService.cs`), not from a recorded run.

## Run the demo against a temporary database

A successful import adds records permanently (imports are insert-only and there is no delete). Start the API against
a new, temporary database so `maintenance-planner.db` and any demo database stay untouched:

```powershell
Get-ChildItem env: | Where-Object Name -like 'ConnectionStrings*'      # should list nothing
$demo = "$env:TEMP\mp-fr02-demo"; New-Item -ItemType Directory -Force $demo | Out-Null
$env:ConnectionStrings__MaintenancePlanner = "Data Source=$demo\fr02-demo.db"
$env:ManualImports__Directory = "$demo\imported-manuals"
dotnet run --project src/MaintenancePlanner.Api --launch-profile http
```

On startup the API creates and seeds the temporary database (asset P-101 only). Then run `npm run dev` in
`frontend/`.

## Files

Columns for assets: `id, plantId, name, model, priority`. `id` and `plantId` must be identifiers (a letter or digit
followed by up to 63 letters, digits, `.`, `_` or `-`); `name` and `model` are required text of at most 200
characters; `priority` is a whole number from 1 to 99. The asset IDs here use a `DEMO-FR02-` prefix, which no seed or
fixture uses (the seeds and the PLANT-01 fixture use P-101 to P-105).

### `fr02-valid-assets.csv` — expected to be imported

Two assets, `DEMO-FR02-A01` and `DEMO-FR02-A02`, with every field valid.

Expected: HTTP 200, outcome **Imported**, total records 2, imported records 2, invalid records 0, no issues. Both assets
then appear in `GET /api/assets`.

Importing the same file a second time demonstrates duplicate detection. Expected: HTTP 422, **Rejected: nothing was
saved**, with one issue per row on field `id`: "An asset with ID 'DEMO-FR02-A01' already exists; imports add records
and never change saved ones." (and the same for A02).

### `fr02-invalid-assets.csv` — expected to be rejected as a whole

Five rows. Row 2 is valid, the other four are not; because imports are all-or-nothing, **no** row is saved, not even
row 2. Row numbers are CSV line numbers (the header is line 1).

| Row | Record ID shown | Field | Deliberate problem | Expected message |
|---|---|---|---|---|
| 2 | — | — | none (valid, but not saved) | no issue |
| 3 | DEMO-FR02-B02 | `priority` | `0` is below the range | "priority must be between 1 and 99, but was 0." |
| 4 | DEMO-FR02-B03 | `priority` | `high` is not a number | "priority must be a whole number, but was 'high'." |
| 5 | — (empty) | `id` | `DEMO FR02 B04` contains spaces | "'DEMO FR02 B04' is not a valid identifier. Identifiers must be 1-64 characters: a letter or digit followed by letters, digits, '.', '_' or '-'." |
| 6 | DEMO-FR02-B01 | `id` | same ID as row 2 | "ID 'DEMO-FR02-B01' is also used at row 2." |
| 6 | DEMO-FR02-B01 | `name` | name is empty | "name is required." |

Expected: HTTP 422, outcome **Rejected**, total records 5, imported records 0, invalid records 4 (rows 3–6; row 6 has
two issues), and five issues in the table, ordered by row.

### `fr02-invalid-assets-unknown-column.csv` — expected to be rejected at the header

The header has an extra `location` column, and row 3 also has an invalid `priority` of `0`.

An unknown column stops the import before any row is checked (`ImportRecordReader.cs`, header checks), so only the
header problem is reported; the bad priority on row 3 is **not** listed. This is why the unknown column is in a
separate file from the row errors above.

Expected: HTTP 422, outcome **Rejected**, total records 2, imported records 0, invalid records 0 (the issue concerns
the file, not a record), and one issue with location `file`: "Unknown column 'location'. Expected columns: id,
plantId, name, model, priority."

## Manual UI steps

1. Open the frontend, choose any role, click **→ Open importer** on the **Import Data** tile.
2. Leave **Dataset** on **Assets**.
3. Choose `fr02-invalid-assets.csv`, click **📥 Import**, read the confirmation, click **✓ Confirm import**. Check the
   rejected result and the five issues.
4. Choose `fr02-invalid-assets-unknown-column.csv` and import it the same way. Check the single file-level issue.
5. Choose `fr02-valid-assets.csv` and import it. Check the success result (2 of 2 records).
6. Optionally import `fr02-valid-assets.csv` again to show the duplicate-ID rejection.
7. Optionally open `http://localhost:5083/api/assets` to show P-101 plus the two `DEMO-FR02-` assets, and that none of
   the B or C assets were saved.

Run steps 3–4 before step 5 if you want the rejections shown on an otherwise untouched database.
