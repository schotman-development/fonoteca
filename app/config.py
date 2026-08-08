"""Application configuration.

Every knob Qobuzarr exposes lives here as a field on :class:`Settings`, which is
populated from environment variables and from the project ``.env`` file via
``pydantic-settings``.  Nothing else in the codebase should read ``os.environ``
directly; call :func:`get_settings` instead (it is cached, so it is cheap to
call from request handlers and background workers alike).

Field names map to environment variables case-insensitively, so
``qobuz_min_request_interval`` is set by ``QOBUZ_MIN_REQUEST_INTERVAL``.
See ``.env.example`` for the fully documented set.
"""

from __future__ import annotations

import enum
import logging
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date as _date
from functools import lru_cache
from pathlib import Path
from typing import Any, Callable, Literal, get_args

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

__all__ = [
    "BASE_DIR",
    "FORMAT_IDS",
    "FORMAT_LABELS",
    "KNOWN_ENRICHMENT_SOURCES",
    "LogLevel",
    "OVERRIDABLE_SETTINGS",
    "OverridableSetting",
    "SETTING_ORIGINS",
    "Settings",
    "SettingOrigin",
    "apply_overrides",
    "effective_for",
    "get_effective_settings",
    "get_settings",
    "install_overrides",
    "installed_overrides",
    "overridable_keys",
    "parse_override",
    "reload_settings",
    "reset_overrides",
    "setting_origins",
]

#: A plain stdlib logger: :mod:`app.logging_conf` imports *this* module, so
#: importing it back would be a cycle.
_log = logging.getLogger(__name__)

#: Enrichment rungs this build knows how to construct. Declared here rather than
#: inside :attr:`Settings.enrichment_source_list` so the overlay's validator and
#: the parser answer from the same list.
KNOWN_ENRICHMENT_SOURCES: tuple[str, ...] = (
    "acoustid",
    "deezer",
    "musicbrainz",
    "coverartarchive",
    "wikidata",
)

#: Repository root (the directory that contains ``app/``).
BASE_DIR: Path = Path(__file__).resolve().parent.parent

#: Qobuz stream ``format_id`` values, worst to best.
FORMAT_IDS: tuple[int, ...] = (5, 6, 7, 27)

#: Human labels for the format ids above, used for folder/file naming.
FORMAT_LABELS: dict[int, str] = {
    5: "MP3 320",
    6: "FLAC 16bit 44.1kHz",
    7: "FLAC 24bit 96kHz",
    27: "FLAC 24bit 192kHz",
}


class LogLevel(str, enum.Enum):
    """Accepted values for ``LOG_LEVEL``."""

    DEBUG = "DEBUG"
    INFO = "INFO"
    WARNING = "WARNING"
    ERROR = "ERROR"
    CRITICAL = "CRITICAL"


