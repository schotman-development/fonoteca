/**
 * The smart playlist builder's rules, pure so they run under `node --test`.
 *
 * The twin of `SmartRules.cs`: a field's kind here must be `KindOf`'s there,
 * or the builder offers an operator the server refuses. The field and
 * operator names come from the generated client, so one the server drops
 * stops this compiling; a kind that drifts is caught only by the 400.
 */

import type { components } from '@fonoteca/api-client'

export type SmartField = NonNullable<components['schemas']['SmartField']>
export type SmartOperator = NonNullable<components['schemas']['SmartOperator']>
export type SmartMatch = NonNullable<components['schemas']['SmartMatch']>
export type SmartSort = components['schemas']['SmartSort']
export type SmartRule = components['schemas']['SmartRule']
export type SmartCriteria = components['schemas']['SmartCriteria']

export type FieldKind = 'text' | 'number' | 'flag' | 'date'

type FieldInfo = {
  readonly label: string
  readonly kind: FieldKind
  readonly group: (typeof FIELD_GROUPS)[number]
  /** What a number is counted in, shown beside the box. */
  readonly unit?: string
}

export const FIELD_GROUPS = ['Song', 'People', 'Album', 'Artist', 'File'] as const

/** Every field the server answers, in the order the builder lists them. */
export const FIELDS = {
  Title: { label: 'Title', kind: 'text', group: 'Song' },
  Artist: { label: 'Artist', kind: 'text', group: 'Song' },
  Work: { label: 'Work', kind: 'text', group: 'Song' },
  Genre: { label: 'Genre', kind: 'text', group: 'Song' },
  Isrc: { label: 'ISRC', kind: 'text', group: 'Song' },
  Length: { label: 'Length', kind: 'number', group: 'Song', unit: 'seconds' },
  Composer: { label: 'Composer', kind: 'text', group: 'People' },
  Lyricist: { label: 'Lyricist', kind: 'text', group: 'People' },
  Conductor: { label: 'Conductor', kind: 'text', group: 'People' },
  Performer: { label: 'Performer', kind: 'text', group: 'People' },
  Arranger: { label: 'Arranger', kind: 'text', group: 'People' },
  Producer: { label: 'Producer', kind: 'text', group: 'People' },
  Engineer: { label: 'Engineer', kind: 'text', group: 'People' },
  Credited: { label: 'Anyone credited', kind: 'text', group: 'People' },
  Album: { label: 'Album', kind: 'text', group: 'Album' },
  AlbumArtist: { label: 'Album artist', kind: 'text', group: 'Album' },
  AlbumType: { label: 'Album type', kind: 'text', group: 'Album' },
  Year: { label: 'Year', kind: 'number', group: 'Album' },
  PressingYear: { label: 'Pressing year', kind: 'number', group: 'Album' },
  Label: { label: 'Label', kind: 'text', group: 'Album' },
  ReleaseCountry: { label: 'Release country', kind: 'text', group: 'Album' },
  MediumFormat: { label: 'Medium', kind: 'text', group: 'Album' },
  Followed: { label: 'Followed', kind: 'flag', group: 'Artist' },
  ArtistCountry: { label: 'Artist country', kind: 'text', group: 'Artist' },
  ArtistType: { label: 'Artist type', kind: 'text', group: 'Artist' },
  Lossless: { label: 'Lossless', kind: 'flag', group: 'File' },
  HiRes: { label: 'Hi-res', kind: 'flag', group: 'File' },
  Codec: { label: 'Codec', kind: 'text', group: 'File' },
  BitDepth: { label: 'Bit depth', kind: 'number', group: 'File', unit: 'bits' },
  SampleRate: { label: 'Sample rate', kind: 'number', group: 'File', unit: 'kHz' },
  Bitrate: { label: 'Bitrate', kind: 'number', group: 'File', unit: 'kbps' },
  Channels: { label: 'Channels', kind: 'number', group: 'File' },
  DynamicRange: { label: 'Dynamic range', kind: 'number', group: 'File', unit: 'DR' },
  Loudness: { label: 'Loudness', kind: 'number', group: 'File', unit: 'LUFS' },
  Damaged: { label: 'Damaged', kind: 'flag', group: 'File' },
  Added: { label: 'Added', kind: 'date', group: 'File', unit: 'days' },
  Path: { label: 'Path', kind: 'text', group: 'File' },
} as const satisfies Record<SmartField, FieldInfo>

