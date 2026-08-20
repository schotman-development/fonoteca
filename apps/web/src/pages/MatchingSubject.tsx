import {
  Badge,
  Button,
  CandidateCard,
  CandidateList,
  type CandidateOption,
  type CandidateOptionProps,
  CandidateSearch,
  type CandidateSelection,
  EvidencePanel,
  FitSummary,
  filterCandidates,
  PlayButton,
  type PlaybackTrack,
  type SlotRow,
  SlotTable,
  Stack,
  Text,
} from '@fonoteca/ui'
import { useDeferredValue, useState } from 'react'

import { apiBaseUrl } from '../api.ts'
import styles from './MatchingSubject.module.css'
import type {
  ArtistCandidate,
  MatchingQuestion,
  RecordingCandidate,
  ReleaseCandidate,
  SubjectFile,
} from './matchingFixtures.ts'

/**
 * One question, and every candidate answer to it.
 *
 * The three subjects — a set of files to a release, a file to a recording, a
 * recording to an artist — are one component tree with three option renderers.
 * That is the claim the component set makes, and this is where it is either true
 * or it is not.
 *
 * **It is the body of a dialog, not a page**, which is why it opens on the
 * evidence rather than on a heading and a back link. The dialog carries the
 * title, and `Escape`, the close button and the backdrop are the way out — three
 * of them, none of which had to be written. A second `<h1>` inside a modal is
 * the thing that makes it read as a page that forgot to navigate.
 *
 * Two things it deliberately does not do:
 *
 * - **It reads nothing.** The question is a fixture. No endpoint returns a
 *   candidate set with its fit metrics, because `ReleaseFit` is computed inside
 *   the attribution pass and discarded once it has ranked.
 * - **It writes nothing.** Committing shows what would have happened and says
 *   plainly that nothing was sent. A button that silently did nothing would be
 *   indistinguishable from a broken one. The real chooser next door *does*
 *   commit — `POST /api/catalogue/matching/recordings/{id}/decision` — and this
 *   one still cannot, because there is no file behind an invented question and
 *   no MBID that would resolve.
 */
export function MatchingSubject({ question }: { readonly question: MatchingQuestion }) {
  const [query, setQuery] = useState('')
  const deferred = useDeferredValue(query.trim())
  const [answer, setAnswer] = useState<CandidateSelection | null>(null)
  const [attempted, setAttempted] = useState(false)

  const options = optionsFor(question, deferred)

  return (
    <Stack direction="column" gap={24} className={styles.subject}>
      {/*
        Why it is open, then what settles it — the same pair the worklist shows
        on every refusal, so arriving here does not mean re-reading the first
        half and losing the second.
      */}
      <Stack direction="column" gap={8} align="start">
        <Text tone="secondary" block>
          {question.reason.note}
        </Text>
        {question.reason.next ? (
          <Text size="sm" tone="tertiary" block>
            {question.reason.next}
          </Text>
        ) : null}
      </Stack>

      <EvidencePanel
        heading={question.heading}
        subject={question.subject}
        subjectMono={question.subjectMono ?? false}
        {...(question.subjectNote != null ? { subjectNote: question.subjectNote } : {})}
        badges={<Badge tone={question.reason.tone}>{question.reason.label}</Badge>}
        rows={question.evidence}
      >
        {question.files.length > 0 ? <Listen files={question.files} /> : null}
      </EvidencePanel>

      <CandidateSearch
        label={SEARCH_LABEL[question.kind]}
        placeholder={SEARCH_PLACEHOLDER[question.kind]}
        value={query}
        onValueChange={setQuery}
        // The rendered count, not a prediction: `options` came out of the same
        // `filterCandidates` call, so the number in the live region and the rows
        // on the page cannot disagree.
        shown={options.length}
        total={question.candidates.length}
        busy={query.trim() !== deferred}
      />

      <CandidateList
        label={question.question}
        description={DESCRIPTION[question.kind]}
        value={answer}
        onSelect={(selection) => {
          setAnswer(selection)
          setAttempted(false)
        }}
        options={options}
        refusal={question.refusal}
        empty={
          <Text size="sm" tone="tertiary">
            No candidate matches that filter.
          </Text>
        }
        footer={
          <Stack direction="column" gap={6} align="start">
            <Stack gap={12} align="center" wrap>
              <Button
                variant="primary"
                size="sm"
                disabled={answer == null}
                onClick={() => setAttempted(true)}
              >
                {answer?.kind === 'none' ? 'Save “none of these”' : 'Save this match'}
              </Button>
              <Text size="xs" tone="tertiary">
                {answer == null
                  ? 'Pick one above, or “none of these”.'
                  : answer.kind === 'none'
                    ? question.ifRefused
                    : question.ifChosen}
              </Text>
            </Stack>

            {/*
              The answer goes nowhere, and the screen says so rather than
              pretending. A polite region because it appears in response to a
              click somewhere else on the page.
            */}
            <div role="status" aria-live="polite">
              {attempted ? (
                <Text size="xs" tone="warning" block>
                  Nothing was sent. This subject is a fixture — there is no file in the catalogue
                  behind it and no identifier that would resolve — so there is nothing to commit
                  against. A real question, opened from a row below, commits for real.
                </Text>
              ) : null}
            </div>
          </Stack>
        }
      />
    </Stack>
  )
}

