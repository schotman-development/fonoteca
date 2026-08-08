/**
 * Formatter tests. Every function is checked on its `null`/`undefined` path,
 * because that is the path the domain rules live on: `null` means *nothing has
 * measured this*, which must never render as `0`, as `false` or as a full bar.
 */

import { describe, expect, it } from 'vitest'

import { EM_DASH } from '@/format/constants'
import {
  ALBUM_STATUS_LABELS,
  ENRICHMENT_STATE_LABELS,
  INTEGRITY_STATE_LABELS,
  MONITOR_MODE_LABELS,
  enumLabel,
  humanise,
} from '@/format/labels'
import {
  fmtCount,
  fmtOfTotal,
  fmtPercent,
  fmtRatioPercent,
  fmtSize,
  fmtTracksOnDisk,
} from '@/format/number'
import { fmtQuality, fmtTrackQuality, formatLabel } from '@/format/quality'
import {
  fmtAgo,
  fmtDate,
  fmtDateTime,
  fmtDuration,
  fmtSpan,
  fmtUtc,
  secondsSince,
} from '@/format/time'

const NOW = Date.parse('2026-08-02T12:00:00Z')

describe('fmtDateTime / fmtUtc', () => {
  it('renders the em dash for null and undefined', () => {
    expect(fmtDateTime(null)).toBe(EM_DASH)
    expect(fmtDateTime(undefined)).toBe(EM_DASH)
    expect(fmtUtc(null)).toBe(EM_DASH)
  })

  it('renders the em dash for an unparseable string rather than "Invalid Date"', () => {
    expect(fmtDateTime('not a date')).toBe(EM_DASH)
    expect(fmtUtc('')).toBe(EM_DASH)
  })

  it('labels the UTC form and only the UTC form', () => {
    // The old UI put the exact UTC instant in `title=` and rendered local time
    // unlabelled; stating the zone in one place is what makes that safe.
    expect(fmtUtc('2026-08-02T09:07:00Z')).toBe('2026-08-02 09:07 UTC')
    expect(fmtDateTime('2026-08-02T09:07:00Z')).not.toContain('UTC')
  })

  it('applies the browser offset exactly once', () => {
    const iso = '2026-08-02T09:07:00Z'
    const local = new Date(iso)
    expect(fmtDateTime(iso)).toBe(
      `${local.getFullYear()}-${String(local.getMonth() + 1).padStart(2, '0')}-` +
        `${String(local.getDate()).padStart(2, '0')} ` +
        `${String(local.getHours()).padStart(2, '0')}:` +
        `${String(local.getMinutes()).padStart(2, '0')}`,
    )
  })
})

describe('fmtDate', () => {
  it('echoes a bare release date instead of round-tripping it through Date', () => {
    // Parsing "1977-05-13" as UTC midnight and formatting it locally moves it a
    // day west of Greenwich. A date with no time has no zone to convert.
    expect(fmtDate('1977-05-13')).toBe('1977-05-13')
  })

  it('renders the em dash for null, undefined and empty', () => {
    expect(fmtDate(null)).toBe(EM_DASH)
    expect(fmtDate(undefined)).toBe(EM_DASH)
    expect(fmtDate('')).toBe(EM_DASH)
    expect(fmtDate('nonsense')).toBe(EM_DASH)
  })
})

describe('fmtSpan', () => {
  it('renders two significant units with the second zero-padded', () => {
    expect(fmtSpan(45)).toBe('45s')
    expect(fmtSpan(303)).toBe('5m 03s')
    expect(fmtSpan(7500)).toBe('2h 05m')
    expect(fmtSpan(273_600)).toBe('3d 04h')
  })

  it('clamps a negative span to zero rather than printing a minus sign', () => {
    expect(fmtSpan(-5)).toBe('0s')
  })

  it('renders the em dash for null, undefined and NaN', () => {
    expect(fmtSpan(null)).toBe(EM_DASH)
    expect(fmtSpan(undefined)).toBe(EM_DASH)
    expect(fmtSpan(Number.NaN)).toBe(EM_DASH)
  })

  it('renders 0 as 0s, which is a measurement, not a missing value', () => {
    expect(fmtSpan(0)).toBe('0s')
  })
})