class Settings(BaseSettings):
    """Effective runtime configuration.

    Instances are immutable in practice — mutate the ``.env`` file and restart,
    or call :func:`reload_settings` in tests.
    """

    model_config = SettingsConfigDict(
        env_file=str(BASE_DIR / ".env"),
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore",
    )

    # ------------------------------------------------------------------ Qobuz
    qobuz_app_id: str = Field(default="", description="Qobuz web-player app id.")
    qobuz_user_auth_token: str = Field(
        default="", description="Auth token for the user's own paid Qobuz account."
    )
    qobuz_app_secret: str | None = Field(
        default=None,
        description="Optional override; derived from the web bundle when absent.",
    )
    qobuz_api_base: str = "https://www.qobuz.com/api.json/0.2/"
    qobuz_user_agent: str = (
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/124.0.0.0 Safari/537.36"
    )
    qobuz_favorite_sync: bool = False
    qobuz_request_timeout: float = 30.0
    download_timeout: float = 300.0

    # ----------------------------------------------------------- Rate limiting
    qobuz_min_request_interval: float = 2.0
    qobuz_max_requests_per_hour: int = 1200
    indexer_artist_interval: int = 300
    indexer_full_sweep_hours: int = 6
    download_track_delay: float = 3.0
    download_concurrency: int = 1

    # -------------------------------------------------- Backoff / breaker
    indexer_jitter_pct: float = 0.25
    backoff_initial: float = 5.0
    backoff_max: float = 600.0
    backoff_multiplier: float = 2.0
    backoff_jitter: float = 0.3
    request_max_retries: int = 5
    circuit_breaker_threshold: int = 3
    circuit_breaker_window: int = 300
    circuit_breaker_cooldown: int = 1800

    # ---------------------------------------------------------------- Indexer
    indexer_enabled: bool = True
    indexer_page_size: int = 50
    indexer_max_pages: int = 20

    # Whether following a new artist immediately imports its back catalogue.
    # This is read-only (no downloads) and fully rate-limited, but it is still
    # many API calls, so it can be turned off. When off, the artist is simply
    # picked up by the next scheduled indexer tick.
    auto_index_on_follow: bool = True

    # -------------------------------------------------------------- Downloads
    # Master opt-in for automatic downloading. When False (the default), the
    # indexer still discovers releases and marks them ``wanted``, but nothing is
    # ever queued without an explicit action from the user — via the per-album
    # "Download" button, "Download wanted" on an artist, or the JSON API.
    # Set AUTO_DOWNLOAD=true to restore hands-off *arr-style behaviour.
    auto_download: bool = False

    download_max_attempts: int = 3
    download_retry_delay: float = 60.0
    download_chunk_size: int = 1024 * 1024
    download_embed_cover: bool = True
    download_write_cover_file: bool = True

    # ------------------------------------------------------------------ Paths
    library_path: Path = BASE_DIR / "music"
    data_path: Path = BASE_DIR / "data"

    # Where deleted and superseded files are moved to. Nothing in Qobuzarr
    # unlinks from ``library_path``: it moves here, and stays until you empty
    # the trash. Put this on the same filesystem as ``library_path`` if you can
    # — then a delete is a rename rather than a copy of the whole album.
    trash_path: Path | None = None

    # Whether a completed upgrade may move the copy it superseded to the trash
    # by itself. The upgrade was an explicit press, so the cleanup is not a
    # surprise; turn it off to inspect both copies before losing either.
    upgrade_cleanup: bool = True

    # -------------------------------------------------------- Library scanning
    # The disk scan reads ``library_path`` and marks albums it finds there as
    # downloaded, so Qobuzarr stops wanting music the user already owns. It is
    # purely local — no Qobuz API calls — and never writes to the filesystem,
    # which is why it is safe to leave on the nightly job.
    library_scan_nightly: bool = True

    # Whether the nightly job also *follows* the artists the scan found on disk
    # but nobody follows yet. This is the one nightly step that ADDS work rather
    # than reducing it — every other pass can only ever mark something present,
    # reverse itself, or delete a list that rebuilds. It makes Qobuz calls (one
    # search per unfollowed artist, through the same limiter as everything else),
    # follows only on an exact name match or an exact barcode match through the
    # audio route, and downloads nothing: AUTO_DOWNLOAD still governs that, and
    # the new artists are indexed one per tick rather than all at 03:17.
    library_follow_nightly: bool = False

    # Fraction of an album's Qobuz track count that must be present on disk
    # before the album counts as complete. 1.0 means every track. Lower it if
    # your rips routinely differ from the Qobuz edition; anything below 1.0
    # risks calling a half-present album finished.
    library_scan_complete_ratio: float = 1.0

    # Whether the walk descends into symlinked directories. Off by default: a
    # link that points at one of its own ancestors would loop forever.
    library_scan_follow_symlinks: bool = False

    # -------------------------------------------------------------- Integrity
    # Whether a file is checked against the hash recorded for it before anything
    # downstream believes what the database says about it. Everything Qobuzarr
    # knows about a track — the recording it is, the release it belongs to, the
    # fingerprint verdict — is a claim about *a file*, and a tag editor, a redone
    # rip or a half-finished copy invalidates it silently. See
    # :mod:`app.core.integrity` for what the check costs and why it is three
    # states rather than two.
    integrity_enabled: bool = True

    # Fraction of the library re-hashed on each nightly run, regardless of
    # whether its size/mtime tripwire fired. The tripwire is what makes the check
    # affordable — one stat per file instead of one full read — but it is not
    # evidence: a tool that rewrites a file and restores its mtime slips past it,
    # and so does a filesystem with coarse timestamps. This bounds how long such
    # a file can hide, at a price that is known in advance rather than discovered
    # at 3am. The default, 1/30, verifies everything once a month and hashes
    # about a thirtieth of the library per night (roughly 200 ms per file).
    integrity_reverify_fraction: float = 1 / 30

    # ------------------------------------------------------------- Enrichment
    # Metadata from open sources: AcoustID, Deezer, MusicBrainz, the Cover Art
    # Archive and Wikidata. All read-only lookups — enrichment never downloads
    # audio and never queues anything, and the single exception that writes
    # anywhere upstream, ``acoustid_submit``, is off by default.
    #
    # Turning this off stops every outbound request to a third party. What leaves
    # the machine when it is on: artist names and release barcodes, plus (for
    # fingerprinting) an acoustic hash of files already on disk — never the files
    # themselves, never anything about the Qobuz account.
    enrichment_enabled: bool = True

    # The ladder, in the order it runs. Any rung can be dropped.
    #
    # AcoustID is first because the audio is the anchor. Every other input to
    # identification is something somebody typed: a folder name, a tag another
    # program wrote, a barcode hand-entered into a catalogue. The user's own
    # library can have all of those wrong and routinely does — that is why it
    # needs identifying at all. The one thing it cannot have mistagged is the
    # waveform, so the ladder starts from the audio and everything below it
    # corroborates what the audio already said. A rung that runs after AcoustID
    # is checking a hypothesis; one that runs before it is inventing one.
    #
    # MusicBrainz now runs *before* Deezer, and that is a reversal. The old order
    # was justified by "MusicBrainz can only be matched exactly, exact matching
    # needs a barcode, and Deezer is where most barcodes come from" — true while
    # the only other barcode was Qobuz's ``upc``. AcoustID changed the premise: it
    # writes ``mb_release_mbid`` straight onto the album, which is a stronger key
    # than a barcode and one MusicBrainz owns outright, so the rung that was
    # supposed to be starved is the one arriving with a key in hand.
    #
    # Measured on a real library of 422 releases: MusicBrainz resolved 253 of
    # them off AcoustID's answer (107 on a pinned release, 146 on a release
    # group) while Deezer, running first, recorded ``no_key`` — "no barcode on
    # this release and no Deezer artist to browse" — on 197 of those *same*
    # albums. The identification was already sitting in the row; the rung that
    # needed it simply ran a tick too early to see it.
    #
    # What the swap gives up is real and worth naming: Deezer genuinely supplies
    # barcodes MusicBrainz then matches on (41 of that library's 117 learned
    # barcodes carry ``barcode_source='deezer'``), and for a release AcoustID
    # could not pin, Deezer-first is still the better order. Order alone cannot
    # satisfy both, because it is one sequence for two populations. What makes
    # the loss temporary rather than permanent is the barcode pass in
    # :meth:`app.core.enricher.Enricher._rearm_stranded`: a rung that ran too
    # early and said "no barcode" is re-opened once any later rung learns one, so
    # the ladder converges on the same answer from either direction and the order
    # decides how many ticks it takes, not what is reachable.
    enrichment_sources: str = "acoustid,musicbrainz,deezer,coverartarchive,wikidata"

    # Identify the library folders the disk scan could not name, by their audio,
    # on a nightly pass. See app/core/binder.py.
    #
    # This is on by default and that is safe because it self-gates: the route
    # needs the AcoustID and MusicBrainz rungs, an ACOUSTID_API_KEY, a contact
    # and fpcalc, and without any of them it does nothing at all rather than
    # failing. It also only ever looks at folders that are *unmatched* — a
    # shrinking set, ~100 on a 611-folder library — and skips every folder
    # already bound or marked, so a steady state costs nothing.
    #
    # It exists as a job rather than only a command because the alternative is
    # worse in a specific way: the scan re-runs its name match on every folder
    # every night and cannot ever succeed on these, so without something that
    # changes the *function* rather than the inputs, a release sitting on disk
    # under a name Qobuz spells differently stays in the wanted list for good.
    folder_binding_enabled: bool = True
    # Folders identified per nightly pass. Each costs a fingerprint of every file
    # in it plus a handful of upstream requests, so this is a budget rather than
    # a page size — the work is not urgent and the set barely changes day to day.
    folder_binding_batch: int = 25

    # An email address or URL identifying this installation. MusicBrainz policy
    # requires a User-Agent naming the application and a contact, and it blocks
    # generic browser strings — so the MusicBrainz rung refuses to run while this
    # is empty rather than falling back to ``qobuz_user_agent``, which is a
    # Chrome spoof. Everything else keeps working.
    enrichment_contact: str = ""

    # AcoustID application key, free from https://acoustid.org/new-application.
    # The one source here that needs registering; without it fingerprinting is
    # skipped and match confirmation falls back to the metadata gates alone.
    acoustid_api_key: str = ""

    # Whether fingerprints that matched nothing are submitted back to AcoustID.
    # Off, and it must stay off by default: a submission is a *write* to a public
    # database, made under this installation's own API key, and it is not
    # retractable by the person who sent it. Everything else enrichment does is a
    # read that leaves no trace upstream. A wrong identification submitted here
    # becomes somebody else's wrong identification tomorrow, which is the exact
    # failure "exact or nothing" exists to prevent — so contributing is an
    # explicit decision by the account holder, never a default.
    acoustid_submit: bool = False

    # Chromaprint's ``fpcalc`` binary. Left empty, it is looked up on PATH.
    fpcalc_path: str = ""

    # Fraction of the non-null source opinions that must agree before a value is
    # written over one Qobuz supplied. Above 0.5 means a real majority; at or
    # below it, two sources out of four could overwrite the other two.
    enrichment_consensus_threshold: float = 0.51

    # There is deliberately NO setting for the identification coverage rule —
    # the fraction of a folder's files a candidate release must explain. It is
    # ``app.enrich.coverage.MIN_COVERAGE``, a constant, and that is the whole
    # argument: coverage is a *requirement*, and the measured gap it separates is
    # 15 files out of 15 against 1 out of 15. A knob invites tuning until the
    # answer comes out, which is what turns a requirement into a score — and a
    # score is exactly what "exact or nothing" refuses to be. If it ever needs
    # adjusting, the honest fix is better evidence, not a lower bar.

    # Whether a majority verdict may rewrite ``Album.release_type``. This is the
    # only Qobuz-owned column enrichment ever touches, because Qobuz genuinely
    # guesses it (anything under four tracks becomes a single). It changes the
    # type and never the status, so it can neither create nor destroy a wanted
    # release.
    enrichment_apply_release_type: bool = True

    # Seconds between enrichment ticks, and how many entities one tick may take.
    # Unlike the indexer this is not pacing a paid account — these are public
    # APIs with published allowances — so the limit that matters is the per-source
    # interval below, not the tick rate.
    enrichment_interval: int = 300
    enrichment_batch_size: int = 25

    # Wall-clock seconds a tick may spend before it yields, leaving the rest for
    # the next one. Keeps a slow upstream from overlapping the following tick.
    enrichment_tick_budget: float = 240.0

    # How long a successful match stays fresh before it is looked at again.
    enrichment_refresh_days: int = 90

    # Consecutive ticks where every request failed before the job pauses itself
    # for the rest of the process. Stops an offline machine writing one activity
    # row per album, which would bury everything else in the feed.
    enrichment_failure_cutoff: int = 5

    # Whether the cover shown in the UI prefers Cover Art Archive/Deezer artwork
    # over Qobuz's. Off by default: Qobuz's image matches the exact release it
    # sells, so the others are better used to fill the gaps.
    enrichment_prefer_external_cover: bool = False

    # Whether identifying a release also writes the ids into the files on disk.
    # On by default, because enrichment scoped to the library arrives strictly
    # after the download loop has already tagged the files, and metadata that
    # never leaves the database is metadata no music player will ever read.
    # Only releases this program downloaded are re-tagged; an album adopted from
    # disk keeps whatever tags it arrived with and gets its NFO only.
    enrichment_write_back: bool = True

    # Per-source pacing. Deezer publishes ~50 requests per 5 seconds; MusicBrainz
    # asks for roughly one per second and enforces it with HTTP 503.
    deezer_min_request_interval: float = 0.25
    deezer_max_requests_per_hour: int = 5000
    musicbrainz_min_request_interval: float = 1.1
    musicbrainz_max_requests_per_hour: int = 3000
    acoustid_min_request_interval: float = 0.34
    acoustid_max_requests_per_hour: int = 3000
    coverart_min_request_interval: float = 0.5
    coverart_max_requests_per_hour: int = 3000
    wikidata_min_request_interval: float = 0.5
    wikidata_max_requests_per_hour: int = 3000

    # ------------------------------------------------------------------- NFO
    # Kodi/Jellyfin-style ``artist.nfo`` and ``album.nfo`` written next to the
    # music. Off means no .nfo is ever created or touched.
    nfo_enabled: bool = True

    # ---------------------------------------------------------------- Quality
    default_format_id: int = 27

    # --------------------------------------------------------------- Naming
    naming_template: str = (
        "{artist}/{album} ({year})[ [{quality}]]/{disc_prefix}{track:02d} - {title}.{ext}"
    )
    max_path_component_length: int = 180
    path_replacement_char: str = "_"

    # ------------------------------------------------- Per-artist defaults
    default_monitor_mode: str = "all"
    default_quality_profile: str = "default"
    default_accepted_release_types: str = "album,ep"

    # ------------------------------------------------------------------- Web
    host: str = "127.0.0.1"
    port: int = 8000
    reload: bool = False

    # --------------------------------------------------------------- Logging
    log_level: LogLevel = LogLevel.INFO
    log_file_name: str = "qobuzarr.log"
    log_max_bytes: int = 5 * 1024 * 1024
    log_backup_count: int = 5
    log_redact_secrets: bool = True

    # ------------------------------------------------------------ validators
    @field_validator("qobuz_api_base")
    @classmethod
    def _ensure_trailing_slash(cls, value: str) -> str:
        """The client joins relative endpoints onto this, so it must end in '/'."""
        return value if value.endswith("/") else value + "/"

    @field_validator("library_path", "data_path")
    @classmethod
    def _expand_path(cls, value: Path) -> Path:
        """Expand ``~`` and make the path absolute relative to the repo root."""
        expanded = Path(value).expanduser()
        return expanded if expanded.is_absolute() else (BASE_DIR / expanded).resolve()

    @field_validator("default_format_id")
    @classmethod
    def _known_format(cls, value: int) -> int:
        if value not in FORMAT_IDS:
            raise ValueError(
                f"DEFAULT_FORMAT_ID must be one of {sorted(FORMAT_IDS)}, got {value!r}"
            )
        return value

    @field_validator("log_level", mode="before")
    @classmethod
    def _upper_log_level(cls, value: object) -> object:
        return value.upper() if isinstance(value, str) else value

    @field_validator("indexer_jitter_pct", "backoff_jitter")
    @classmethod
    def _sane_jitter(cls, value: float) -> float:
        return min(max(value, 0.0), 1.0)

    @field_validator("download_concurrency")
    @classmethod
    def _at_least_one(cls, value: int) -> int:
        return max(1, value)

    @field_validator("library_scan_complete_ratio")
    @classmethod
    def _sane_ratio(cls, value: float) -> float:
        """Keep the completeness threshold inside ``(0, 1]``.

        Zero would make every folder containing a single file "complete", which
        would mark whole albums downloaded on the strength of one track.
        """
        return min(max(value, 0.01), 1.0)

    @field_validator("enrichment_consensus_threshold")
    @classmethod
    def _sane_consensus(cls, value: float) -> float:
        """Keep the consensus threshold above a bare tie and at or below unanimity.

        Anything at or below 0.5 is not a majority: with four sources, two could
        outvote the other two and overwrite Qobuz on a coin flip.
        """
        return min(max(value, 0.51), 1.0)

    @field_validator("integrity_reverify_fraction")
    @classmethod
    def _sane_reverify_fraction(cls, value: float) -> float:
        """Keep the nightly re-verification share inside ``(0, 1]``.

        The floor is one 365th — once a year — rather than zero, because zero is
        not a fraction of the library, it is "never", and the setting that means
        never is ``INTEGRITY_ENABLED=false``. A zero here is far more likely a
        typo, and it would silently retire the only check that catches a file
        whose mtime was restored after it was rewritten.
        """
        return min(max(value, 1 / 365), 1.0)

    @field_validator(
        "deezer_min_request_interval",
        "musicbrainz_min_request_interval",
        "acoustid_min_request_interval",
        "coverart_min_request_interval",
        "wikidata_min_request_interval",
    )
    @classmethod
    def _non_negative_interval(cls, value: float) -> float:
        return max(0.0, value)

    @field_validator("enrichment_batch_size", "enrichment_refresh_days")
    @classmethod
    def _positive(cls, value: int) -> int:
        return max(1, value)

    # ------------------------------------------------------- derived helpers
    @property
    def db_path(self) -> Path:
        """Absolute path of the SQLite database file."""
        return self.data_path / "qobuzarr.db"

    @property
    def database_url(self) -> str:
        """SQLAlchemy async URL for the SQLite database."""
        return f"sqlite+aiosqlite:///{self.db_path}"

    @property
    def log_path(self) -> Path:
        """Absolute path of the rotating log file."""
        return self.data_path / self.log_file_name

    @property
    def secret_cache_path(self) -> Path:
        """Where a successfully validated app secret is cached."""
        return self.data_path / "secret.cache"

    @property
    def cache_path(self) -> Path:
        """Scratch directory for partial downloads and cached artwork."""
        return self.data_path / "cache"

    @property
    def trash_dir(self) -> Path:
        """Where deleted library files are moved to.

        ``TRASH_PATH`` when set, else ``data/trash``. Always resolved, because
        :mod:`app.core.librarian` proves containment by prefix and a relative
        path would make that check meaningless.
        """
        configured = self.trash_path
        if configured is None:
            return self.data_path / "trash"
        expanded = Path(configured).expanduser()
        return expanded if expanded.is_absolute() else (BASE_DIR / expanded).resolve()

    @property
    def accepted_release_type_defaults(self) -> list[str]:
        """``default_accepted_release_types`` parsed into a clean list."""
        return [
            part.strip().lower()
            for part in self.default_accepted_release_types.split(",")
            if part.strip()
        ]

    @property
    def has_credentials(self) -> bool:
        """True when both required Qobuz credentials are present."""
        return bool(self.qobuz_app_id and self.qobuz_user_auth_token)

    @property
    def indexer_interval_seconds(self) -> int:
        """Alias kept for readability in scheduler code."""
        return self.indexer_artist_interval

    @property
    def enrichment_source_list(self) -> list[str]:
        """``enrichment_sources`` parsed into an ordered, de-duplicated list.

        Order is meaningful: it is the order the rungs run in, and each one can
        feed the next. Unknown names are dropped rather than raising, so a typo
        disables one source instead of refusing to start the application.

        The overlay (:data:`OVERRIDABLE_SETTINGS`) is deliberately *stricter*
        than this: a name typed into the environment is a file nobody validated,
        while a name arriving through ``PATCH /api/settings`` came from a control
        that knows the vocabulary, so there it is a 400 rather than a silent drop.
        """
        known = set(KNOWN_ENRICHMENT_SOURCES)
        seen: list[str] = []
        for part in self.enrichment_sources.split(","):
            name = part.strip().lower()
            if name in known and name not in seen:
                seen.append(name)
        return seen

    def enrichment_source_enabled(self, source: str) -> bool:
        """True when *source* is switched on and enrichment is enabled at all."""
        return self.enrichment_enabled and source in self.enrichment_source_list

    @property
    def enrichment_user_agent(self) -> str:
        """The User-Agent enrichment sources see.

        MusicBrainz requires an application name, a version and a contact, and
        actively blocks generic browser strings — so this is built from
        ``ENRICHMENT_CONTACT`` and is deliberately *not* ``qobuz_user_agent``,
        which impersonates Chrome. Sending that to musicbrainz.org would be both
        rude and against their policy.
        """
        from app import __version__  # noqa: PLC0415 - avoids a circular import

        contact = self.enrichment_contact.strip()
        if not contact:
            return f"Qobuzarr/{__version__}"
        return f"Qobuzarr/{__version__} ( {contact} )"

    @property
    def musicbrainz_ready(self) -> bool:
        """True when the MusicBrainz rung is allowed to make requests.

        Without a contact the rung is *gated*, not broken: it records that state
        and every other source carries on. Guessing a contact address would put a
        stranger's email in someone else's request logs.
        """
        return bool(self.enrichment_contact.strip())

    @property
    def acoustid_ready(self) -> bool:
        """True when AcoustID lookups are possible (a free key is registered)."""
        return bool(self.acoustid_api_key.strip())

    @property
    def acoustid_submit_ready(self) -> bool:
        """True when unmatched fingerprints may be submitted back to AcoustID.

        Both halves are required and neither implies the other. The key is what
        a submission is made *under*, so without one there is nothing to post
        with; the switch is the account holder having agreed to write to a public
        database at all. Keeping the pair in one property means no caller has to
        remember that "we have a key" is not the same as "we may contribute".
        """
        return self.acoustid_submit and self.acoustid_ready

    def format_label(self, format_id: int | None) -> str:
        """Return the human label for *format_id* (empty string when unknown)."""
        if format_id is None:
            return ""
        return FORMAT_LABELS.get(int(format_id), f"format {format_id}")

    def ensure_directories(self) -> None:
        """Create the data, cache and library directories if they are missing."""
        for path in (self.data_path, self.cache_path, self.library_path):
            path.mkdir(parents=True, exist_ok=True)

    def require_credentials(self) -> None:
        """Raise a clear error when the Qobuz credentials are not configured."""
        if not self.has_credentials:
            raise RuntimeError(
                "QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN must be set in .env "
                "before Qobuzarr can talk to the Qobuz API."
            )


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """Return the process-wide :class:`Settings` singleton."""
    return Settings()


