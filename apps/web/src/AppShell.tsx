import { Badge, Stack, Text } from '@fonoteca/ui'
import type { ReactNode } from 'react'

import styles from './AppShell.module.css'
import { HealthPanel } from './components/HealthPanel.tsx'
import { JobsPanel } from './components/JobsPanel.tsx'
import { ThemeToggle } from './components/ThemeToggle.tsx'

export function AppShell() {
  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <div className={styles.brand}>
          <Text size="lg" weight="semibold">
            Fonoteca
          </Text>
          <Badge tone="neutral" size="sm">
            scaffold
          </Badge>
        </div>
        <ThemeToggle />
      </header>

      {/*
        Where the router's outlet will go. Deliberately not wired: choosing a
        router is still an open decision, and adding one here would settle it by
        default rather than on purpose.
      */}
      <main className={styles.main}>
        <Stack direction="column" gap={24}>
          <Stack direction="column" gap={4}>
            <Text size="xl" weight="semibold" block>
              Foundation
            </Text>
            <Text tone="secondary" block>
              Everything below is a live check that one seam of the scaffold works. No product
              features exist yet.
            </Text>
          </Stack>

          <div className={styles.grid}>
            <Card title="API — via generated client">
              <HealthPanel />
            </Card>

            <Card title="Realtime — SignalR">
              <JobsPanel />
            </Card>

            <Card title="Design system">
              <Stack direction="column" gap={12}>
                <Text size="sm" tone="secondary" block>
                  Components come from <code>@fonoteca/ui</code>, styled entirely with tokens from{' '}
                  <code>@fonoteca/tokens</code>. Toggling the theme above re-renders every one of
                  them, including this card.
                </Text>
                <Stack gap={6} wrap>
                  <Badge tone="success" mono>
                    FLAC 24/96
                  </Badge>
                  <Badge tone="neutral" mono>
                    MP3 320
                  </Badge>
                  <Badge tone="warning">Upgrade available</Badge>
                  <Badge tone="danger">3 duplicates</Badge>
                </Stack>
              </Stack>
            </Card>
          </div>
        </Stack>
      </main>

      {/*
        Reserved for the playback transport bar. Empty today and collapsed to
        zero height; it exists so a future player does not require re-nesting
        the layout. See the plan's "Designed for, not built" section.
      */}
      <div className={styles.transport} data-slot="transport" />
    </div>
  )
}

function Card({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className={styles.card}>
      <div className={styles.cardTitle}>
        <Text size="sm" weight="semibold" tone="secondary">
          {title}
        </Text>
      </div>
      {children}
    </section>
  )
}
