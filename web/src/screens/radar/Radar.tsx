/**
 * Release radar — design 528–586, build spec §6.3.
 *
 * Two columns: what has come out lately, and who is being watched.
 *
 * **The left column is ordered by release date** — `GET /api/releases/recent`,
 * which sorts on `Album.release_date` and is the only list in the API ordered
 * by when the music came out rather than by something this program did. It used
 * to be `useQueue()`, and that was the wrong list under the right heading: the
 * queue is ordered by the moment somebody pressed Download, so the top of a
 * page headed *New & upcoming* was whatever finished downloading most recently
 * — a 1975 remaster fetched this morning above a record released last week —
 * and a radar nobody had downloaded from was empty. The queue is still drawn on
 * the Download queue screen, where the ordering and the heading agree.
 *
 * The design's `Announced` (a release dated ahead) is reachable now and is not
 * faked: future dates sort first and `ReleaseRow` notes them as *out in N
 * days*. The size column is still dropped — no byte total exists per release.
 *
 * **The toggle writes `monitored`, and the footnote says so.** The design calls
 * it auto-download, which would be false twice over: `auto_download` is a
 * *global* opt-in that defaults to off, and monitoring an artist marks releases
 * wanted without queueing anything.
 *
 * `+ Follow artist` opens the command bar, which is where catalogue search
 * lives. There is no Add-artist screen any more and this is deliberately not a
 * second search box: two search boxes over one endpoint is two caches of one
 * data set.
 */

import {
  useArtists,
  useRecentReleases,
  useSettings,
  useStats,
  useUpdateArtist,
} from '@/api/queries'
import { Button, Eyebrow, PageError, PageLoading, useToast } from '@/design'
import { useCommandBar } from '@/shell'
import { FollowRow, ReleaseRow } from '@/widgets'

import styles from '@/screens/radar/Radar.module.css'

export default function Radar() {
  const toast = useToast()
  const commandBar = useCommandBar()

  const releases = useRecentReleases()
  const artists = useArtists()
  const stats = useStats()
  const settings = useSettings()
  const updateArtist = useUpdateArtist()

  const sweep = settings.data?.indexer_full_sweep_hours
  const library = stats.data?.library
  const lede =
    `Qobuz is the only download source` +
    (sweep === undefined ? '' : `, checked about every ${sweep} h`) +
    '. ' +
    (library === undefined
      ? 'Monitoring marks new releases wanted; downloading stays an explicit press.'
      : `${library.monitored_artists} of ${library.artists} artists monitored · new releases are marked wanted, never queued for you.`)

  const items = releases.data?.items ?? []

  return (
    <section className={styles.section} aria-labelledby="radar-title">
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <h1 className={styles.title} id="radar-title">
            Release radar
          </h1>
          <p className={styles.lede}>{lede}</p>
        </div>
        <Button onClick={commandBar.open}>+ Follow artist</Button>
      </div>

      <div className={styles.columns}>
        <div>
          <Eyebrow as="h2">New &amp; upcoming</Eyebrow>
          {releases.isError ? (
            <PageError
              message={releases.error.message}
              action={<Button onClick={() => void releases.refetch()}>Retry</Button>}
            />
          ) : releases.isLoading ? (
            <PageLoading rows={4} title={false} label="Loading recent releases" />
          ) : items.length === 0 ? (
            <p className={styles.empty}>
              No release carries a date yet. Follow an artist and the indexer fills this
              in on its next sweep.
            </p>
          ) : (
            <div className={styles.rows}>
              {items.map((album) => (
                <ReleaseRow key={album.id} album={album} />
              ))}
            </div>
          )}
        </div>

        <div className={styles.ruled}>
          <Eyebrow as="h2">Followed artists</Eyebrow>
          {artists.isError ? (
            <PageError
              message={artists.error.message}
              action={<Button onClick={() => void artists.refetch()}>Retry</Button>}
            />
          ) : artists.isLoading ? (
            <PageLoading rows={5} title={false} label="Loading followed artists" />
          ) : (artists.data?.items.length ?? 0) === 0 ? (
            <p className={styles.empty}>
              Nobody is followed yet. Press ⌘K and search the Qobuz catalogue.
            </p>
          ) : (
            <div className={styles.rows}>
              {artists.data?.items.map((artist) => (
                <FollowRow
                  key={artist.id}
                  artist={artist}
                  busy={updateArtist.isPending && updateArtist.variables.artistId === artist.id}
                  // `() => void`: the row hands over nothing, so it cannot hand
                  // over a filter value by mistake. The new state is computed
                  // here, from the artist this row is about.
                  onToggle={() => {
                    updateArtist.mutate(
                      { artistId: artist.id, payload: { monitored: !artist.monitored } },
                      {
                        onSuccess: (next) =>
                          toast(
                            `${next.name}: monitoring ${next.monitored ? 'on' : 'off'}`,
                            'neutral',
                          ),
                        onError: (error: Error) => toast(error.message, 'bad'),
                      },
                    )
                  }}
                />
              ))}
            </div>
          )}
          <p className={styles.footnote}>
            The switch controls monitoring. Off keeps the artist on the radar but every
            release has to be grabbed by hand.
          </p>
        </div>
      </div>
    </section>
  )
}