def reload_settings() -> Settings:
    """Clear the cache and re-read the environment. Intended for tests.

    Also drops the settings overlay, because an overlay computed from the *old*
    environment is exactly the stale answer a reload is trying to get rid of.
    """
    reset_overrides()
    get_settings.cache_clear()
    return get_settings()


# ---------------------------------------------------------------------------
# The settings overlay
# ---------------------------------------------------------------------------
# ``Settings`` is read from the environment, and that stays true: it is the only
# thing that works before the database exists, and it is what every script, the
# CLI and the test suite construct. What the settings screen needs on top of it
# is a *narrow* way for a person to change seven specific values from inside the
# application, and have that survive a restart.
#
# The shape is deliberately small:
#
# * a row in ``app_setting`` per overridden key (see
#   :mod:`app.core.settings_store` — the only reader and writer of that table);
# * an **allowlist**, :data:`OVERRIDABLE_SETTINGS`, declared here and nowhere
#   else. A key outside it is refused with a 400 rather than quietly ignored,
#   because "the switch did nothing and said nothing" is the failure a settings
#   screen must never have. Nothing that is a credential, a path or a rate-limit
#   figure is on it, and adding one is a deliberate edit to this list;
# * **the database wins over the environment** — the user pressed the switch more
#   recently than they edited a file — but the origin of every overridable value
#   is reported (:func:`setting_origins`) so the screen can say *which*, and
#   offer a reset that deletes the row rather than writing the env value back;
# * **one accessor**: :func:`get_effective_settings` (or :func:`effective_for`
#   when the caller already holds a base). ``get_settings()`` is unchanged and
#   still env-only. Nothing anywhere writes ``if override else`` and nothing
#   mutates the cached singleton in place — an overlay produces a *new*
#   ``Settings`` object, and the values a long-lived component captured are
#   re-pointed by :meth:`app.core.state.AppState.apply_setting_overrides`.

