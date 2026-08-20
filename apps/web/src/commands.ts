import { type Command, useTheme } from '@fonoteca/ui'
import { useNavigate } from '@tanstack/react-router'
import { useMemo } from 'react'

import { NAV_ITEMS } from './navigation.ts'

/**
 * What the command bar can do today: go to a section, or set the theme.
 *
 * Deliberately only what works with no server behind it. Searching the
 * catalogue from here is the obvious next entry and it is not written yet —
 * there is no endpoint that answers "artists and albums matching this", and a
 * command bar that quietly searches four page names while looking like it
 * searches 100,000 tracks is worse than one that plainly does not.
 *
 * The theme commands are the way back to "follow the system", which
 * `ThemeSwitch` can leave but not return to — see its own note. That is the
 * "settings screen offering it in words" that comment anticipated.
 */
export function useAppCommands(): readonly Command[] {
  const navigate = useNavigate()
  const { setSetting } = useTheme()

  return useMemo(
    () => [
      ...NAV_ITEMS.map(
        (item): Command => ({
          id: `go:${item.to}`,
          label: item.label,
          kind: 'Go to',
          keywords: item.keywords,
          onSelect: () => {
            void navigate({ to: item.to })
          },
        }),
      ),
      {
        id: 'theme:light',
        label: 'Theme — light',
        kind: 'Action',
        keywords: ['appearance', 'colour'],
        onSelect: () => {
          setSetting('light')
        },
      },
      {
        id: 'theme:dark',
        label: 'Theme — dark',
        kind: 'Action',
        keywords: ['appearance', 'colour', 'night'],
        onSelect: () => {
          setSetting('dark')
        },
      },
      {
        id: 'theme:system',
        label: 'Theme — follow the system',
        kind: 'Action',
        keywords: ['appearance', 'colour', 'auto'],
        onSelect: () => {
          setSetting('system')
        },
      },
    ],
    [navigate, setSetting],
  )
}
