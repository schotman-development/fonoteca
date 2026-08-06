"""Fonoteca — a Lidarr-style release monitor and downloader backed by the Qobuz API.

The package is laid out as:

``app.config``
    Environment-driven settings (pydantic-settings).
``app.db``
    Async SQLAlchemy engine, session factory and schema bootstrap.
``app.models``
    SQLAlchemy 2.0 ORM models and the enums used across the app.
``app.schemas``
    Pydantic response models for the JSON API under ``/api/*``.
``app.logging_conf``
    Logging setup with secret redaction.
``app.qobuz``
    Qobuz API client, request signing and app-secret derivation.
``app.core``
    Rate limiter, indexer, download queue, tagging and library organisation.
``app.api``
    FastAPI routers for the HTML UI and the JSON API.
"""

__all__ = ["__version__"]

__version__ = "0.1.0"
