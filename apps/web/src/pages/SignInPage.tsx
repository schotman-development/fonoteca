import { describeError } from '@fonoteca/api-client'
import { Button, Card, Field, Input, Stack, Text } from '@fonoteca/ui'
import { useState } from 'react'

import { api } from '../api.ts'
import styles from './SignInPage.module.css'

/**
 * Signing in, or — while no account exists — making the first one.
 *
 * The same name and password sign in to a Subsonic app, which the page says,
 * because that is the one thing about an account here a person could not
 * guess (ADR 0018).
 */
export function SignInPage({
  setUp,
  onSignedIn,
}: {
  readonly setUp: boolean
  readonly onSignedIn: () => void
}) {
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    setBusy(true)
    setError(null)
    try {
      const json = { name: name.trim(), password }
      await (setUp
        ? api.post('/api/account/setup', { json })
        : api.post('/api/account/login', { json }))
      onSignedIn()
    } catch (cause) {
      setError(describeError(cause))
      setBusy(false)
    }
  }

  return (
    <main className={styles.page}>
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault()
          void submit()
        }}
      >
        <Card padding="lg">
          <Stack direction="column" gap={16}>
            <Stack direction="column" gap={4}>
              <h1 className={styles.title}>
                <Text size="xl" weight="semibold" block>
                  {setUp ? 'Make your account' : 'Sign in to Fonoteca'}
                </Text>
              </h1>
              <Text size="sm" tone="secondary" block>
                {setUp
                  ? 'No account exists yet, so this one is the first. The same name and password sign in to Subsonic apps.'
                  : 'The same name and password sign in to Subsonic apps.'}
              </Text>
            </Stack>

            <Field label="Name" hint={setUp ? 'Letters, digits and - . _ @ +' : undefined}>
              {(control) => (
                <Input
                  {...control}
                  value={name}
                  autoComplete="username"
                  autoCapitalize="none"
                  required
                  onChange={(event) => setName(event.target.value)}
                />
              )}
            </Field>

            <Field label="Password" hint={setUp ? 'At least 16 characters.' : undefined}>
              {(control) => (
                <Input
                  {...control}
                  type="password"
                  value={password}
                  autoComplete={setUp ? 'new-password' : 'current-password'}
                  required
                  onChange={(event) => setPassword(event.target.value)}
                />
              )}
            </Field>

            {error != null ? (
              <Text size="sm" tone="danger" role="alert" block>
                {error}
              </Text>
            ) : null}

            <Button
              type="submit"
              variant="primary"
              disabled={busy || name.trim() === '' || password === ''}
            >
              {setUp ? 'Make account' : 'Sign in'}
            </Button>
          </Stack>
        </Card>
      </form>
    </main>
  )
}