SettingOrigin = Literal["env", "override"]

#: The same two words as a sequence, published through ``/api/meta`` alongside
#: every other vocabulary the server decides. A ``Literal`` is invisible at
#: runtime — it produces no OpenAPI enum, because ``SettingsOut.origins`` is
#: declared ``dict[str, str]`` — so without this the client's only copy of the
#: pair would be a hand-typed union, which is the one thing ``MetaOut`` exists
#: to prevent.
SETTING_ORIGINS: tuple[str, ...] = get_args(SettingOrigin)


@dataclass(frozen=True, slots=True)
class OverridableSetting:
    """One allowlisted key: how to parse a submitted value and how to store it.

    ``parse`` turns whatever the client sent into the value ``Settings`` would
    hold, raising :class:`ValueError` with a sentence a person can act on;
    ``dump`` turns that back into the ``TEXT`` the row stores. The pair is what
    makes a round trip through the database lossless.
    """

    key: str
    kind: Literal["bool", "text", "csv"]
    parse: Callable[[Any], Any]
    dump: Callable[[Any], str]
    summary: str


def _parse_bool(value: Any) -> bool:
    """Accept a real bool, or the strings a form and a JSON client actually send."""
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)) and value in (0, 1):
        return bool(value)
    if isinstance(value, str):
        text = value.strip().lower()
        if text in {"1", "true", "yes", "on"}:
            return True
        if text in {"0", "false", "no", "off"}:
            return False
    raise ValueError("expects true or false")


