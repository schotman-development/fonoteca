import { type components, describeError } from '@fonoteca/api-client'
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

import { type Role, useAccount, useCan, useReloadAccount } from '../AccountGate.tsx'
import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import own from './AccountsPage.module.css'
import styles from './BrowsePage.module.css'

type AccountResponse = components['schemas']['AccountResponse']

/**
 * What each of OpenSubsonic's roles lets an account do here, in its order.
 * A record, so a role the API gains is a compile error until it is described.
 */
const ROLES: Readonly<Record<Role, { readonly name: string; readonly does: string }>> = {
  Admin: { name: 'Admin', does: 'accounts, the passes, files, tags, Identify, follows and Qobuz' },
  Settings: { name: 'Settings', does: 'their own password and API keys' },
  Download: { name: 'Download', does: 'music as files' },
  Upload: { name: 'Upload', does: 'files into the library' },
  Playlist: { name: 'Playlists', does: 'no effect: playlists are everyone’s' },
  CoverArt: { name: 'Covers', does: 'covers, pictures and corrections to albums and artists' },
  Comment: { name: 'Comments and ratings', does: 'nothing here uses it' },
  Podcast: { name: 'Podcasts', does: 'nothing here uses it' },
  Stream: { name: 'Play', does: 'music, here and in apps' },
  Jukebox: { name: 'Jukebox', does: 'nothing here uses it' },
  Share: { name: 'Sharing', does: 'nothing here uses it' },
  VideoConversion: { name: 'Video conversion', does: 'nothing here uses it' },
}

const EVERY_ROLE = Object.keys(ROLES) as Role[]

/** OpenSubsonic's `createUser` defaults, which the API gives an account added without roles too. */
const DEFAULT_ROLES: readonly Role[] = ['Settings', 'Stream']

/**
 * Accounts, and the signed-in account's own password and Subsonic API keys
 * (ADR 0018).
 *
 * What an account may do is its roles, OpenSubsonic's, on the web as in a
 * Subsonic app. An admin manages every account; anybody else sees their own.
 * What an account owns is its hearts and its keys; the library and the
 * playlists are everyone's, which the page says so nobody looks here for a
 * setting that is not per person.
 */
export function AccountsPage() {
  const me = useAccount()
  const admin = useCan('Admin')
  const settings = useCan('Settings', 'Admin')

  return (
    <Stack direction="column" gap={24}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Accounts
          </Text>
        </h1>
        <Text tone="secondary" block>
          An account signs in here and in Subsonic apps with the same name and password, and may do
          what its roles say, there as here. The library and its playlists are everyone's; what is
          an account's own is its hearts and its API keys. An admin adds, changes and removes
          accounts.
        </Text>
      </Stack>

      <Stack direction="column" gap={4}>
        <Stack gap={8} align="center" wrap>
          <Text>
            Signed in as <Text weight="semibold">{me?.name ?? '…'}</Text>
          </Text>
          {settings && me !== undefined ? <PasswordButton account={me} own /> : null}
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              void api.post('/api/account/logout').then(() => window.location.reload())
            }
          >
            Sign out
          </Button>
        </Stack>
        {me !== undefined ? (
          <Text size="sm" tone="secondary" block>
            {me.roles.length === 0
              ? 'Your account holds no roles: it may browse the library and nothing more.'
              : `Your roles: ${me.roles.map((role) => ROLES[role].name).join(', ')}.`}
          </Text>
        ) : null}
      </Stack>

      {admin ? <Everyone me={me} /> : null}

      {settings ? <ApiKeys /> : null}
    </Stack>
  )
}

/** Every account, for an admin to add, change and remove. */
function Everyone({ me }: { readonly me: AccountResponse | undefined }) {
  const [version, setVersion] = useState(0)
  const reloadMe = useReloadAccount()
  const reload = () => {
    setVersion((n) => n + 1)
    reloadMe()
  }
  const accounts = useApiQuery(() => api.get('/api/accounts'), [version])
  const admins =
    accounts.status === 'ready'
      ? accounts.data.filter((account) => account.roles.includes('Admin')).length
      : 0

  return (
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
                <TableHeaderCell>Roles</TableHeaderCell>
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
                    {me?.id === account.id ? (
                      <>
                        {' '}
                        <Badge size="sm">you</Badge>
                      </>
                    ) : null}
                    {account.email != null ? (
                      <Text size="sm" tone="secondary" block>
                        {account.email}
                      </Text>
                    ) : null}
                  </TableCell>
                  <TableCell>
                    <Text size="sm" tone="secondary">
                      {account.roles.length === 0
                        ? 'None'
                        : account.roles.map((role) => ROLES[role].name).join(', ')}
                    </Text>
                  </TableCell>
                  <TableCell>
                    <Text size="sm" tone="secondary">
                      {new Date(account.createdUtc).toLocaleDateString()}
                    </Text>
                  </TableCell>
                  <TableCell>
                    <AccountActions
                      account={account}
                      lastAdmin={admins === 1 && account.roles.includes('Admin')}
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
  )
}

