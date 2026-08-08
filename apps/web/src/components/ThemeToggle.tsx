import { Button, Stack, type ThemeSetting, useTheme } from '@fonoteca/ui'

const SETTINGS: readonly { value: ThemeSetting; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'system', label: 'System' },
]

/**
 * Three options, not a two-way switch. "System" is a distinct state — it clears
 * the `data-theme` attribute so `prefers-color-scheme` decides — and it is the
 * default most people are on, so it needs to be reachable rather than merely
 * being what you get before touching anything.
 */
export function ThemeToggle() {
  const { setting, setSetting } = useTheme()

  return (
    <Stack gap={2} role="group" aria-label="Colour theme">
      {SETTINGS.map(({ value, label }) => (
        <Button
          key={value}
          size="sm"
          variant={setting === value ? 'primary' : 'ghost'}
          aria-pressed={setting === value}
          onClick={() => setSetting(value)}
        >
          {label}
        </Button>
      ))}
    </Stack>
  )
}
