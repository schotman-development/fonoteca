/**
 * The identify picker — build spec §6.4, and the escape hatch from the review
 * list.
 *
 * It exists because the endpoint asks for `984f8239-8fe1-4683-9c54-10ffb14439e9`
 * and the only people who can answer that are the ones who never needed the
 * review list. `Enricher.candidates()` searches the source on the person's
 * behalf and this renders the results as cards — artwork, title, subtitle,
 * detail and the upstream's own disambiguation, which is the field that
 * actually settles it.
 *
 * Two rules ride with it:
 *
 *  - **It is a `Drawer` mounted outside the polled list's tree.** The review
 *    list refetches every 15s; a picker inside it would unmount under somebody
 *    halfway through choosing.
 *  - **Nothing here auto-resolves.** The candidate search is the only search in
 *    the enrichment layer and only a person may read it — taking the top row
 *    would be exactly the guess `matching.py` refuses to make, written into
 *    every file on disk as `MUSICBRAINZ_ALBUMID`.
 *
 * A pasted musicbrainz.org URL is a valid `external_id`: the server takes the
 * id out of the link before validating it, because a link is what somebody
 * looking at the record actually has.
 */

import { useEffect, useState } from 'react'

import { useEnrichmentCandidates, useIdentifyEntity } from '@/api/queries'
import {
  Artwork,
  Button,
  Drawer,
  Field,
  PageError,
  PageLoading,
  TextInput,
  initialsOf,
  useToast,
} from '@/design'
import { EM_DASH } from '@/format'

import styles from '@/screens/identify/Identify.module.css'

export interface IdentifyTarget {
  entityType: string
  entityId: string
  source: string
  name: string
}

export interface IdentifyPickerProps {
  target: IdentifyTarget | null
  onClose: () => void
  /** Called once a match is recorded, so the screen can advance. */
  onIdentified: () => void
}

export function IdentifyPicker({ target, onClose, onIdentified }: IdentifyPickerProps) {
  const toast = useToast()
  const [query, setQuery] = useState('')
  const [pasted, setPasted] = useState('')

  const candidates = useEnrichmentCandidates(
    target?.entityType ?? '',
    target?.entityId ?? '',
    target?.source ?? '',
    query === '' ? undefined : query,
    target !== null,
  )
  const identify = useIdentifyEntity()

  useEffect(() => {
    setQuery('')
    setPasted('')
  }, [target?.entityId, target?.source])

  function choose(externalId: string) {
    if (target === null || externalId.trim() === '') return
    identify.mutate(
      {
        entityType: target.entityType,
        entityId: target.entityId,
        payload: { source: target.source, external_id: externalId.trim() },
      },
      {
        onSuccess: (message) => {
          toast(message.message, 'ok')
          onIdentified()
          onClose()
        },
        // A 400 leaves the results in place so nobody is dropped back to an
        // empty search over a typo.
        onError: (error: Error) => toast(error.message, 'bad'),
      },
    )
  }

  return (
    <Drawer
      open={target !== null}
      onClose={onClose}
      kicker={`Search ${target?.source ?? ''}`}
      title={target?.name ?? 'Find a match'}
      closeLabel="Close the picker"
      footer={
        <div className={styles.actions}>
          <Button
            variant="primary"
            size="block"
            onClick={() => choose(pasted)}
            loading={identify.isPending}
            disabled={pasted.trim() === ''}
          >
            Use this id
          </Button>
        </div>
      }
    >
      <Field
        label="Search"
        hint="The query the server built is shown; edit it if the release is filed under another name."
      >
        {(control) => (
          <TextInput
            {...control}
            value={query === '' ? (candidates.data?.query ?? '') : query}
            onChange={(event) => setQuery(event.target.value)}
          />
        )}
      </Field>

      <Field
        label="…or paste an id"
        hint="A musicbrainz.org link works too — the id is taken out of it."
      >
        {(control) => (
          <TextInput
            {...control}
            mono
            value={pasted}
            placeholder="984f8239-8fe1-4683-9c54-10ffb14439e9"
            onChange={(event) => setPasted(event.target.value)}
          />
        )}
      </Field>

      {candidates.isError ? (
        <PageError message={candidates.error.message} />
      ) : candidates.isLoading ? (
        <PageLoading rows={3} title={false} label="Searching" />
      ) : (candidates.data?.items.length ?? 0) === 0 ? (
        <p className={styles.empty}>
          Nothing came back for that query. Widen it, or paste the id.
        </p>
      ) : (
        <div className={styles.candidates}>
          {candidates.data?.items.map((candidate) => (
            <button
              key={candidate.external_id}
              type="button"
              className={styles.candidate}
              onClick={() => choose(candidate.external_id)}
            >
              <Artwork
                seed={candidate.external_id}
                initials={initialsOf(candidate.title, 'chars')}
                shape="square"
                size={40}
                src={candidate.image_url}
              />
              <span className={styles.candidateBody}>
                <span className={styles.candidateTitle}>{candidate.title}</span>
                <span className={styles.candidateSub}>
                  {candidate.subtitle ?? EM_DASH}
                </span>
                <span className={`${styles.candidateDetail} mono`}>
                  {[candidate.detail, candidate.disambiguation]
                    .filter((part): part is string => part !== null && part !== '')
                    .join(' · ') || candidate.external_id}
                </span>
              </span>
            </button>
          ))}
        </div>
      )}
    </Drawer>
  )
}
