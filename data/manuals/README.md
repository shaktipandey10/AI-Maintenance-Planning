# Synthetic maintenance-planning manuals

`MAN-P101-PLANNING-001.pdf` and `MAN-P102-SCOPE-001.pdf` are synthetic, fictional planning references created for this POC. They are not ABB documents or repair instructions. Their document IDs and versions are supplied explicitly in the API's dependency-injection registration.

PDF text extraction uses PdfPig (`PdfPig` NuGet package, version 0.1.16), a managed .NET PDF library that exposes page-level text extraction without requiring a native runtime. The retriever uses PdfPig's content-order text extractor and reports page numbers as one-based PDF page numbers. It returns only text found in extracted page content; section headings are retained only when the explicit `Section:` marker can be identified. Scanned/image-only PDFs with no extractable text are rejected. No page citation is inferred for the existing TXT manual.

These fixed fixtures verify page-aware extraction and a scope distinction: the P-102 inspection-only reference explicitly must not be substituted for the P-101 bearing-replacement scope. This does not constitute general PDF ingestion or satisfy the assignment's full data-import requirement.