/**
 * The files themselves, playable.
 *
 * On a library this deliberately tag-stripped, the audio is frequently the only
 * claim worth trusting — the folder name is the other one, and it is the one
 * that is wrong. So the controls sit in the evidence, above every candidate,
 * rather than being something to go looking for.
 */
function Listen({ files }: { readonly files: readonly SubjectFile[] }) {
  return (
    <Stack direction="column" gap={6} className={styles.listen}>
      <Text size="xs" tone="tertiary">
        Listen
      </Text>
      <ul className={styles.files} aria-label="Files">
        {files.map((file) => (
          <li key={file.path} className={styles.file}>
            <PlayButton size="sm" track={trackFor(file.path, basename(file.path))} />
            <Text size="xs" family="mono" tone="secondary" truncate>
              {basename(file.path)}
            </Text>
            <Text size="xs" family="mono" tone="tertiary">
              {file.fingerprint}
            </Text>
          </li>
        ))}
      </ul>
      <Text size="2xs" tone="tertiary">
        These ask for <code>{AUDIO_PATH}</code>, which does not exist yet — the store can already
        serve a byte range, but nothing exposes it and no response carries a media file id.
      </Text>
    </Stack>
  )
}

/* ------------------------------------------------------------- candidates */

function optionsFor(question: MatchingQuestion, query: string): readonly CandidateOption[] {
  switch (question.kind) {
    case 'release':
      return filterCandidates(question.candidates, query, (candidate) => ({
        title: candidate.title,
        artist: candidate.artist,
        ...(candidate.year != null ? { year: candidate.year } : {}),
        ...(candidate.country != null ? { country: candidate.country } : {}),
        formats: candidate.formats,
        extra: [candidate.status, candidate.label, candidate.catalogNumber].filter(
          (part): part is string => part != null,
        ),
      })).map(releaseOption)

    case 'recording':
      return filterCandidates(question.candidates, query, (candidate) => ({
        title: candidate.title,
        artist: candidate.artist,
        extra: [candidate.release, candidate.length, candidate.mbid],
      })).map(recordingOption)

    case 'artist':
      return filterCandidates(question.candidates, query, (candidate) => ({
        title: candidate.name,
        extra: [
          candidate.type,
          candidate.sortName,
          ...(candidate.disambiguation != null ? [candidate.disambiguation] : []),
        ],
      })).map(artistOption)
  }
}

function releaseOption(candidate: ReleaseCandidate): CandidateOption {
  return {
    id: candidate.id,
    render: (selection: CandidateOptionProps) => (
      <CandidateCard
        title={candidate.title}
        subtitle={candidate.artist}
        facts={[
          candidate.year?.toString(),
          candidate.country,
          candidate.status,
          candidate.formats,
          `${candidate.fit.trackCount} tracks`,
        ]
          .filter((part) => part != null)
          .join(' · ')}
        {...(candidate.catalogNumber != null
          ? { detail: `${candidate.label ?? '—'} · ${candidate.catalogNumber}` }
          : {})}
        selection={selection}
        fit={<FitSummary fit={candidate.fit} />}
        disclosureAside={
          <Badge tone="neutral" size="sm" mono>
            {candidate.fit.held} of {candidate.fit.trackCount}
          </Badge>
        }
      >
        <SlotTable
          caption={
            <Text size="xs" tone="tertiary">
              {candidate.title}
              {candidate.year != null ? ` (${candidate.year})` : ''} — printed against measured
            </Text>
          }
          rows={candidate.slots}
          renderPlay={(row) => <PlayButton size="sm" track={slotTrack(row)} />}
        />
      </CandidateCard>
    ),
  }
}

