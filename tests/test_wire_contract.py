"""Cross-producer equality between ``app/schemas.py`` and the SPA's wire types.

``web/src/api/types.ts`` is a hand-kept mirror of the Pydantic response models —
there is no code generation, deliberately, because a generator's output is not
the place to write down that ``AlbumOut.complete`` being ``None`` means *nothing
has counted this release yet*.  The cost of hand-keeping it is that the two
halves can drift, and drift here is invisible in both directions:

* a field the server **gained** is a field the client never renders;
* a field the server **dropped** is ``undefined`` reaching a component that the
  type checker promised would get a value — which surfaces as a blank cell, or
  as ``NaN``, three layers from the change that caused it.

So this walks the live OpenAPI schema and asserts every response model's field
names round-trip.  It checks *names*, not types: TypeScript narrows several
fields the server declares loosely (``AlbumOut.release_type`` is ``str`` on the
wire and the ``ReleaseType`` union in TypeScript, which is correct because every
writer goes through :func:`app.models.normalize_release_type`), and encoding that
judgement here would just be a third place to keep in step.

The whole module skips when ``web/src/api/types.ts`` is absent, so a checkout
without the front end still runs green.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

import main

TYPES_TS = Path(__file__).resolve().parent.parent / "web" / "src" / "api" / "types.ts"

pytestmark = pytest.mark.skipif(
    not TYPES_TS.is_file(), reason="the SPA's wire types are not in this checkout"
)

#: Schemas with no TypeScript counterpart, each for a stated reason.
#:
#: The four enums are TypeScript *type aliases* rather than interfaces, so the
#: interface parser below cannot see them; they are covered by
#: :func:`test_enum_vocabularies_match` instead.  FastAPI's own validation
#: envelope is modelled as ``ValidationErrorItem``/``ErrorEnvelope``, which are
#: shaped by ``main.register_exception_handlers`` rather than by a Pydantic model.
_NOT_INTERFACES = frozenset(
    {
        "ActivityLevel",
        "AlbumStatus",
        "MonitorMode",
        "QueueState",
        "TrackOrigin",
        "TrackStatus",
        "HTTPValidationError",
        "ValidationError",
    }
)


def _typescript_interfaces() -> dict[str, dict[str, str]]:
    """Parse ``types.ts`` into ``{interface: {field: declared type}}``.

    Comments are stripped first: several field docs contain braces and colons,
    and a doc comment mentioning ``{q: undefined}`` would otherwise read as a
    field.  ``extends`` is resolved so an inherited ``PageOut`` counts as part of
    the child's surface, which is exactly how FastAPI flattens it on the wire.
    """
    source = TYPES_TS.read_text(encoding="utf-8")
    source = re.sub(r"/\*.*?\*/", "", source, flags=re.S)
    source = re.sub(r"//.*", "", source)

    own: dict[str, dict[str, str]] = {}
    parents: dict[str, list[str]] = {}
    pattern = re.compile(
        r"export interface (\w+)(?:\s+extends\s+([\w, ]+))?\s*\{(.*?)\n\}", re.S
    )
    for match in pattern.finditer(source):
        name, extends, body = match.group(1), match.group(2), match.group(3)
        own[name] = {
            field.group(1): field.group(3).rstrip().rstrip(";")
            for field in re.finditer(
                r"^\s{2}([a-zA-Z_]\w*)(\??):\s*(.+?)$", body, re.M
            )
        }
        parents[name] = [part.strip() for part in extends.split(",")] if extends else []

    def flatten(name: str, seen: frozenset[str] = frozenset()) -> dict[str, str]:
        if name in seen or name not in own:
            return {}
        fields: dict[str, str] = {}
        for parent in parents[name]:
            fields.update(flatten(parent, seen | {name}))
        fields.update(own[name])
        return fields

    return {name: flatten(name) for name in own}


@pytest.fixture(name="schemas", scope="module")
def schemas_fixture() -> dict[str, dict]:
    """Every component schema the running application publishes."""
    return main.app.openapi()["components"]["schemas"]


@pytest.fixture(name="interfaces", scope="module")
def interfaces_fixture() -> dict[str, dict[str, str]]:
    return _typescript_interfaces()


def test_the_parser_actually_found_something(
    interfaces: dict[str, dict[str, str]],
) -> None:
    """Guard the guard.

    Every assertion below iterates a parsed structure, so a regex that quietly
    matched nothing would make the whole module pass while checking nothing at
    all — the one way a contract test can be worse than no contract test.
    """
    assert len(interfaces) > 50
    assert "AlbumOut" in interfaces
    assert "complete" in interfaces["AlbumOut"]


def test_every_schema_has_a_typescript_interface(
    schemas: dict[str, dict], interfaces: dict[str, dict[str, str]]
) -> None:
    """A response model the client has never heard of is a screen that cannot exist."""
    missing = sorted(set(schemas) - set(interfaces) - _NOT_INTERFACES)
    assert missing == [], f"no TypeScript interface for: {missing}"


def test_no_field_is_missing_from_typescript(
    schemas: dict[str, dict], interfaces: dict[str, dict[str, str]]
) -> None:
    """The server never sends a field the client has no name for."""
    gaps: list[str] = []
    for name, schema in schemas.items():
        fields = interfaces.get(name)
        if fields is None:
            continue
        for key in schema.get("properties", {}):
            if key not in fields:
                gaps.append(f"{name}.{key}")
    assert gaps == [], f"present on the wire, absent from types.ts: {sorted(gaps)}"


def test_typescript_invents_no_field(
    schemas: dict[str, dict], interfaces: dict[str, dict[str, str]]
) -> None:
    """And the client never expects a field the server does not send.

    This is the direction that reads as a bug somewhere else: the type checker
    promises a value, the key is absent, and a component renders ``undefined``.
    """
    invented: list[str] = []
    for name, fields in interfaces.items():
        schema = schemas.get(name)
        if schema is None:
            continue
        properties = schema.get("properties", {})
        for key in fields:
            if key not in properties:
                invented.append(f"{name}.{key}")
    assert invented == [], f"declared in types.ts, never sent: {sorted(invented)}"


def test_enum_vocabularies_match(schemas: dict[str, dict]) -> None:
    """The closed sets are closed on both sides, and in the same order-free set.

    These reach the client as ``<select>`` options and label-map keys, so a value
    the union omits is an option that silently disappears rather than an error.
    """
    source = TYPES_TS.read_text(encoding="utf-8")

    def union(alias: str) -> set[str]:
        match = re.search(rf"export type {alias} =\s*((?:[^\n]|\n\s*\|)*)", source)
        assert match is not None, f"no `export type {alias}` in types.ts"
        return set(re.findall(r"'([^']+)'", match.group(1)))

    for alias in ("MonitorMode", "AlbumStatus", "TrackStatus", "TrackOrigin", "QueueState", "ActivityLevel"):
        assert union(alias) == set(schemas[alias]["enum"]), f"{alias} drifted"


def test_wire_enums_not_published_as_components_still_match() -> None:
    """The vocabularies ``/api/meta`` publishes rather than the schema does.

    ``ReleaseType``, ``EnrichmentSource``, ``EnrichmentEntity``, the enrichment
    states, the integrity states and the fingerprint states are all serialised as
    bare ``str``, so they never become an OpenAPI enum — they reach the client
    through ``MetaOut`` at runtime. That makes them exactly the ones a stale
    union would break silently.

    ``SettingOrigin`` is here for a sharper version of the same reason: it is a
    ``Literal`` rather than an enum, and ``SettingsOut.origins`` is declared
    ``dict[str, str]``, so nothing about it reaches the OpenAPI document at all.
    Without this assertion the client's copy of the pair would be the one union
    in ``types.ts`` that no test can see drift in.
    """
    from app.api.deps import build_meta  # noqa: PLC0415 - import cost, not a cycle

    source = TYPES_TS.read_text(encoding="utf-8")

    def union(alias: str) -> set[str]:
        match = re.search(rf"export type {alias} =\s*((?:[^\n]|\n\s*\|)*)", source)
        assert match is not None, f"no `export type {alias}` in types.ts"
        return set(re.findall(r"'([^']+)'", match.group(1)))

    meta = build_meta()
    assert union("ReleaseType") == set(meta.release_types)
    assert union("EnrichmentSource") == set(meta.enrichment_sources)
    assert union("EnrichmentEntity") == set(meta.enrichment_entities)
    assert union("EnrichmentStateValue") == set(meta.enrichment_states)
    assert union("IntegrityState") == set(meta.integrity_states)
    assert union("FingerprintState") == set(meta.fingerprint_states)
    assert union("SettingOrigin") == set(meta.setting_origins)

    # ``review_states`` is a rule rather than a vocabulary, so it has no union
    # of its own — it is a *subset* of one, and the thing worth pinning is that
    # the server never publishes a review state the client has never heard of.
    assert set(meta.review_states) <= set(meta.enrichment_states)


def test_every_declared_vocabulary_actually_reaches_the_client() -> None:
    """A vocabulary in ``_VOCABULARIES`` with no ``MetaOut`` field is dropped.

    ``build_meta()`` splats ``_VOCABULARIES`` into ``MetaOut(**...)``, and
    ``MetaOut`` takes pydantic's default ``extra="ignore"``. So the guarantee
    that file's docstring makes — "a value added to an enum shows up here
    without anybody remembering" — holds for a new *value* and **not** for a new
    *vocabulary*: declare one and forget the field, and it is discarded in
    silence, at the one seam whose entire purpose is that nothing is
    hand-copied into TypeScript. There is no exception worth allowing, because
    a list nobody publishes has no reason to be in that mapping either.

    Asserted rather than fixed with ``extra="forbid"`` on purpose: the model is
    a *response*, and hardening it would turn a developer's omission into a 500
    on ``/api/meta`` — which takes the whole client down, since every screen
    waits on that payload. A failing test names the missing field instead.
    """
    from app.api.deps import _VOCABULARIES, build_meta  # noqa: PLC0415

    published = build_meta().model_dump()
    missing = sorted(name for name in _VOCABULARIES if name not in published)
    assert not missing, (
        f"declared in deps._VOCABULARIES but absent from MetaOut, so silently "
        f"dropped from /api/meta: {missing}. Add the field to app/schemas.py "
        f"and mirror it in web/src/api/types.ts."
    )
