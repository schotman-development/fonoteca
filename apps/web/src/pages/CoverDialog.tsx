import { describeError } from '@fonoteca/api-client'
import { Badge, Button, Dialog, Stack, Text } from '@fonoteca/ui'
import { useState } from 'react'
import { api, apiBaseUrl } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './CoverDialog.module.css'
import { archiveImage } from './coverArt.ts'

/**
 * Choosing an album's cover: any image the Cover Art Archive holds for the
 * release, or a picture of one's own.
 *
 * Every archive image is offered, not only the ones typed `Front` — the right
 * sleeve is sometimes filed as something else, and the type is printed on
 * each so a booklet page is not mistaken for one.
 */
export function CoverDialog({
  releaseId,
  title,
  onClose,
  onChanged,
}: {
  readonly releaseId: string
  readonly title: string
  readonly onClose: () => void
  readonly onChanged: () => void
}) {
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/releases/{id}/cover/options', {
        params: { path: { id: releaseId } },
      }),
    [releaseId],
  )

  async function save(send: () => Promise<unknown>) {
    setSaving(true)
    setError(null)

    try {
      await send()
      onChanged()
      onClose()
    } catch (cause: unknown) {
      setError(describeError(cause))
    } finally {
      setSaving(false)
    }
  }

  function choose(imageId: number) {
    void save(() =>
      api.post('/api/catalogue/releases/{id}/cover/archive/{imageId}', {
        params: { path: { id: releaseId, imageId } },
      }),
    )
  }

  function upload(file: File) {
    void save(async () => {
      const response = await fetch(`${apiBaseUrl}/api/catalogue/releases/${releaseId}/cover`, {
        method: 'POST',
        // The picture's own type is the claim the API checks against its
        // allowlist, so here — unlike the file manager's upload — it is sent.
        headers: { 'Content-Type': file.type || 'application/octet-stream' },
        body: file,
      })

      if (!response.ok) {
        throw new Error(
          response.status === 400
            ? 'Not a picture this application will store — use a JPEG, PNG, GIF, WebP, BMP or AVIF under 10 MB.'
            : `The upload failed (${response.status}).`,
        )
      }
    })
  }

  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      title="Choose a cover"
      description={title}
      footer={
        <Stack gap={8} justify="end" align="center" wrap>
          <label className={styles.upload} data-disabled={saving ? '' : undefined}>
            <input
              type="file"
              accept="image/jpeg,image/png,image/gif,image/webp,image/bmp,image/avif"
              className={styles.fileInput}
              disabled={saving}
              onChange={(event) => {
                const file = event.currentTarget.files?.[0]
                if (file !== undefined) upload(file)
                event.currentTarget.value = ''
              }}
            />
            <Text size="sm">Upload a picture…</Text>
          </label>
          <Button variant="ghost" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
        </Stack>
      }
    >
      <Stack direction="column" gap={12}>
        <div role="status" aria-live="polite" aria-busy={state.status === 'loading' || saving}>
          {state.status === 'loading' ? (
            <Text tone="tertiary">Asking the Cover Art Archive…</Text>
          ) : null}
          {saving ? <Text tone="tertiary">Saving…</Text> : null}
          {state.status === 'error' ? (
            <Text size="sm" tone="danger" block>
              {state.message}
            </Text>
          ) : null}
          {error !== null ? (
            <Text size="sm" tone="danger" block>
              {error}
            </Text>
          ) : null}
        </div>

        {state.status === 'ready' && state.data.uploaded ? (
          <Text size="sm" tone="secondary" block>
            The cover now is a picture you uploaded.
          </Text>
        ) : null}

        {state.status === 'ready' && state.data.images.length === 0 ? (
          <Text tone="tertiary" block>
            {state.data.mbid == null
              ? 'This album has no MusicBrainz id, so the Cover Art Archive has nothing for it.'
              : 'The Cover Art Archive holds no images for this release.'}{' '}
            Upload one instead.
          </Text>
        ) : null}

        {state.status === 'ready' && state.data.mbid != null && state.data.images.length > 0 ? (
          <ul className={styles.grid}>
            {state.data.images.map((image) => {
              const current = image.id === state.data.chosen
              const label =
                [image.types.join(', '), image.comment].filter(Boolean).join(' — ') || 'Untyped'

              return (
                <li key={image.id}>
                  <button
                    type="button"
                    className={styles.option}
                    aria-pressed={current}
                    disabled={saving}
                    onClick={() => choose(image.id)}
                  >
                    <img
                      className={styles.thumb}
                      src={archiveImage(state.data.mbid ?? '', image.id)}
                      alt=""
                      loading="lazy"
                      referrerPolicy="no-referrer"
                    />
                    <Text size="xs" block>
                      {label}
                    </Text>
                    <Stack gap={4} wrap>
                      {current ? (
                        <Badge tone="success" size="sm">
                          Current
                        </Badge>
                      ) : null}
                      {image.front ? (
                        <Badge tone="neutral" size="sm">
                          Archive's front
                        </Badge>
                      ) : null}
                    </Stack>
                  </button>
                </li>
              )
            })}
          </ul>
        ) : null}
      </Stack>
    </Dialog>
  )
}
