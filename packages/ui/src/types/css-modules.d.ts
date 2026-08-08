/**
 * CSS Modules typing.
 *
 * `vite/client` ships an equivalent declaration, but this package sets
 * `types: []` so that no global type package is pulled in implicitly — the
 * design system should compile without knowing it will be bundled by Vite.
 */
declare module '*.module.css' {
  const classes: Readonly<Record<string, string>>
  export default classes
}

declare module '*.css' {
  const content: string
  export default content
}
