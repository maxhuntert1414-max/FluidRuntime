# MEMORY - FluidLink v2

- Representation: `project_memory-fluidlink-v2.json`.
- Assignment: preserve architecture decisions, handoffs, resolved findings, and
  next promotion criteria.
- Edited source files: no.

The durable boundary is contract-first sharing between repositories while the
native hook/control ABI remains separate. Delta and generic shared memory stay
deferred until measured traffic and correctness gates justify them.