def _parse_sources(value: Any) -> str:
    """Validate an enrichment ladder and return it in stored (CSV) form.

    Accepts a list (which is what an ordered ▲▼ control produces) or a comma
    separated string. Order is preserved because order is the ladder, and
    duplicates collapse. An unknown name is refused rather than dropped: see
    :attr:`Settings.enrichment_source_list` for why the two differ.
    """
    if isinstance(value, str):
        parts = [part.strip().lower() for part in value.split(",")]
    elif isinstance(value, (list, tuple)):
        parts = [str(part).strip().lower() for part in value]
    else:
        raise ValueError("expects a list of source names, or a comma-separated string")

    seen: list[str] = []
    for name in parts:
        if not name:
            continue
        if name not in KNOWN_ENRICHMENT_SOURCES:
            raise ValueError(
                f"{name!r} is not an enrichment source; known sources are "
                + ", ".join(KNOWN_ENRICHMENT_SOURCES)
            )
        if name not in seen:
            seen.append(name)
    return ",".join(seen)


#: A fabricated release used only to prove a naming template renders. It is not
#: the settings screen's preview (that lives in ``app.api.deps.naming_preview``
#: and is what the *user* is shown); this one exists so a template that cannot
#: render is refused at the moment it is submitted rather than at 3am inside a
#: re-file.
_TEMPLATE_PROBE_ARTIST: dict[str, Any] = {"id": "0", "name": "Floating Points"}
_TEMPLATE_PROBE_ALBUM: dict[str, Any] = {
    "id": "preview",
    "title": "Promises",
    "version": None,
    "release_date": _date(2021, 3, 26),
    "label": "Luaka Bop",
    "genre": "Jazz",
    "media_count": 1,
    "tracks_count": 9,
    "hires": True,
    "max_bit_depth": 24,
    "max_sampling_rate": 96.0,
    "artist": _TEMPLATE_PROBE_ARTIST,
}
_TEMPLATE_PROBE_TRACKS: tuple[dict[str, Any], ...] = (
    {"title": "Movement 1", "track_number": 1, "media_number": 1},
    {"title": "Movement 2", "track_number": 2, "media_number": 1},
)


