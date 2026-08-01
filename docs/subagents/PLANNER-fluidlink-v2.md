# PLANNER - FluidLink v2

- Agent: `019fb417-a673-77d2-8a98-bb7b63845b30`
- Assignment: turn the research into a compatibility-safe implementation plan.
- Owned domain: phases, repository boundaries, and release gates.
- Edited files: no.

## Plan

1. Freeze a canonical v2 manifest and cross-language full-frame fixture.
2. Add Python framing, positional payload codecs, and per-connection dispatch.
3. Add a typed .NET package/client while retaining all v1 public types.
4. Compare real v1/v2 sessions for the same semantic flow.
5. Review, fix, run complete gates, publish Gateway first, then Runtime.

Delta and generic shared memory require separate promotion gates rather than
speculative fields in the initial protocol.
