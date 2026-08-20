/**
 * @fonoteca/ui — Fonoteca's design system.
 *
 * Runtime dependencies: `react`, `react-dom`, and the sibling token package.
 * Nothing else. No headless primitives, no positioning library, no focus-trap
 * helper, no virtualizer — every behaviour is implemented here.
 *
 * That is a deliberate constraint with a real cost: the hard components
 * (dialog, popover, combobox, menu, virtualized table) each carry accessibility
 * work that a primitives library would otherwise provide. Each of those gets an
 * ADR in `docs/adr/` naming the WAI-ARIA pattern it implements, and is built
 * when a screen actually needs it rather than speculatively.
 *
 * Consumers must import the stylesheets once, at the app root:
 *
 *   import '@fonoteca/tokens/tokens.css'
 *   import '@fonoteca/ui/reset.css'
 */

export * from './Artwork/index.ts'
export * from './AudioTransport/index.ts'
export * from './Badge/index.ts'
export * from './Button/index.ts'
export * from './Card/index.ts'
export * from './CatalogueCard/index.ts'
export * from './CommandBar/index.ts'
export * from './Dialog/index.ts'
export * from './Disclosure/index.ts'
export * from './Field/index.ts'
export * from './Input/index.ts'
export * from './Match/index.ts'
export * from './PlayButton/index.ts'
export * from './playback/index.ts'
export * from './Slider/index.ts'
export * from './Stack/index.ts'
export * from './Table/index.ts'
export * from './Text/index.ts'
export * from './ThemeSwitch/index.ts'
export * from './theme/index.ts'
export * from './VisuallyHidden/index.ts'