describe('fmtAgo', () => {
  it('says "never" for null — a sentence, not a missing value', () => {
    expect(fmtAgo(null, NOW)).toBe('never')
    expect(fmtAgo(undefined, NOW)).toBe('never')
    expect(fmtAgo('garbage', NOW)).toBe('never')
  })

  it('says "just now" for a future stamp instead of "in -3s"', () => {
    // Server and browser clocks disagree routinely; a relative time that runs
    // backwards reads as broken data.
    expect(fmtAgo('2026-08-02T12:00:03Z', NOW)).toBe('just now')
  })

  it('renders elapsed time with the span format', () => {
    expect(fmtAgo('2026-08-02T11:54:57Z', NOW)).toBe('5m 03s ago')
  })
})

describe('secondsSince', () => {
  it('is null for an unset stamp, so callers cannot compare against zero', () => {
    expect(secondsSince(null, NOW)).toBeNull()
    expect(secondsSince('2026-08-02T11:59:00Z', NOW)).toBe(60)
  })
})

describe('fmtDuration', () => {
  it('renders m:ss and h:mm:ss', () => {
    expect(fmtDuration(272)).toBe('4:32')
    expect(fmtDuration(3_925)).toBe('1:05:25')
  })

  it('renders the em dash for null, undefined and zero', () => {
    // The API sends 0 for a duration it does not know; "0:00" would claim the
    // track is silent.
    expect(fmtDuration(null)).toBe(EM_DASH)
    expect(fmtDuration(undefined)).toBe(EM_DASH)
    expect(fmtDuration(0)).toBe(EM_DASH)
    expect(fmtDuration(-4)).toBe(EM_DASH)
  })
})

describe('fmtSize', () => {
  it('uses binary units with one decimal', () => {
    expect(fmtSize(1024)).toBe('1.0 KiB')
    expect(fmtSize(1_503_238_553)).toBe('1.4 GiB')
    expect(fmtSize(999)).toBe('999.0 B')
  })

  it('caps at TiB rather than inventing a unit', () => {
    expect(fmtSize(1024 ** 5)).toBe('1024.0 TiB')
  })

  it('renders the em dash for null, undefined, zero and negative', () => {
    expect(fmtSize(null)).toBe(EM_DASH)
    expect(fmtSize(undefined)).toBe(EM_DASH)
    expect(fmtSize(0)).toBe(EM_DASH)
    expect(fmtSize(-1)).toBe(EM_DASH)
  })
})

describe('fmtPercent / fmtRatioPercent', () => {
  it('renders a fraction the server already computed', () => {
    expect(fmtPercent(0.4237)).toBe('42%')
    expect(fmtPercent(0.4237, 1)).toBe('42.4%')
    expect(fmtPercent(0)).toBe('0%')
  })

  it('renders the em dash for null — a ratio of nothing is not zero', () => {
    expect(fmtPercent(null)).toBe(EM_DASH)
    expect(fmtPercent(undefined)).toBe(EM_DASH)
    expect(fmtPercent(Number.NaN)).toBe(EM_DASH)
  })

  it('refuses to divide by an unknown or zero total', () => {
    expect(fmtRatioPercent(3, 0)).toBe(EM_DASH)
    expect(fmtRatioPercent(3, null)).toBe(EM_DASH)
    expect(fmtRatioPercent(null, 10)).toBe(EM_DASH)
    expect(fmtRatioPercent(3, 12)).toBe('25%')
  })
})

describe('fmtOfTotal / fmtCount', () => {
  it('reports a real gap honestly, including when it is bad news', () => {
    expect(fmtOfTotal(9, 12)).toBe('9 of 12')
  })

  it('is an em dash when nothing has counted the release', () => {
    expect(fmtOfTotal(null, 12)).toBe(EM_DASH)
    expect(fmtOfTotal(undefined, 12)).toBe(EM_DASH)
  })

  it('drops the denominator when the total is unknown', () => {
    expect(fmtOfTotal(9, null)).toBe('9')
  })

  it('pluralises in one place', () => {
    expect(fmtCount(1, 'track')).toBe('1 track')
    expect(fmtCount(3, 'track')).toBe('3 tracks')
    expect(fmtCount(0, 'release')).toBe('0 releases')
    expect(fmtCount(null, 'track')).toBe(EM_DASH)
    expect(fmtCount(undefined, 'track')).toBe(EM_DASH)
  })
})

