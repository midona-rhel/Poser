# Domain tests and build-source follow-up

All 18 Domain test sources and 18 script/build sources plus six root-reviewed scripts/configuration
files (24 total) were read in full. This pass changed comments only.

## Supported consolidation

- Put project-reference and namespace-import rules enforced by the boundary
  script in docs/architecture/product-and-boundaries.md. Link to the script;
  do not duplicate its project matrix in prose.
- Keep IK chain discovery, solver eligibility, and anchor behavior in
  docs/features/expression-gaze-and-ik.md.
- Keep structural pose matching and import-layer behavior in
  docs/features/pose-operations.md; keep group transform frames and identity
  thresholds in docs/features/selection-and-transforms.md.
- Keep scene snapshot equality and outcome/remedy rules in
  docs/features/scenes.md.
- Keep Release validation, TRX reporting, and Debug-only bridge/deployment
  rules in docs/process/testing.md.

## Concrete implementation order

1. Compare each home above with the enforced behaviors recorded in the CSVs.
2. Add only missing durable rules to the existing home in plain language.
3. Replace duplicate prose elsewhere with links and delete superseded text.
4. Run the boundary script and Domain tests in Release. Do not use a Debug
   build for documentation work.

No additional documents, guards, feature behavior, or test matrices are
supported by this audit.
