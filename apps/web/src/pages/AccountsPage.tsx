import { describeError } from '@fonoteca/api-client'
import {
  Badge,
  Button,
  Dialog,
  Field,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './BrowsePage.module.css'

/**
 * Accounts, and the signed-in account's Subsonic API keys (ADR 0018).
 *
 * Every account may do everything, so any of them manages all of them. What
 * an account owns is its hearts and its keys; the library and the playlists
 * are everyone's, which the page says so nobody looks here for a setting
 * that is not per person.
 */
export function AccountsPage() {
  const [version, setVersion] = useState(0)
  const reload = () => setVersion((n) => n + 1)
  const me = useApiQuery(() => api.get('/api/account'), [])
  const accounts = useApiQuery(() => api.get('/api/accounts'), [version])

  return (
    <Stack direction="column" gap={24}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Accounts
          </Text>
        </h1>
        <Text tone="secondary" block>
          An account signs in here and in Subsonic apps with the same name and password. The library
          and its playlists are everyone's; what is an account's own is its hearts and its API keys.
          Every account may add, reset and remove the others.
        </Text>
      </Stack>

      <Stack gap={8} align="center" wrap>
        <Text>
          Signed in as <Text weight="semibold">{me.status === 'ready' ? me.data.name : '…'}</Text>
        </Text>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void api.post('/api/account/logout').then(() => window.location.reload())}
        >
          Sign out
        </Button>
      </Stack>

      <section aria-labelledby="accounts-heading">
        <Stack direction="column" gap={12}>
          <h2 id="accounts-heading" className={styles.title}>
            <Text weight="semibold">Everyone</Text>
          </h2>
          <div role="status" aria-live="polite">
            {accounts.status === 'error' ? (
              <Badge tone="danger">Could not read the accounts: {accounts.message}</Badge>
            ) : null}
          </div>
          {accounts.status === 'ready' ? (
            <Table density="cozy">
              <thead>
                <tr>
                  <TableHeaderCell>Name</TableHeaderCell>
                  <TableHeaderCell>Since</TableHeaderCell>
                  <TableHeaderCell>
                    <VisuallyHidden>Actions</VisuallyHidden>
                  </TableHeaderCell>
                </tr>
              </thead>
              <tbody>
                {accounts.data.map((account) => (
                  <tr key={account.id}>
                    <TableCell>
                      <Text weight="medium">{account.name}</Text>
                      {me.status === 'ready' && me.data.id === account.id ? (
                        <>
                          {' '}
                          <Badge size="sm">you</Badge>
                        </>
                      ) : null}
                    </TableCell>
                    <TableCell>
                      <Text size="sm" tone="secondary">
                        {new Date(account.createdUtc).toLocaleDateString()}
                      </Text>
                    </TableCell>
                    <TableCell>
                      <AccountActions
                        account={account}
                        alone={accounts.data.length === 1}
                        onChanged={reload}
                      />
                    </TableCell>
                  </tr>
                ))}
              </tbody>
            </Table>
          ) : null}
          <AddAccount onAdded={reload} />
        </Stack>
      </section>

      <ApiKeys />
    </Stack>
  )
}

type Account = { readonly id: string; readonly name: string }

