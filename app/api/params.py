"""Reusable query-parameter annotations shared by the HTML and JSON routers.

HTML ``<select>`` elements express "no filter" as ``<option value="">``, so the
browser submits ``?state=`` — an *empty string*, not a missing parameter.  A
parameter declared as ``QueueState | None`` rejects that, FastAPI raises
``RequestValidationError`` and the user gets the 422 page instead of the
unfiltered list.

:data:`EmptyAsNone` normalises an empty (or whitespace-only) string to ``None``
*before* validation, so ``?state=``, ``?state`` and an omitted ``state`` all mean
the same thing.  It also protects hand-typed and bookmarked URLs, which is why
it is preferred over giving the "all" options a sentinel value.

Use the ready-made aliases below rather than re-deriving them::

    async def page_queue(state: OptQueueStateQuery = None) -> Response: ...
"""

from __future__ import annotations

from typing import Annotated, Any

from fastapi import Query
from pydantic import BeforeValidator

from app.models import ActivityLevel, AlbumStatus, QueueState

__all__ = [
    "EmptyAsNone",
    "OptActivityLevelQuery",
    "OptAlbumStatusQuery",
    "OptBoolQuery",
    "OptQueueStateQuery",
    "OptStrQuery",
    "empty_to_none",
]


def empty_to_none(value: Any) -> Any:
    """Return ``None`` for an empty/whitespace-only string, else *value*.

    Non-string values (already-parsed enums, booleans, ``None``) pass straight
    through, so the validator is safe to stack on any optional parameter.
    """
    if isinstance(value, str) and not value.strip():
        return None
    return value


#: Pydantic validator that runs before parsing and blanks out empty strings.
EmptyAsNone = BeforeValidator(empty_to_none)

#: ``?state=`` / ``?state=active`` / omitted.
OptQueueStateQuery = Annotated[QueueState | None, EmptyAsNone, Query()]

#: ``?level=`` / ``?level=warning`` / omitted.
OptActivityLevelQuery = Annotated[ActivityLevel | None, EmptyAsNone, Query()]

#: ``?status=`` / ``?status=wanted`` / omitted.
OptAlbumStatusQuery = Annotated[AlbumStatus | None, EmptyAsNone, Query()]

#: ``?monitored=`` / ``?monitored=true`` / omitted.
OptBoolQuery = Annotated[bool | None, EmptyAsNone, Query()]

#: A free-text filter where an empty box means "no filter".
OptStrQuery = Annotated[str | None, EmptyAsNone, Query()]
