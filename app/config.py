"""Application configuration.

Every knob Fonoteca exposes lives here as a field on :class:`Settings`, which is
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
from functools import lru_cache
from pathlib import Path

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

__all__ = [
    "BASE_DIR",
    "FORMAT_IDS",
    "FORMAT_LABELS",
    "LogLevel",
    "Settings",
    "get_settings",
    "reload_settings",
]

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

    # Where deleted and superseded files are moved to. Nothing in Fonoteca
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
    # downloaded, so Fonoteca stops wanting music the user already owns. It is
    # purely local — no Qobuz API calls — and never writes to the filesystem,
    # which is why it is safe to leave on the nightly job.
    library_scan_nightly: bool = True

    # Fraction of an album's Qobuz track count that must be present on disk
    # before the album counts as complete. 1.0 means every track. Lower it if
    # your rips routinely differ from the Qobuz edition; anything below 1.0
    # risks calling a half-present album finished.
    library_scan_complete_ratio: float = 1.0

    # Whether the walk descends into symlinked directories. Off by default: a
    # link that points at one of its own ancestors would loop forever.
    library_scan_follow_symlinks: bool = False

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
    log_file_name: str = "fonoteca.log"
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

    # ------------------------------------------------------- derived helpers
    @property
    def db_path(self) -> Path:
        """Absolute path of the SQLite database file."""
        return self.data_path / "fonoteca.db"

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
                "before Fonoteca can talk to the Qobuz API."
            )


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """Return the process-wide :class:`Settings` singleton."""
    return Settings()


def reload_settings() -> Settings:
    """Clear the cache and re-read the environment. Intended for tests."""
    get_settings.cache_clear()
    return get_settings()