function AccountActions({
  account,
  alone,
  onChanged,
}: {
  readonly account: Account
  readonly alone: boolean
  readonly onChanged: () => void
}) {
  const [dialog, setDialog] = useState<'password' | 'remove' | null>(null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const close = () => {
    setDialog(null)
    setPassword('')
    setError(null)
  }

  const run = async (action: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
      close()
      onChanged()
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  const params = { params: { path: { id: account.id } } } as const

  return (
    <Stack gap={8} justify="end" wrap>
      <Button size="sm" variant="ghost" onClick={() => setDialog('password')}>
        Set password
      </Button>
      <Button size="sm" variant="ghost" disabled={alone} onClick={() => setDialog('remove')}>
        Remove
      </Button>

      <Dialog
        open={dialog === 'password'}
        onClose={close}
        title={`A new password for ${account.name}`}
        description="For the web and Subsonic apps alike. Their other sign-ins end within a minute."
        footer={
          <Stack gap={8} justify="end">
            <Button variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={busy || password === ''}
              onClick={() =>
                void run(() =>
                  api.post('/api/accounts/{id}/password', { ...params, json: { password } }),
                )
              }
            >
              Set password
            </Button>
          </Stack>
        }
      >
        <Field
          label="Password"
          hint="At least 6 characters, with an upper- and a lower-case letter, a digit and a symbol."
          error={error ?? undefined}
        >
          {(control) => (
            <Input
              {...control}
              type="password"
              autoComplete="new-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          )}
        </Field>
      </Dialog>

      <Dialog
        open={dialog === 'remove'}
        onClose={close}
        title={`Remove ${account.name}?`}
        description="Their hearts and API keys go with the account, for good. The library and the playlists stay."
        footer={
          <Stack gap={8} justify="end">
            <Button variant="ghost" onClick={close}>
              Keep it
            </Button>
            <Button
              variant="danger"
              disabled={busy}
              onClick={() => void run(() => api.delete('/api/accounts/{id}', params))}
            >
              Remove
            </Button>
          </Stack>
        }
      >
        {error != null ? (
          <Text size="sm" tone="danger" role="alert" block>
            {error}
          </Text>
        ) : null}
      </Dialog>
    </Stack>
  )
}

function AddAccount({ onAdded }: { readonly onAdded: () => void }) {
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const add = async () => {
    setBusy(true)
    setError(null)
    try {
      await api.post('/api/accounts', { json: { name: name.trim(), password } })
      setName('')
      setPassword('')
      onAdded()
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        void add()
      }}
    >
      <Stack direction="column" gap={8}>
        <Stack gap={8} align="end" wrap>
          <Field label="New account">
            {(control) => (
              <Input
                {...control}
                value={name}
                autoComplete="off"
                autoCapitalize="none"
                onChange={(event) => setName(event.target.value)}
              />
            )}
          </Field>
          <Field label="Password">
            {(control) => (
              <Input
                {...control}
                type="password"
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            )}
          </Field>
          <Button
            type="submit"
            variant="secondary"
            disabled={busy || name.trim() === '' || password === ''}
          >
            Add
          </Button>
        </Stack>
        {error != null ? (
          <Text size="sm" tone="danger" role="alert" block>
            {error}
          </Text>
        ) : null}
      </Stack>
    </form>
  )
}

/**
 * OpenSubsonic's API keys, for an app that signs in with one instead of a
 * password. A key is shown once, at its making: only its hash is kept.
 */
function ApiKeys() {
  const [version, setVersion] = useState(0)
  const keys = useApiQuery(() => api.get('/api/account/keys'), [version])
  const [name, setName] = useState('')
  const [made, setMade] = useState<{ readonly name: string; readonly key: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const run = async (action: () => Promise<void>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
      setVersion((n) => n + 1)
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section aria-labelledby="keys-heading">
      <Stack direction="column" gap={12}>
        <Stack direction="column" gap={4}>
          <h2 id="keys-heading" className={styles.title}>
            <Text weight="semibold">Your API keys</Text>
          </h2>
          <Text size="sm" tone="secondary" block>
            For a Subsonic app that offers to sign in with an API key rather than a password, such
            as Symfonium. A key signs in as you until it is revoked, and is shown only once.
          </Text>
        </Stack>

        {made != null ? (
          <Stack direction="column" gap={4}>
            <Text size="sm">
              The key for <Text weight="semibold">{made.name}</Text>; copy it now, it is not shown
              again:
            </Text>
            <Text family="mono" size="sm" block>
              {made.key}
            </Text>
          </Stack>
        ) : null}

        {keys.status === 'ready' && keys.data.length > 0 ? (
          <Table density="cozy">
            <thead>
              <tr>
                <TableHeaderCell>Name</TableHeaderCell>
                <TableHeaderCell>Made</TableHeaderCell>
                <TableHeaderCell>
                  <VisuallyHidden>Actions</VisuallyHidden>
                </TableHeaderCell>
              </tr>
            </thead>
            <tbody>
              {keys.data.map((key) => (
                <tr key={key.id}>
                  <TableCell>
                    <Text weight="medium">{key.name}</Text>
                  </TableCell>
                  <TableCell>
                    <Text size="sm" tone="secondary">
                      {new Date(key.createdUtc).toLocaleDateString()}
                    </Text>
                  </TableCell>
                  <TableCell>
                    <Stack justify="end">
                      <Button
                        size="sm"
                        variant="ghost"
                        disabled={busy}
                        onClick={() =>
                          void run(() =>
                            api.delete('/api/account/keys/{id}', {
                              params: { path: { id: key.id } },
                            }),
                          )
                        }
                      >
                        Revoke
                      </Button>
                    </Stack>
                  </TableCell>
                </tr>
              ))}
            </tbody>
          </Table>
        ) : null}

        <form
          onSubmit={(event) => {
            event.preventDefault()
            void run(async () => {
              const created = await api.post('/api/account/keys', { json: { name: name.trim() } })
              setMade({ name: created.name, key: created.key })
              setName('')
            })
          }}
        >
          <Stack gap={8} align="end" wrap>
            <Field label="A new key" hint="Name it for the app or device, to know which to revoke.">
              {(control) => (
                <Input
                  {...control}
                  value={name}
                  maxLength={100}
                  placeholder="Phone"
                  onChange={(event) => setName(event.target.value)}
                />
              )}
            </Field>
            <Button type="submit" variant="secondary" disabled={busy || name.trim() === ''}>
              Make key
            </Button>
          </Stack>
        </form>

        {error != null ? (
          <Text size="sm" tone="danger" role="alert" block>
            {error}
          </Text>
        ) : null}
      </Stack>
    </section>
  )
}