/** A checkbox per role, in OpenSubsonic's order. */
function RolePicker({
  roles,
  onChange,
}: {
  readonly roles: readonly Role[]
  readonly onChange: (roles: readonly Role[]) => void
}) {
  return (
    <fieldset className={own.roles}>
      <legend>
        <Text size="sm" weight="medium">
          Roles
        </Text>
      </legend>
      {EVERY_ROLE.map((role) => (
        <label key={role} className={own.check}>
          <input
            type="checkbox"
            checked={roles.includes(role)}
            onChange={(event) =>
              onChange(
                event.currentTarget.checked
                  ? EVERY_ROLE.filter((each) => each === role || roles.includes(each))
                  : roles.filter((each) => each !== role),
              )
            }
          />
          <Text size="sm">
            {ROLES[role].name}: {ROLES[role].does}
          </Text>
        </label>
      ))}
    </fieldset>
  )
}

/** Sets a password: the signed-in account's own, or, for an admin, anybody's. */
function PasswordButton({
  account,
  own: isOwn,
}: {
  readonly account: AccountResponse
  readonly own?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const close = () => {
    setOpen(false)
    setPassword('')
    setError(null)
  }

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      await (isOwn
        ? api.post('/api/account/password', { json: { password } })
        : api.post('/api/accounts/{id}/password', {
            params: { path: { id: account.id } },
            json: { password },
          }))
      close()
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Button size="sm" variant="ghost" onClick={() => setOpen(true)}>
        {isOwn ? 'Change your password' : 'Set password'}
      </Button>
      <Dialog
        open={open}
        onClose={close}
        title={isOwn ? 'A new password for you' : `A new password for ${account.name}`}
        description="For the web and Subsonic apps alike. Other sign-ins end within a minute."
        footer={
          <Stack gap={8} justify="end">
            <Button variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={busy || password === ''}
              onClick={() => void save()}
            >
              Set password
            </Button>
          </Stack>
        }
      >
        <Field label="Password" hint="At least 16 characters." error={error ?? undefined}>
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
    </>
  )
}

function AccountActions({
  account,
  lastAdmin,
  onChanged,
}: {
  readonly account: AccountResponse
  readonly lastAdmin: boolean
  readonly onChanged: () => void
}) {
  const [dialog, setDialog] = useState<'roles' | 'remove' | null>(null)
  const [roles, setRoles] = useState<readonly Role[]>(account.roles)
  const [email, setEmail] = useState(account.email ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const close = () => {
    setDialog(null)
    setError(null)
  }

  // From the account as it is now, which a save elsewhere may have changed.
  const editRoles = () => {
    setRoles(account.roles)
    setEmail(account.email ?? '')
    setDialog('roles')
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
      <Button size="sm" variant="ghost" onClick={editRoles}>
        Roles
      </Button>
      <PasswordButton account={account} />
      <Button size="sm" variant="ghost" disabled={lastAdmin} onClick={() => setDialog('remove')}>
        Remove
      </Button>

      <Dialog
        open={dialog === 'roles'}
        onClose={close}
        title={`What ${account.name} may do`}
        description="A change reaches their other sign-ins within a minute. There is always one admin."
        footer={
          <Stack gap={8} justify="end">
            <Button variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={busy}
              onClick={() =>
                void run(() =>
                  api.post('/api/accounts/{id}', { ...params, json: { roles: [...roles], email } }),
                )
              }
            >
              Save
            </Button>
          </Stack>
        }
      >
        <Stack direction="column" gap={12}>
          <Field label="Email" hint="Optional; Subsonic apps show it, nothing here sends mail.">
            {(control) => (
              <Input
                {...control}
                type="email"
                autoComplete="off"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
              />
            )}
          </Field>
          <RolePicker roles={roles} onChange={setRoles} />
          {error != null ? (
            <Text size="sm" tone="danger" role="alert" block>
              {error}
            </Text>
          ) : null}
        </Stack>
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
  const [email, setEmail] = useState('')
  const [roles, setRoles] = useState<readonly Role[]>(DEFAULT_ROLES)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const add = async () => {
    setBusy(true)
    setError(null)
    try {
      await api.post('/api/accounts', {
        json: { name: name.trim(), password, email: email.trim() || null, roles: [...roles] },
      })
      setName('')
      setPassword('')
      setEmail('')
      setRoles(DEFAULT_ROLES)
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
          <Field label="Email" hint="Optional.">
            {(control) => (
              <Input
                {...control}
                type="email"
                autoComplete="off"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
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
        <RolePicker roles={roles} onChange={setRoles} />
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
