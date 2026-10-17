"""Generate deterministic FSRS-6 replay vectors using the pinned Python oracle."""

import hashlib
import json
import math
import random
from collections import Counter
from datetime import datetime, timedelta, timezone
from importlib.metadata import version
from pathlib import Path

from fsrs import Card, Rating, Scheduler, State


ORACLE_VERSION = "6.3.2"
SEED = 20261018
SEQUENCE_COUNT = 500
BASE_TIME = datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
OUTPUT = Path(__file__).resolve().parents[2] / "Backend/Palabras.Tests/Srs/golden.json"


def utc_seconds(value: datetime) -> str:
    """Reject fractional seconds rather than silently truncate oracle results."""
    assert value.tzinfo == timezone.utc and value.microsecond == 0
    return value.isoformat(timespec="seconds").replace("+00:00", "Z")


def memory(card: Card) -> dict:
    def number(value: float | None) -> float | None:
        if value is None:
            return None
        assert math.isfinite(value)
        return round(value, 12)

    return {
        "state": card.state.value,
        "step": card.step,
        "stability": number(card.stability),
        "difficulty": number(card.difficulty),
        "due": utc_seconds(card.due),
        "last_review": utc_seconds(card.last_review) if card.last_review else None,
    }


def generate() -> dict:
    if version("fsrs") != ORACLE_VERSION:
        raise RuntimeError(f"Expected fsrs=={ORACLE_VERSION}; found {version('fsrs')}")

    rng = random.Random(SEED)
    scheduler = Scheduler(
        desired_retention=0.90,
        maximum_interval=730,
        enable_fuzzing=False,
        learning_steps=(timedelta(minutes=1), timedelta(minutes=10)),
        relearning_steps=(timedelta(minutes=10),),
    )
    sequences = []
    for sequence_id in range(SEQUENCE_COUNT):
        reviewed_at = BASE_TIME + timedelta(days=sequence_id)
        # Supplying id and due avoids Card's wall-clock defaults. py-fsrs has no New enum.
        card = Card(card_id=sequence_id + 1, state=State.Learning, step=0, due=reviewed_at)
        initial = memory(card)
        steps = []
        for step_index in range(rng.randint(12, 40)):
            gap_kind = "initial"
            if step_index:
                # Cycle categories to guarantee mixed coverage in every sequence;
                # sample actual gaps and ratings using the seeded PRNG.
                gap_kind = ("same_day", "whole_day", "fractional_day", "due")[
                    (step_index + sequence_id) % 4
                ]
                if gap_kind == "same_day":
                    gap = timedelta(seconds=rng.choice((0, 1, 60, 330, 600, 3600, 86399)))
                elif gap_kind == "whole_day":
                    gap = timedelta(days=rng.choice((1, 2, 7, 30, 180, 730, 1000)))
                elif gap_kind == "fractional_day":
                    gap = timedelta(days=rng.choice((0, 1, 2, 7, 30)), seconds=rng.randint(1, 86399))
                else:
                    gap = card.due - reviewed_at
                reviewed_at += gap

            rating = Rating(rng.randint(1, 4))
            before = card
            before_memory = memory(before)
            card, review_log = scheduler.review_card(card, rating, review_datetime=reviewed_at)
            assert review_log.review_datetime == reviewed_at
            assert card.last_review == reviewed_at
            assert card.due > reviewed_at
            assert card.due - reviewed_at <= timedelta(days=730)
            assert 1 <= card.difficulty <= 10 and card.stability > 0
            assert (card.step is None) == (card.state == State.Review)
            assert before is not card and memory(before) == before_memory
            steps.append({
                "rating": rating.value,
                "review_time": utc_seconds(reviewed_at),
                "gap_kind": gap_kind,
                "expected": memory(card),
            })
        sequences.append({"id": sequence_id + 1, "initial": initial, "steps": steps})

    states = {step["expected"]["state"] for seq in sequences for step in seq["steps"]}
    ratings = {step["rating"] for seq in sequences for step in seq["steps"]}
    assert states == {state.value for state in State}
    assert ratings == {rating.value for rating in Rating}
    return {
        "schema_version": 1,
        "oracle": {"package": "fsrs", "version": ORACLE_VERSION},
        "seed": SEED,
        "sequence_count": SEQUENCE_COUNT,
        "options": {
            "desired_retention": 0.90,
            "maximum_interval_days": 730,
            "enable_fuzzing": False,
            "learning_steps_seconds": [60, 600],
            "relearning_steps_seconds": [600],
            "parameters": list(scheduler.parameters),
        },
        "states": {state.name: state.value for state in State},
        "ratings": {rating.name: rating.value for rating in Rating},
        "sequences": sequences,
    }


def main() -> None:
    vectors = generate()
    # Python 3.12's JSON encoder uses deterministic shortest float representations.
    # Round only serialized values, never the card passed back into the oracle.
    encoded = (json.dumps(vectors, sort_keys=True, indent=2, allow_nan=False) + "\n").encode("utf-8")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(encoded)
    steps = [step for seq in vectors["sequences"] for step in seq["steps"]]
    print(f"Wrote {OUTPUT}: {len(vectors['sequences'])} sequences, {len(steps)} reviews")
    print(f"Gap coverage: {dict(sorted(Counter(step['gap_kind'] for step in steps).items()))}")
    print(f"State coverage: {dict(sorted(Counter(step['expected']['state'] for step in steps).items()))}")
    print(f"Rating coverage: {dict(sorted(Counter(step['rating'] for step in steps).items()))}")
    print(f"SHA256: {hashlib.sha256(encoded).hexdigest()}")


if __name__ == "__main__":
    main()