describe('fmtTracksOnDisk — the meter label', () => {
  it('states both numbers, because a bare percentage is not actionable', () => {
    expect(fmtTracksOnDisk(9, 12)).toBe('9 of 12 tracks on disk')
    expect(fmtTracksOnDisk(1, 1)).toBe('1 of 1 tracks on disk')
  })

  it('explains the adopted-from-disk case rather than saying zero', () => {
    expect(fmtTracksOnDisk(null, 12)).toMatch(/nothing has counted this release/)
    expect(fmtTracksOnDisk(undefined, 12)).toMatch(/nothing has counted this release/)
  })

  it('drops the denominator when the catalogue count is unknown', () => {
    expect(fmtTracksOnDisk(9, null)).toBe('9 tracks on disk')
    expect(fmtTracksOnDisk(1, 0)).toBe('1 track on disk')
  })
})

describe('fmtQuality', () => {
  it('renders depth and rate when both are known', () => {
    expect(fmtQuality({ max_bit_depth: 24, max_sampling_rate: 96, hires: true })).toBe(
      '24bit/96kHz',
    )
    expect(
      fmtQuality({ max_bit_depth: 16, max_sampling_rate: 44.1, hires: false }),
    ).toBe('16bit/44.1kHz')
  })

  it('falls back to Hi-Res rather than inventing a figure', () => {
    // Qobuz reports `hires` for releases whose depth and rate it does not
    // publish; a fabricated 24/96 would end up in a folder name.
    expect(fmtQuality({ hires: true })).toBe('Hi-Res')
    expect(fmtQuality({ max_bit_depth: 24, hires: true })).toBe('Hi-Res')
  })

  it('renders the em dash when nothing is known', () => {
    expect(fmtQuality(null)).toBe(EM_DASH)
    expect(fmtQuality(undefined)).toBe(EM_DASH)
    expect(fmtQuality({})).toBe(EM_DASH)
    expect(fmtQuality({ max_bit_depth: null, max_sampling_rate: null })).toBe(EM_DASH)
  })

  it('reads a track\'s own file rather than the catalogue ceiling', () => {
    expect(fmtTrackQuality({ bit_depth: 16, sampling_rate: 44.1 })).toBe(
      '16bit/44.1kHz',
    )
    expect(fmtTrackQuality({ bit_depth: null, sampling_rate: 44.1 })).toBe(EM_DASH)
  })
})

describe('formatLabel', () => {
  const labels = { '5': 'MP3 320', '6': 'FLAC 16bit 44.1kHz', '7': 'FLAC 24bit 96kHz' }

  it('looks the id up in the map the server published', () => {
    expect(formatLabel(7, labels)).toBe('FLAC 24bit 96kHz')
  })

  it('returns an empty string, not an em dash, for an unknown or absent id', () => {
    // It renders inline inside a sentence ("→ FLAC 24bit 96kHz"), where a dash
    // would read as a missing word.
    expect(formatLabel(null, labels)).toBe('')
    expect(formatLabel(undefined, labels)).toBe('')
    expect(formatLabel(99, labels)).toBe('')
    expect(formatLabel(7, null)).toBe('')
    expect(formatLabel(7, undefined)).toBe('')
  })
})

describe('labels', () => {
  it('calls skipped "Ignored", which is what the button does', () => {
    expect(ALBUM_STATUS_LABELS.skipped).toBe('Ignored')
  })

  it('calls an unmeasured file "Not measured", never tampered with', () => {
    // Every file in an unbaselined library is `unknown`; calling that tampering
    // is what would make the integrity feature cry wolf on day one.
    expect(INTEGRITY_STATE_LABELS.unknown).toBe('Not measured')
  })

  it('names every monitor mode and every enrichment state', () => {
    expect(Object.keys(MONITOR_MODE_LABELS).sort()).toEqual(['all', 'future', 'none'])
    expect(ENRICHMENT_STATE_LABELS.no_key).toBe('Nothing to match on')
  })

  it('humanises a value it has never heard of instead of rendering nothing', () => {
    expect(humanise('no_key')).toBe('No key')
    expect(humanise('some-new-state')).toBe('Some new state')
    expect(humanise('')).toBe('')
  })

  it('prefers the caller\'s map, because vocabularies share words', () => {
    // `failed` is an album status, a queue state and an enrichment state.
    expect(enumLabel('failed', { failed: 'Gave up' })).toBe('Gave up')
    expect(enumLabel('downloading')).toBe('Downloading')
  })

  it('is an empty string for null and undefined, for use inside sentences', () => {
    expect(enumLabel(null)).toBe('')
    expect(enumLabel(undefined)).toBe('')
    expect(enumLabel('')).toBe('')
  })
})
