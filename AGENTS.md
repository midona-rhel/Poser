# Agent rules

- Documentation is required for **durable concepts and non-obvious
  invariants** — product boundaries, runtime ordering, identity/gesture
  contracts, compatibility conventions. Do NOT create a document per class,
  interface, service, or entity; member tables, constructor lists, and
  implementation flow belong to source and search. Before adding a concept,
  consult `docs/README.md` and extend the existing normative home; create a
  new file only when no home fits, and keep it around 10–40 lines.
- One normative home per contract; other documents link instead of
  restating. Delete superseded prose — Git preserves history.
- Consult Brio for native/backend behavior and Ktisis for posing
  interaction (Brio: robust backend, horrid UI; Ktisis: the reverse). Keep a
  reference citation in docs only when it explains an intentional
  compatibility decision.
- Unsafe offsets, native ordering, and surprising math are explained in
  tight comments beside the code, not in documentation essays.
