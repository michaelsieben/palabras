# FSRS golden vectors

Oracle: **py-fsrs 6.3.2**, not ts-fsrs. Python 3.12 generates the tracked
`Backend/Palabras.Tests/Srs/golden.json`; CI replays that file without Python.

## Regenerate

From the repository root, run:

```bash
docker run --rm -v /repos/palabras:/repo -w /repo python:3.12-slim sh -c 'pip install --quiet --disable-pip-version-check fsrs==6.3.2 && python3 tools/fsrs-golden/generate.py'
sha256sum Backend/Palabras.Tests/Srs/golden.json
```

Run both commands twice. Hashes must match. Review changes before staging the
generator and JSON together. The dependency is also pinned in `requirements.txt`.
No timestamp, random card ID, or clock read contributes to the output.

When invoking the Unraid host's Docker daemon from OpenChamber, use the host
checkout path for the bind mount (the daemon cannot see `/repos/palabras`):

```bash
docker run --rm -v /mnt/user/repos/palabras:/repo -w /repo python:3.12-slim sh -c 'pip install --quiet --disable-pip-version-check fsrs==6.3.2 && python3 tools/fsrs-golden/generate.py'
```

On the Unraid host, Docker-written files may need ownership restored:

```bash
docker run --rm --entrypoint chown -v /mnt/user/repos/palabras:/repo alpine/socat -R 99:100 /repo/tools /repo/Backend/Palabras.Tests/Srs
```

## Replay contract

- Seed `20261018`; 500 sequences, each with 12–40 random ratings.
- Fuzz disabled; retention 0.90; maximum interval 730 days; learning steps
  60/600 seconds; relearning step 600 seconds. Default FSRS-6 weights are recorded.
- Each sequence contains an `initial` card and ordered `steps`: `rating`,
  `review_time`, diagnostic `gap_kind`, and `expected` memory after that review.
- Gaps cover same-day (including simultaneous repeats and 23:59:59), whole-day,
  fractional-day, and exactly-due reviews. Long gaps include 1000 days.
- `State.Learning=1`, `State.Review=2`, `State.Relearning=3`. Python has **no New
  state**: a fresh card is Learning/step 0 with null stability/difficulty/last_review.
  The C# adapter must map the application's New state to this oracle input.
- `Rating.Again=1`, `Rating.Hard=2`, `Rating.Good=3`, `Rating.Easy=4`.
- `Scheduler.review_card(card, rating, review_datetime=...)` accepts explicit UTC
  review time and returns `(updated_card, review_log)`, copying rather than mutating
  the input. Always pass that time when replaying offline attempts.
- All serialized instants are UTC ISO 8601 with `Z` and second precision. Due is
  **review time + scheduled interval**, not midnight. Review intervals use whole
  days; learning/relearning use configured seconds (Hard can use 330/900 seconds).
  The oracle itself supports microseconds; these inputs/configuration yield exact
  seconds, asserted before serialization rather than silently truncated.
- py-fsrs uses `(review_time - last_review).days` (floored whole elapsed days) for
  its scheduling branch. Do not replace this with fractional `TotalDays` in C#.
- Keys sorted; UTF-8/LF with terminal newline; finite floats rounded to 12 decimal
  places only at serialization, then Python 3.12's stable JSON float encoding.
  Full-precision oracle cards feed subsequent reviews. Compare S/D within `1e-6`,
  states/steps exactly, and due to the second. `last_review` is also provided.

Generation asserts timestamp precision, interval limits, valid memory, and coverage
of every oracle state and rating. S5 adds the C# golden replay test; this tool does
not install or implement the C# scheduler.
