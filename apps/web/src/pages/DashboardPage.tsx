import { Card, Stack, Text } from '@fonoteca/ui'
import { useReducer } from 'react'

import { AttributionPanel } from '../components/AttributionPanel.tsx'
import { EnrichmentPanel } from '../components/EnrichmentPanel.tsx'
import { HealthPanel } from '../components/HealthPanel.tsx'
import { IdentificationPanel } from '../components/IdentificationPanel.tsx'
import { JobsPanel } from '../components/JobsPanel.tsx'
import { LibraryScanPanel } from '../components/LibraryScanPanel.tsx'
import { MusicBrainzPanel } from '../components/MusicBrainzPanel.tsx'
import { ProbePanel } from '../components/ProbePanel.tsx'
import { TagWritePanel } from '../components/TagWritePanel.tsx'
import styles from './DashboardPage.module.css'

/**
 * The scaffold's dashboard: one card per seam that has to keep working.
 *
 * Unchanged in substance by the arrival of a router — it moved out of
 * `AppShell` and became a route, which is what the outlet comment there was
 * waiting for.
 */
export function DashboardPage() {
  // The one piece of cross-panel state: a scan changes the catalogue, and the
  // panel showing its row counts has no other way to learn that. This is what a
  // server-state library would do properly; see the plan.
  const [catalogueVersion, catalogueChanged] = useReducer((n: number) => n + 1, 0)

  return (
    <Stack direction="column" gap={24}>
      <Stack direction="column" gap={4}>
        <Text size="xl" weight="semibold" block>
          Foundation
        </Text>
        <Text tone="secondary" block>
          Scan the library, identify what is in it, then ask MusicBrainz who made it. Everything
          below is a live check that one seam of the scaffold works.
        </Text>
      </Stack>

      <div className={styles.grid}>
        <Card title="Library — scan">
          <LibraryScanPanel onScanned={catalogueChanged} />
        </Card>

        {/*
          Next to the scan, because it is the pass that follows it: the scan
          says which files exist, this says what they are.
        */}
        <Card title="Library — identify">
          <IdentificationPanel />
        </Card>

        {/* And the one after that: what they are, in somebody's catalogue. */}
        <Card title="Library — enrich">
          <EnrichmentPanel onEnriched={catalogueChanged} />
        </Card>

        <Card title="Library — attribute">
          <AttributionPanel onAttributed={catalogueChanged} />
        </Card>

        {/*
          Last of the passes and independent of the other three: it asks the
          bytes what they are rather than asking anybody what the music is, so
          it neither needs nor feeds identification. What it does feed is the
          upgrade list, which cannot see a CD rip against a hi-res master until
          something has measured the depth and rate.
        */}
        <Card title="Library — measure">
          <ProbePanel onProbed={catalogueChanged} />
        </Card>

        {/*
          After all five, and deliberately last: the four above write to a
          database that can be dropped and rebuilt from the audio, and this one
          rewrites the audio. Nothing chains into it — it is the only card here
          whose work has no automatic path to it at all.
        */}
        <Card title="Library — write tags">
          <TagWritePanel />
        </Card>

        <Card title="API — via generated client">
          <HealthPanel refreshKey={catalogueVersion} />
        </Card>

        <Card title="MusicBrainz — identification seam">
          <MusicBrainzPanel />
        </Card>

        <Card title="Realtime — SignalR">
          <JobsPanel />
        </Card>
      </div>
    </Stack>
  )
}