def _parse_template(value: Any) -> str:
    """Validate a naming template by rendering it, and return it unchanged.

    ``Settings`` itself does not validate this, and for the environment that is
    tolerable: the settings page shows an empty preview and says so. A *write* is
    different — it would be accepted with a green toast and then discovered
    during a thousand-file re-file — so the one place that can refuse it is here.

    What is actually checked is **not** "does it parse". :mod:`app.core.naming`
    sanitises rather than raises: an unknown token becomes ``Unknown``, an
    unbalanced brace becomes a literal, a leading ``/`` and a ``..`` are stripped.
    That is the right behaviour for a path renderer and it means a syntax check
    here would refuse almost nothing. The property worth having is the one whose
    absence destroys data: **two different tracks of one release must render to
    two different paths.** A template with no ``{title}`` and no ``{track}`` —
    or one whose only distinguishing token is misspelt — maps every track of an
    album onto one filename, and the download loop then writes each track over
    the last, quietly, leaving a nine-track album as one file.
    """
    if not isinstance(value, str):
        raise ValueError("expects a template string")
    template = value.strip()
    if not template:
        raise ValueError("must not be empty")

    from app.core.naming import render_track_path  # noqa: PLC0415 - cycle at import

    probe = get_settings().model_copy(update={"naming_template": template})
    try:
        rendered = [
            str(
                render_track_path(
                    track,
                    _TEMPLATE_PROBE_ALBUM,
                    probe,
                    artist=_TEMPLATE_PROBE_ARTIST,
                    bit_depth=24,
                    sampling_rate=96.0,
                    ext="flac",
                )
            )
            for track in _TEMPLATE_PROBE_TRACKS
        ]
    except Exception as exc:  # noqa: BLE001 - any failure is the same refusal
        raise ValueError(f"cannot be rendered ({exc})") from exc

    if not all(path.strip() for path in rendered):
        raise ValueError("renders to an empty path")
    if len(set(rendered)) < len(rendered):
        raise ValueError(
            "gives every track of a release the same path "
            f"({rendered[0]}), which would overwrite them one by one — "
            "include {title} or {track} in the file name"
        )
    return template


