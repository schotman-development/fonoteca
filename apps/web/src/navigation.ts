/**
 * The sections of the application, in the order they are shown.
 *
 * One list, read twice — by the rail in `AppShell` and by the command bar's
 * "Go to" commands — because two hand-written copies of a navigation list is
 * how a new screen ends up reachable from one of them and not the other.
 *
 * `to` is a union of literals rather than `string` on purpose: that is what
 * lets `Link` and `navigate` keep checking the paths against the route tree.
 * A new section is a compile error here until the route exists.
 */
export type NavItem = {
  readonly to: '/' | '/library' | '/library/releases' | '/library/matching' | '/files' | '/acquire'
  readonly label: string
  /**
   * Whether the link is current only on exactly this path.
   *
   * True where a longer path belongs to a *different* section: every path
   * starts with "/", and `/library/releases` starts with `/library`, so without
   * this the dashboard link would be current everywhere and the artists link
   * would light up on the albums page.
   */
  readonly exact: boolean
  /**
   * Searched by the command bar, not shown anywhere. Required rather than
   * optional so a new section has to answer "what would someone type to find
   * this?" — the labels alone are four words, and "releases" is not one of them.
   */
  readonly keywords: readonly string[]
}

export const NAV_ITEMS: readonly NavItem[] = [
  { to: '/', label: 'Foundation', exact: true, keywords: ['dashboard', 'scan', 'health'] },
  { to: '/library', label: 'Artists', exact: true, keywords: ['library', 'people', 'composers'] },
  { to: '/library/releases', label: 'Albums', exact: false, keywords: ['releases', 'catalogue'] },
  // Not exact, though nothing sits below it today — a question opens in a
  // dialog rather than at a path of its own. Left as it is because the rail
  // behaves identically either way here, and the next screen under /identify
  // would otherwise unhighlight the section it belongs to.
  {
    to: '/library/matching',
    label: 'Identify',
    exact: false,
    keywords: ['matching', 'questions', 'unmatched'],
  },
  // The disk, rather than the catalogue. It sits after the three catalogue
  // screens because it is where somebody goes when one of them is wrong about
  // something — a duplicate folder, a stray rip — and before Acquire because
  // nothing on it spends money.
  {
    to: '/files',
    label: 'Files',
    exact: false,
    keywords: ['files', 'folders', 'disk', 'upload', 'delete', 'trash', 'rename'],
  },
  // Last, and separate from the three above it in more than order: those browse
  // and correct what is already here, this one spends money on something that
  // is not.
  {
    to: '/acquire',
    label: 'Acquire',
    exact: true,
    keywords: ['qobuz', 'download', 'buy', 'purchase', 'acquire'],
  },
]