/** What each kind can be asked, most useful first; the first is a new condition's. */
export const OPERATORS: Record<FieldKind, ReadonlyArray<readonly [SmartOperator, string]>> = {
  text: [
    ['Contains', 'contains'],
    ['DoesNotContain', 'does not contain'],
    ['Is', 'is'],
    ['IsNot', 'is not'],
    ['StartsWith', 'starts with'],
    ['EndsWith', 'ends with'],
    ['IsEmpty', 'is unknown'],
    ['IsNotEmpty', 'is known'],
  ],
  number: [
    ['AtLeast', 'is at least'],
    ['AtMost', 'is at most'],
    ['Between', 'is between'],
    ['Is', 'is'],
    ['IsNot', 'is not'],
    ['IsEmpty', 'is unknown'],
    ['IsNotEmpty', 'is known'],
  ],
  flag: [
    ['IsTrue', 'yes'],
    ['IsFalse', 'no'],
  ],
  date: [
    ['InTheLast', 'in the last'],
    ['NotInTheLast', 'not in the last'],
  ],
}

export const SORTS: ReadonlyArray<readonly [SmartSort, string]> = [
  ['Added', 'Recently added'],
  ['Title', 'Title'],
  ['Artist', 'Artist'],
  ['Album', 'Album'],
  ['Year', 'Year'],
  ['Length', 'Length'],
  ['Random', 'Shuffled'],
]

/** How many values an operator takes: none, one, or a range's two. */
export function valuesFor(op: SmartOperator): 0 | 1 | 2 {
  if (op === 'Between') return 2
  if (op === 'IsEmpty' || op === 'IsNotEmpty' || op === 'IsTrue' || op === 'IsFalse') return 0
  return 1
}

export function isGroup(rule: SmartRule): boolean {
  return rule.rules != null
}

/** A new condition on this field, asking its kind's first question. */
export function condition(field: SmartField = 'Artist'): SmartRule {
  const kind = FIELDS[field].kind
  const operator = OPERATORS[kind][0]?.[0] ?? null
  return {
    match: null,
    rules: null,
    field,
    operator,
    value: kind === 'date' ? '30' : null,
    to: null,
  }
}

export function group(match: SmartMatch = 'All', rules: SmartRule[] = [condition()]): SmartRule {
  return { match, rules, field: null, operator: null, value: null, to: null }
}

export function newCriteria(): SmartCriteria {
  return { rules: group(), sort: 'Added', reverse: false, limit: null }
}

/** The condition on another field; its question kept where the new field's kind can ask it. */
export function withField(rule: SmartRule, field: SmartField): SmartRule {
  const kind = FIELDS[field].kind
  const kept = rule.field != null && FIELDS[rule.field].kind === kind
  return kept ? { ...rule, field } : condition(field)
}

export function withOperator(rule: SmartRule, operator: SmartOperator): SmartRule {
  const count = valuesFor(operator)
  return {
    ...rule,
    operator,
    value: count === 0 ? null : rule.value,
    to: count === 2 ? rule.to : null,
  }
}

/** The group with its rule at `index` replaced, or taken out where `rule` is null. */
export function withChild(parent: SmartRule, index: number, rule: SmartRule | null): SmartRule {
  const rules = parent.rules ?? []
  return {
    ...parent,
    rules:
      rule == null
        ? rules.filter((_, at) => at !== index)
        : rules.map((child, at) => (at === index ? rule : child)),
  }
}

export function withAppended(parent: SmartRule, rule: SmartRule): SmartRule {
  return { ...parent, rules: [...(parent.rules ?? []), rule] }
}

/**
 * Whether every condition has what its question needs, so the rules are
 * worth sending. The server has the last word; this only keeps a half-typed
 * number from asking it.
 */
export function complete(rule: SmartRule): boolean {
  if (rule.rules != null) return rule.rules.every(complete)
  if (rule.field == null || rule.operator == null) return false

  const count = valuesFor(rule.operator)
  if (count === 0) return true

  const kind = FIELDS[rule.field].kind
  const filled = (value: string | null) =>
    value != null &&
    value.trim() !== '' &&
    (kind === 'text' || Number.isFinite(Number(value.trim())))

  return filled(rule.value) && (count === 1 || filled(rule.to))
}