def _identity(value: Any) -> str:
    return str(value)


#: **The allowlist.** Exactly what ``PATCH /api/settings`` may write, in the
#: order the settings screen shows it. Everything absent from this mapping is
#: environment-only, and asking to change one is an error rather than a no-op.
OVERRIDABLE_SETTINGS: Mapping[str, OverridableSetting] = {
    setting.key: setting
    for setting in (
        OverridableSetting(
            key="naming_template",
            kind="text",
            parse=_parse_template,
            dump=_identity,
            summary="Where a track is filed, relative to the library root.",
        ),
        OverridableSetting(
            key="enrichment_sources",
            kind="csv",
            parse=_parse_sources,
            dump=_identity,
            summary="The enrichment ladder, in the order the rungs run.",
        ),
        OverridableSetting(
            key="upgrade_cleanup",
            kind="bool",
            parse=_parse_bool,
            dump=lambda value: "true" if value else "false",
            summary="Whether a complete upgrade trashes the copy it superseded.",
        ),
        OverridableSetting(
            key="library_scan_nightly",
            kind="bool",
            parse=_parse_bool,
            dump=lambda value: "true" if value else "false",
            summary="Whether the nightly job scans the library folder.",
        ),
        OverridableSetting(
            key="integrity_enabled",
            kind="bool",
            parse=_parse_bool,
            dump=lambda value: "true" if value else "false",
            summary="Whether files are checked against the hash recorded for them.",
        ),
        OverridableSetting(
            key="enrichment_write_back",
            kind="bool",
            parse=_parse_bool,
            dump=lambda value: "true" if value else "false",
            summary="Whether an identification is written into the files on disk.",
        ),
        OverridableSetting(
            key="nfo_enabled",
            kind="bool",
            parse=_parse_bool,
            dump=lambda value: "true" if value else "false",
            summary="Whether artist.nfo / album.nfo are written next to the music.",
        ),
    )
}


def overridable_keys() -> tuple[str, ...]:
    """The allowlist, in display order."""
    return tuple(OVERRIDABLE_SETTINGS)


def parse_override(key: str, value: Any) -> tuple[Any, str]:
    """Validate one submitted override.

    Returns ``(typed value, stored text)``.

    Raises:
        KeyError: *key* is not on the allowlist. The caller turns this into a
            400 — never a silent no-op, which is a switch that lies.
        ValueError: the value is not one this key can hold. The message is a
            fragment ("expects true or false"), so the caller can prefix it with
            the key and produce one readable sentence.
    """
    spec = OVERRIDABLE_SETTINGS.get(key)
    if spec is None:
        raise KeyError(key)
    typed = spec.parse(value)
    return typed, spec.dump(typed)


