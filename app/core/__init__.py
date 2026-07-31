"""Domain services.

``naming``     path/template rendering and POSIX sanitising (pure functions)
``tagger``     synchronous mutagen tagging (call via ``asyncio.to_thread``)
``downloader`` album download orchestration (streams, tags, moves into place)
``indexer``    the slow, one-artist-at-a-time release indexer
``queue``      the sequential download-queue worker
``scheduler``  APScheduler jobs (indexer tick, nightly housekeeping)
``state``      the process-wide :class:`~app.core.state.AppState` singleton

Nothing in this package imports :mod:`app.api`, so every service stays usable from
``cli.py`` and from tests. This module intentionally re-exports nothing to keep
import cost (and import cycles) at zero.
"""