function recordingOption(candidate: RecordingCandidate): CandidateOption {
  return {
    id: candidate.id,
    render: (selection: CandidateOptionProps) => (
      <CandidateCard
        title={candidate.title}
        subtitle={`${candidate.artist} — ${candidate.release}`}
        facts={`${candidate.length} · score ${candidate.score} · ${candidate.sources} sources`}
        detail={candidate.mbid}
        selection={selection}
        fit={
          <Text size="xs" tone="secondary">
            {candidate.note}
          </Text>
        }
      />
    ),
  }
}

function artistOption(candidate: ArtistCandidate): CandidateOption {
  return {
    id: candidate.id,
    render: (selection: CandidateOptionProps) => (
      <CandidateCard
        title={candidate.name}
        subtitle={
          candidate.disambiguation != null
            ? `${candidate.type} — ${candidate.disambiguation}`
            : candidate.type
        }
        facts={`${candidate.trackCount.toLocaleString()} tracks held`}
        detail={`sorts as ${candidate.sortName}`}
        artworkShape="circle"
        selection={selection}
        fit={
          <Text size="xs" tone="secondary">
            {candidate.note}
          </Text>
        }
      />
    ),
  }
}

/* ---------------------------------------------------------------- playback */

/**
 * The endpoint these controls want.
 *
 * `IAudioFileStore.OpenRangeAsync` already exists and says in its own comment
 * that it is for "a future playback context", so the half that is missing is the
 * route in front of it and a media file id in the DTOs. Until then every play
 * control here resolves to a 404 and the player says so — which is the truthful
 * behaviour of an unwired screen, and better than a control that does nothing at
 * all.
 */
const AUDIO_PATH = '/api/library/audio'

function trackFor(path: string, title: string): PlaybackTrack {
  const mimeType = mimeTypeFor(path)

  return {
    id: path,
    // Keyed by path because no response carries a media file id yet. The real
    // one is `/api/library/files/{id}/audio`; a path in a query string is a
    // placeholder, not a proposal.
    src: `${apiBaseUrl}${AUDIO_PATH}?path=${encodeURIComponent(path)}`,
    title,
    subtitle: path,
    ...(mimeType != null ? { mimeType } : {}),
  }
}

function slotTrack(row: SlotRow): PlaybackTrack {
  const path = row.file?.path ?? ''
  return trackFor(path, row.title)
}

/**
 * Why bother, when everything here is a FLAC: `MediaError` reports a missing
 * file and an undecodable one with the same code, and the declared type is the
 * only thing that lets the player tell them apart before the request.
 */
function mimeTypeFor(path: string): string | undefined {
  const extension = path.slice(path.lastIndexOf('.') + 1).toLowerCase()
  return AUDIO_TYPES[extension]
}

const AUDIO_TYPES: Readonly<Record<string, string>> = {
  flac: 'audio/flac',
  mp3: 'audio/mpeg',
  m4a: 'audio/mp4',
  ogg: 'audio/ogg',
  opus: 'audio/ogg',
  wav: 'audio/wav',
}

function basename(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1)
}

/* ------------------------------------------------------------------ words */

const SEARCH_LABEL: Readonly<Record<MatchingQuestion['kind'], string>> = {
  release: 'Filter releases',
  recording: 'Filter recordings',
  artist: 'Filter artists',
}

const SEARCH_PLACEHOLDER: Readonly<Record<MatchingQuestion['kind'], string>> = {
  release: 'Title, year, country, catalogue number…',
  recording: 'Title, release, MBID…',
  artist: 'Name, type, disambiguation…',
}

const DESCRIPTION: Readonly<Record<MatchingQuestion['kind'], string>> = {
  release:
    'Coverage says whether the release is accounted for; drift says whether the audio agrees. A release that explains more files is not the same as one that fits.',
  recording:
    'One recording routinely sits under several AcoustID clusters, so two near-tied scores naming the same recording are one answer arriving twice.',
  artist:
    'A billed credit line is not the whole story. Conductors, orchestras and composers reach this list through relationships instead.',
}