# The installed overlay. ``_overrides`` is what the table holds, ``_applied`` is
# the subset that actually reached ``Settings``, and the last two are a memo so
# the common case — ``effective_for(get_settings())``, called once per librarian
# operation — does not rebuild a ``Settings`` object every time.
#
# The two sets differ only when a stored row no longer parses, and keeping them
# apart is the honest answer to "where did this value come from?". Reporting a
# skipped row as ``"override"`` would say the live value was chosen on the
# settings screen when it demonstrably came from ``.env`` — which is the one
# thing :func:`setting_origins` exists to be right about.
_overrides: dict[str, str] = {}
_applied: frozenset[str] = frozenset()
_effective: Settings | None = None
_effective_base: Settings | None = None


def _usable_overrides(overrides: Mapping[str, str]) -> dict[str, Any]:
    """The subset of *overrides* that parses, as typed values.

    A stored value that no longer parses (the allowlist shrank, or somebody
    hand-edited the table) is logged and skipped rather than raised: an
    unreadable row must not stop the application starting.
    """
    values: dict[str, Any] = {}
    for key, raw in overrides.items():
        try:
            typed, _ = parse_override(key, raw)
        except KeyError:
            _log.warning("Ignoring stored override for unknown setting %r", key)
            continue
        except ValueError as exc:
            _log.warning("Ignoring unusable stored override %s=%r: %s", key, raw, exc)
            continue
        values[key] = typed
    return values


def apply_overrides(base: Settings, overrides: Mapping[str, str]) -> Settings:
    """Return a **new** ``Settings``: *base* with *overrides* laid over it.

    Pure, and it never mutates *base*. Unusable rows are skipped — see
    :func:`_usable_overrides`.

    ``model_copy`` rather than a re-validation pass is correct **for this
    allowlist**: none of the seven keys has a field validator on ``Settings``, and
    each one has already been through :func:`parse_override`. A future key that
    does have one has to be routed through that validator here.
    """
    values = _usable_overrides(overrides)
    return base.model_copy(update=values) if values else base


def install_overrides(
    overrides: Mapping[str, str], *, base: Settings | None = None
) -> Settings:
    """Make *overrides* the process-wide overlay and return the effective settings.

    Called once at startup (:func:`app.core.state.init_state`) and again on every
    successful ``PATCH /api/settings``. It replaces the whole set rather than
    merging, because the caller has just read the table and the table is the
    truth — a key deleted there has to disappear here.
    """
    global _overrides, _applied, _effective, _effective_base

    resolved = base if base is not None else get_settings()
    _overrides = {str(key): str(value) for key, value in overrides.items()}
    values = _usable_overrides(_overrides)
    _applied = frozenset(values)
    _effective_base = resolved
    _effective = resolved.model_copy(update=values) if values else resolved
    return _effective


def reset_overrides() -> None:
    """Drop the overlay. Startup, shutdown and tests; nothing else needs it."""
    global _overrides, _applied, _effective, _effective_base

    _overrides = {}
    _applied = frozenset()
    _effective = None
    _effective_base = None


def installed_overrides() -> dict[str, str]:
    """The overlay as stored — ``key -> text``. A copy; nobody mutates ours.

    This is every row that was handed to :func:`install_overrides`, including one
    whose value could not be parsed. :func:`setting_origins` is the narrower
    question — which of them is actually being *read*.
    """
    return dict(_overrides)


def setting_origins() -> dict[str, SettingOrigin]:
    """``key -> "env" | "override"`` for every allowlisted key.

    Every key is present, always: "this one is still coming from ``.env``" is the
    answer the screen needs to render a reset affordance honestly, and an absent
    key would make the client guess.

    ``"override"`` means the live value came from the overlay, not merely that a
    row exists for it. A row this build cannot parse is reported as ``"env"``,
    because that is where the value being used came from — the row is inert, and
    saying otherwise would put an "overridden" badge next to the environment's
    own value.
    """
    origins: dict[str, SettingOrigin] = {}
    for key in OVERRIDABLE_SETTINGS:
        origins[key] = "override" if key in _applied else "env"
    return origins


def effective_for(base: Settings) -> Settings:
    """Apply the installed overlay to *base*.

    This — not :func:`get_settings` — is what a caller reads when it wants the
    value a person last chose. It takes a base so a caller that already has one
    (a test's scratch settings, a dependency the test monkeypatched) keeps it;
    with no overlay installed it returns *base* itself, unchanged and un-copied,
    which is why routing a call site through it can never change behaviour on its
    own.
    """
    if not _applied:
        return base
    if base is _effective_base and _effective is not None:
        return _effective
    return apply_overrides(base, _overrides)


def get_effective_settings() -> Settings:
    """The process-wide effective settings: the environment plus the overlay.

    Use this wherever a value a person can change has to be read *live*. Use
    :func:`get_settings` where the environment is what is meant — before the
    database exists, in the CLI, and for anything the overlay cannot reach.

    Defined as the overlay over *the current* environment rather than as a
    remembered object, so clearing the ``get_settings`` cache (which is how the
    environment changes at all) cannot leave this answering from the old one.
    """
    return effective_for(get_settings())
