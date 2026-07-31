"""HTTP layer.

``app.api.routes_ui`` serves the HTML pages and HTMX fragments, ``app.api.routes_api``
serves the JSON API under ``/api`` plus ``/health``, and ``app.api.deps`` holds the
shared template plumbing, runtime-singleton accessors and read-model builders.

Both routers are included by :mod:`main`; importing this package deliberately pulls
in nothing, so ``app.api.deps`` stays cheap to import from scripts.
"""
